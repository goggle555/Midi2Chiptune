using System.Text;

namespace Midi2Chiptune;

// WAVファイルのヘッダー構造体
public struct WaveHeader
{
    public byte[] ChunkID;        // "RIFF"
    public uint ChunkSize;
    public byte[] Format;         // "WAVE"
    public byte[] Subchunk1ID;    // "fmt "
    public uint Subchunk1Size;
    public ushort AudioFormat;
    public ushort NumChannels;
    public uint SampleRate;
    public uint ByteRate;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public byte[] Subchunk2ID;    // "data"
    public uint Subchunk2Size;
}

// MIDIイベント
public abstract class MidiEvent
{
    public int Time { get; set; }
}

public class NoteOnEvent : MidiEvent
{
    public int Channel { get; set; }
    public int Note { get; set; }
    public int Velocity { get; set; }
}

public class NoteOffEvent : MidiEvent
{
    public int Channel { get; set; }
    public int Note { get; set; }
}

public class ProgramChangeEvent : MidiEvent
{
    public int Channel { get; set; }
    public int Program { get; set; }
}

public class UnknownEvent : MidiEvent { }

// MIDIトラック
public class MidiTrack
{
    public List<MidiEvent> Events { get; set; } = new List<MidiEvent>();
}

// MIDIファイル
public class MidiFile
{
    public int Format { get; set; }
    public int TrackCount { get; set; }
    public int TicksPerQuarter { get; set; }
    public List<MidiTrack> Tracks { get; set; } = new List<MidiTrack>();
}

// 音符情報
public class Note
{
    public int MidiNote { get; set; }
    public int Channel { get; set; }
    public double StartTime { get; set; }
    public double Duration { get; set; }
    public int Velocity { get; set; }
}

// 矩形波のデューティサイクル（ファミコンの4種類）
public enum DutyCycle
{
    Duty12_5,  // 12.5%
    Duty25,    // 25%
    Duty50,    // 50%
    Duty75     // 75%
}

public class Midi2Chiptune
{
    // デューティサイクルの値を取得
    private static double GetDutyCycleValue(DutyCycle duty)
    {
        return duty switch
        {
            DutyCycle.Duty12_5 => 0.125,
            DutyCycle.Duty25 => 0.25,
            DutyCycle.Duty50 => 0.5,
            DutyCycle.Duty75 => 0.75,
            _ => 0.5
        };
    }

    // MIDIノート番号を周波数に変換
    private static double MidiNoteToFrequency(int midiNote)
    {
        return 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
    }

    // Variable Length Quantity (VLQ) の読み込み
    private static int ReadVLQ(BinaryReader reader)
    {
        int value = 0;
        int b;

        do
        {
            b = reader.ReadByte();
            value = (value << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);

        return value;
    }

    // ビッグエンディアンから変換
    private static ushort ReadBigEndianUInt16(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(2);
        Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }

    private static uint ReadBigEndianUInt32(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(4);
        Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    // MIDIヘッダーチャンクの読み込み
    private static (int format, int trackCount, int ticksPerQuarter) ReadMidiHeader(BinaryReader reader)
    {
        string chunkType = Encoding.ASCII.GetString(reader.ReadBytes(4));
        uint chunkLength = ReadBigEndianUInt32(reader);

        ushort format = ReadBigEndianUInt16(reader);
        ushort trackCount = ReadBigEndianUInt16(reader);
        ushort ticksPerQuarter = ReadBigEndianUInt16(reader);

        return (format, trackCount, ticksPerQuarter);
    }

    // MIDIイベントの解析
    private static (MidiEvent evt, int status) ParseMidiEvent(BinaryReader reader, int runningStatus, int deltaTime)
    {
        int status = runningStatus;
        byte firstByte = reader.ReadByte();

        if (firstByte >= 128)
        {
            status = firstByte;
        }
        else
        {
            reader.BaseStream.Seek(-1, SeekOrigin.Current);
        }

        switch (status & 0xF0)
        {
            case 0x90: // Note On
                {
                    int channel = status & 0x0F;
                    int note = reader.ReadByte();
                    int velocity = reader.ReadByte();

                    if (velocity == 0)
                    {
                        return (new NoteOffEvent { Channel = channel, Note = note, Time = deltaTime }, status);
                    }
                    else
                    {
                        return (new NoteOnEvent { Channel = channel, Note = note, Velocity = velocity, Time = deltaTime }, status);
                    }
                }
            case 0x80: // Note Off
                {
                    int channel = status & 0x0F;
                    int note = reader.ReadByte();
                    reader.ReadByte(); // velocity (ignored)
                    return (new NoteOffEvent { Channel = channel, Note = note, Time = deltaTime }, status);
                }
            case 0xC0: // Program Change
                {
                    int channel = status & 0x0F;
                    int program = reader.ReadByte();
                    return (new ProgramChangeEvent { Channel = channel, Program = program, Time = deltaTime }, status);
                }
            default:
                // その他のイベントはスキップ
                if (status == 0xFF) // Meta event
                {
                    reader.ReadByte(); // event type
                    int length = ReadVLQ(reader);
                    reader.ReadBytes(length);
                }
                else if (status >= 0x80)
                {
                    reader.ReadByte();
                    if ((status & 0xE0) != 0xC0 && (status & 0xE0) != 0xD0)
                    {
                        reader.ReadByte();
                    }
                }
                return (new UnknownEvent { Time = deltaTime }, status);
        }
    }

    // MIDIトラックの読み込み
    private static MidiTrack ReadMidiTrack(BinaryReader reader)
    {
        string chunkType = Encoding.ASCII.GetString(reader.ReadBytes(4));
        uint chunkLength = ReadBigEndianUInt32(reader);

        long endPosition = reader.BaseStream.Position + chunkLength;
        var events = new List<MidiEvent>();
        int runningStatus = 0;

        while (reader.BaseStream.Position < endPosition)
        {
            try
            {
                int deltaTime = ReadVLQ(reader);
                var (evt, newStatus) = ParseMidiEvent(reader, runningStatus, deltaTime);
                runningStatus = newStatus;

                if (!(evt is UnknownEvent))
                {
                    events.Add(evt);
                }
            }
            catch
            {
                if (reader.BaseStream.Position < endPosition)
                {
                    reader.ReadByte();
                }
            }
        }

        return new MidiTrack { Events = events };
    }

    // MIDIファイル全体の読み込み
    public static MidiFile? ReadMidiFile(string filename)
    {
        try
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);

            var (format, trackCount, ticksPerQuarter) = ReadMidiHeader(reader);

            var tracks = new List<MidiTrack>();
            for (int i = 0; i < trackCount; i++)
            {
                tracks.Add(ReadMidiTrack(reader));
            }

            return new MidiFile
            {
                Format = format,
                TrackCount = trackCount,
                TicksPerQuarter = ticksPerQuarter,
                Tracks = tracks
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MIDIファイルの読み込みエラー: {ex.Message}");
            return null;
        }
    }

    // MIDIイベントから音符リストに変換
    public static List<Note> EventsToNotes(MidiFile midiFile, double tempo)
    {
        double TicksToSeconds(int tick) => tick / (double)midiFile.TicksPerQuarter * 60.0 / tempo;

        var noteOnEvents = new Dictionary<(int channel, int note), (int velocity, double startTime)>();
        var notes = new List<Note>();

        foreach (var track in midiFile.Tracks)
        {
            int currentTick = 0;

            foreach (var evt in track.Events)
            {
                currentTick += evt.Time;
                double currentTime = TicksToSeconds(currentTick);

                switch (evt)
                {
                    case NoteOnEvent noteOn:
                        noteOnEvents[(noteOn.Channel, noteOn.Note)] = (noteOn.Velocity, currentTime);
                        break;

                    case NoteOffEvent noteOff:
                        var key = (noteOff.Channel, noteOff.Note);
                        if (noteOnEvents.TryGetValue(key, out var noteOnInfo))
                        {
                            var (velocity, startTime) = noteOnInfo;
                            double duration = currentTime - startTime;

                            if (duration > 0.0)
                            {
                                notes.Add(new Note
                                {
                                    MidiNote = noteOff.Note,
                                    Channel = noteOff.Channel,
                                    StartTime = startTime,
                                    Duration = duration,
                                    Velocity = velocity
                                });
                            }

                            noteOnEvents.Remove(key);
                        }
                        break;
                }
            }
        }

        return notes;
    }

    // 波形生成関数
    public static double[] GenerateSquareWave(double frequency, DutyCycle duty, int sampleRate, double duration)
    {
        int samples = (int)(sampleRate * duration);
        double dutyValue = GetDutyCycleValue(duty);
        var result = new double[samples];

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
            double phase = (t * frequency) % 1.0;
            result[i] = phase < dutyValue ? 1.0 : -1.0;
        }

        return result;
    }

    public static double[] GenerateTriangleWave(double frequency, int sampleRate, double duration)
    {
        int samples = (int)(sampleRate * duration);
        var result = new double[samples];

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
            double phase = (t * frequency) % 1.0;

            if (phase < 0.5)
            {
                result[i] = 4.0 * phase - 1.0;
            }
            else
            {
                result[i] = 3.0 - 4.0 * phase;
            }
        }

        return result;
    }

    public static double[] GenerateNoise(bool isShort, int sampleRate, double duration)
    {
        int samples = (int)(sampleRate * duration);
        var result = new double[samples];
        ushort shift = 1;

        for (int i = 0; i < samples; i++)
        {
            ushort bit0 = (ushort)(shift & 1);
            ushort bit1 = isShort ? (ushort)((shift >> 6) & 1) : (ushort)((shift >> 1) & 1);
            ushort feedback = (ushort)(bit0 ^ bit1);
            shift = (ushort)((shift >> 1) | (feedback << 14));

            result[i] = bit0 == 1 ? 1.0 : -1.0;
        }

        return result;
    }

    // 音符を波形に変換
    public static double[] NoteToWaveform(Note note, int sampleRate, double totalDuration)
    {
        double frequency = MidiNoteToFrequency(note.MidiNote);
        double volume = note.Velocity / 127.0 * 0.7;
        int startSample = (int)(note.StartTime * sampleRate);
        int noteSamples = (int)(note.Duration * sampleRate);
        int totalSamples = (int)(totalDuration * sampleRate);

        double[] waveform = (note.Channel % 4) switch
        {
            0 => GenerateSquareWave(frequency, DutyCycle.Duty50, sampleRate, note.Duration),
            1 => GenerateSquareWave(frequency, DutyCycle.Duty25, sampleRate, note.Duration),
            2 => GenerateTriangleWave(frequency, sampleRate, note.Duration),
            _ => GenerateNoise(false, sampleRate, note.Duration)
        };

        var result = new double[totalSamples];
        int endSample = Math.Min(startSample + noteSamples, totalSamples);

        for (int i = startSample; i < endSample; i++)
        {
            if (i - startSample < waveform.Length)
            {
                result[i] = waveform[i - startSample] * volume;
            }
        }

        return result;
    }

    // 複数の波形をミックス
    public static double[] MixWaveforms(double[][] waveforms)
    {
        if (waveforms.Length == 0) return new double[0];

        int maxLength = waveforms.Max(w => w.Length);
        var result = new double[maxLength];

        for (int i = 0; i < maxLength; i++)
        {
            double sum = 0;
            foreach (var waveform in waveforms)
            {
                if (i < waveform.Length)
                {
                    sum += waveform[i];
                }
            }
            result[i] = sum / waveforms.Length;
        }

        return result;
    }

    // 16ビット整数に変換
    private static short[] ConvertTo16Bit(double[] samples)
    {
        return samples.Select(s =>
        {
            double clamped = Math.Max(-1.0, Math.Min(1.0, s));
            return (short)(clamped * 32767.0);
        }).ToArray();
    }

    // WAVヘッダー作成
    private static WaveHeader CreateWaveHeader(int sampleRate, int numChannels, int bitsPerSample, int dataSize)
    {
        uint byteRate = (uint)(sampleRate * numChannels * bitsPerSample / 8);
        ushort blockAlign = (ushort)(numChannels * bitsPerSample / 8);

        return new WaveHeader
        {
            ChunkID = Encoding.ASCII.GetBytes("RIFF"),
            ChunkSize = (uint)(36 + dataSize),
            Format = Encoding.ASCII.GetBytes("WAVE"),
            Subchunk1ID = Encoding.ASCII.GetBytes("fmt "),
            Subchunk1Size = 16,
            AudioFormat = 1,
            NumChannels = (ushort)numChannels,
            SampleRate = (uint)sampleRate,
            ByteRate = byteRate,
            BlockAlign = blockAlign,
            BitsPerSample = (ushort)bitsPerSample,
            Subchunk2ID = Encoding.ASCII.GetBytes("data"),
            Subchunk2Size = (uint)dataSize
        };
    }

    // WAVファイル書き込み
    public static void WriteWaveFile(string filename, double[] samples, int sampleRate)
    {
        using var stream = new FileStream(filename, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        short[] sampleData = ConvertTo16Bit(samples);
        int dataSize = sampleData.Length * 2;
        var header = CreateWaveHeader(sampleRate, 1, 16, dataSize);

        // ヘッダー書き込み
        writer.Write(header.ChunkID);
        writer.Write(header.ChunkSize);
        writer.Write(header.Format);
        writer.Write(header.Subchunk1ID);
        writer.Write(header.Subchunk1Size);
        writer.Write(header.AudioFormat);
        writer.Write(header.NumChannels);
        writer.Write(header.SampleRate);
        writer.Write(header.ByteRate);
        writer.Write(header.BlockAlign);
        writer.Write(header.BitsPerSample);
        writer.Write(header.Subchunk2ID);
        writer.Write(header.Subchunk2Size);

        // サンプルデータ書き込み
        foreach (short sample in sampleData)
        {
            writer.Write(sample);
        }
    }

    // MIDIからWAV変換のメイン関数
    public static bool ConvertMidiToWav(string midiFilename, string wavFilename, double tempo = 120.0)
    {
        var midiFile = ReadMidiFile(midiFilename);
        if (midiFile == null)
        {
            Console.WriteLine("MIDIファイルの読み込みに失敗しました");
            return false;
        }

        Console.WriteLine($"MIDIファイルを読み込みました: Format {midiFile.Format}, {midiFile.TrackCount} tracks, {midiFile.TicksPerQuarter} ticks/quarter");

        var notes = EventsToNotes(midiFile, tempo);
        Console.WriteLine($"{notes.Count}個の音符を検出しました");

        if (notes.Count == 0)
        {
            Console.WriteLine("音符が見つかりませんでした");
            return false;
        }

        double totalDuration = notes.Max(n => n.StartTime + n.Duration) + 1.0; // 余裕を持たせる
        int sampleRate = 44100;

        Console.WriteLine($"総演奏時間: {totalDuration:F2}秒");
        Console.WriteLine("波形を生成中...");

        var waveforms = notes.Select(note => NoteToWaveform(note, sampleRate, totalDuration)).ToArray();
        var mixed = MixWaveforms(waveforms);

        WriteWaveFile(wavFilename, mixed, sampleRate);

        Console.WriteLine($"WAVファイル '{wavFilename}' を生成しました！");
        return true;
    }

    // デモ用のサンプル生成
    public static void GenerateDemoNESMusic()
    {
        int sampleRate = 44100;
        double duration = 2.0;

        // チャンネル1: 矩形波（メロディ）
        var square1 = GenerateSquareWave(440.0, DutyCycle.Duty50, sampleRate, duration)
                     .Select(s => s * 0.3).ToArray();

        // チャンネル2: 矩形波（ハーモニー）
        var square2 = GenerateSquareWave(330.0, DutyCycle.Duty25, sampleRate, duration)
                     .Select(s => s * 0.25).ToArray();

        // チャンネル3: 三角波（ベース）
        var triangle = GenerateTriangleWave(110.0, sampleRate, duration)
                      .Select(s => s * 0.4).ToArray();

        // ミックス
        var mixed = MixWaveforms(new[] { square1, square2, triangle });

        // WAVファイルに出力
        WriteWaveFile("demo_nes_sound.wav", mixed, sampleRate);
        Console.WriteLine("デモNES音源のWAVファイル 'demo_nes_sound.wav' を生成しました！");
    }
}

class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 1)
            {
                string midiFile = args[0];
                string outputFile = args.Length >= 2 ? args[1] : Path.ChangeExtension(midiFile, ".wav");
                double tempo = args.Length >= 3 ? double.Parse(args[2]) : 120.0;

                if (File.Exists(midiFile))
                {
                    return Midi2Chiptune.ConvertMidiToWav(midiFile, outputFile, tempo) ? 0 : 1;
                }
                else
                {
                    Console.WriteLine($"MIDIファイル '{midiFile}' が見つかりません");
                    return 1;
                }
            }
            else
            {
                Console.WriteLine("使用方法: program.exe <MIDIファイル> [出力WAVファイル] [テンポ]");
                Console.WriteLine("デモファイルを生成します...");
                Midi2Chiptune.GenerateDemoNESMusic();
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"エラーが発生しました: {ex.Message}");
            return 1;
        }
    }
}

