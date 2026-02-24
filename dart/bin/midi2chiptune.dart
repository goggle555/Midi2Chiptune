// MIDIをNES(ファミコン)風チップチューンWAVに変換するCLI
// dart run midi2chiptune <MIDIファイル> [出力WAVファイル] [テンポ]

import 'dart:io';
import 'dart:math' as math;
import 'dart:typed_data';

// --- MIDI データ構造 ---

enum MidiEventType { noteOn, noteOff, programChange, unknown }

class MidiEvent {
  final MidiEventType type;
  final int channel;
  final int note;
  final int velocity;
  final int program;
  final int deltaTime;

  MidiEvent({
    required this.type,
    required this.channel,
    required this.note,
    required this.velocity,
    required this.program,
    required this.deltaTime,
  });
}

class MidiTrack {
  final List<MidiEvent> events;
  MidiTrack(this.events);
}

class MidiFile {
  final int format;
  final int trackCount;
  final int ticksPerQuarter;
  final List<MidiTrack> tracks;

  MidiFile({
    required this.format,
    required this.trackCount,
    required this.ticksPerQuarter,
    required this.tracks,
  });
}

class Note {
  final int midiNote;
  final int channel;
  final double startTime;
  final double duration;
  final int velocity;

  Note({
    required this.midiNote,
    required this.channel,
    required this.startTime,
    required this.duration,
    required this.velocity,
  });
}

// 矩形波デューティサイクル（NES 4種）
enum DutyCycle { duty12_5, duty25, duty50, duty75 }

double dutyCycleValue(DutyCycle duty) {
  switch (duty) {
    case DutyCycle.duty12_5:
      return 0.125;
    case DutyCycle.duty25:
      return 0.25;
    case DutyCycle.duty50:
      return 0.5;
    case DutyCycle.duty75:
      return 0.75;
  }
}

// バイト列リーダー（位置管理）
class ByteReader {
  final Uint8List data;
  int pos = 0;

  ByteReader(this.data);

  bool get hasMore => pos < data.length;

  int readByte() {
    if (pos >= data.length) throw StateError('EOF');
    return data[pos++];
  }

  int readVLQ() {
    int value = 0;
    int b;
    do {
      b = readByte();
      value = (value << 7) | (b & 0x7F);
    } while ((b & 0x80) != 0);
    return value;
  }

  int readUint16BE() {
    if (pos + 2 > data.length) throw StateError('EOF');
    final high = data[pos++];
    final low = data[pos++];
    return (high << 8) | low;
  }

  int readUint32BE() {
    if (pos + 4 > data.length) throw StateError('EOF');
    return (data[pos++] << 24) |
        (data[pos++] << 16) |
        (data[pos++] << 8) |
        data[pos++];
  }

  void skip(int n) {
    pos = math.min(pos + n, data.length);
  }
}

double midiNoteToFrequency(int midiNote) {
  return 440.0 * math.pow(2.0, (midiNote - 69) / 12.0).toDouble();
}

// MIDI ヘッダー読み込み
(List<int>, List<MidiTrack>) readMidiFile(Uint8List bytes) {
  final reader = ByteReader(bytes);

  if (reader.data.length < 14) throw FormatException('Invalid MIDI: too short');
  final chunkId = String.fromCharCodes(reader.data.sublist(0, 4));
  reader.pos = 4;
  if (chunkId != 'MThd') throw FormatException('Invalid MIDI header');

  reader.readUint32BE(); // chunk length
  final format = reader.readUint16BE();
  final trackCount = reader.readUint16BE();
  final ticksPerQuarter = reader.readUint16BE();

  final tracks = <MidiTrack>[];
  for (var i = 0; i < trackCount && reader.hasMore; i++) {
    if (reader.pos + 8 > reader.data.length) break;
    final track = readMidiTrack(reader);
    tracks.add(track);
  }

  return ([format, trackCount, ticksPerQuarter], tracks);
}

MidiTrack readMidiTrack(ByteReader reader) {
  final chunkType = String.fromCharCodes([
    reader.data[reader.pos],
    reader.data[reader.pos + 1],
    reader.data[reader.pos + 2],
    reader.data[reader.pos + 3],
  ]);
  reader.pos += 4;
  if (chunkType != 'MTrk') throw FormatException('Invalid track header');

  final chunkLength = reader.readUint32BE();
  final endPos = reader.pos + chunkLength;
  final events = <MidiEvent>[];
  var runningStatus = 0;

  while (reader.pos < endPos && reader.hasMore) {
    try {
      final deltaTime = reader.readVLQ();
      final (evt, newStatus) = parseMidiEvent(reader, runningStatus, deltaTime);
      runningStatus = newStatus;
      if (evt.type != MidiEventType.unknown) {
        events.add(evt);
      }
    } catch (_) {
      if (reader.pos < endPos && reader.hasMore) reader.readByte();
    }
  }
  reader.pos = endPos;

  return MidiTrack(events);
}

(MidiEvent, int) parseMidiEvent(ByteReader reader, int runningStatus, int deltaTime) {
  if (!reader.hasMore) return (_unknown(deltaTime), runningStatus);

  var status = runningStatus;
  final firstByte = reader.readByte();

  if (firstByte >= 0x80) {
    status = firstByte;
  } else {
    reader.pos--; // push back for running status data
  }

  final eventType = status & 0xF0;
  final channel = status & 0x0F;

  switch (eventType) {
    case 0x90: // Note On
      final note = reader.readByte();
      final velocity = reader.readByte();
      if (velocity == 0) {
        return (
          MidiEvent(
            type: MidiEventType.noteOff,
            channel: channel,
            note: note,
            velocity: 0,
            program: 0,
            deltaTime: deltaTime,
          ),
          status,
        );
      }
      return (
        MidiEvent(
          type: MidiEventType.noteOn,
          channel: channel,
          note: note,
          velocity: velocity,
          program: 0,
          deltaTime: deltaTime,
        ),
        status,
      );

    case 0x80: // Note Off
      final note = reader.readByte();
      reader.readByte(); // velocity
      return (
        MidiEvent(
          type: MidiEventType.noteOff,
          channel: channel,
          note: note,
          velocity: 0,
          program: 0,
          deltaTime: deltaTime,
        ),
        status,
      );

    case 0xC0: // Program Change
      final program = reader.readByte();
      return (
        MidiEvent(
          type: MidiEventType.programChange,
          channel: channel,
          note: 0,
          velocity: 0,
          program: program,
          deltaTime: deltaTime,
        ),
        status,
      );

    default:
      if (status == 0xFF) {
        reader.readByte(); // meta type
        final length = reader.readVLQ();
        reader.skip(length);
      } else if (status >= 0x80) {
        reader.readByte();
        if ((status & 0xE0) != 0xC0 && (status & 0xE0) != 0xD0) {
          if (reader.hasMore) reader.readByte();
        }
      }
      return (_unknown(deltaTime), status);
  }
}

MidiEvent _unknown(int deltaTime) => MidiEvent(
      type: MidiEventType.unknown,
      channel: 0,
      note: 0,
      velocity: 0,
      program: 0,
      deltaTime: deltaTime,
    );

// イベント → 音符リスト
List<Note> eventsToNotes(MidiFile midiFile, double tempo) {
  double ticksToSeconds(int tick) =>
      tick / midiFile.ticksPerQuarter * 60.0 / tempo;

  final noteOnMap = <String, ({int velocity, double startTime})>{};
  final notes = <Note>[];

  for (final track in midiFile.tracks) {
    var currentTick = 0;
    for (final evt in track.events) {
      currentTick += evt.deltaTime;
      final key = '${evt.channel}-${evt.note}';

      switch (evt.type) {
        case MidiEventType.noteOn:
          noteOnMap[key] = (
            velocity: evt.velocity,
            startTime: ticksToSeconds(currentTick),
          );
          break;
        case MidiEventType.noteOff:
          final onInfo = noteOnMap.remove(key);
          if (onInfo != null) {
            final endTime = ticksToSeconds(currentTick);
            final duration = endTime - onInfo.startTime;
            if (duration > 0) {
              notes.add(Note(
                midiNote: evt.note,
                channel: evt.channel,
                startTime: onInfo.startTime,
                duration: duration,
                velocity: onInfo.velocity,
              ));
            }
          }
          break;
        default:
          break;
      }
    }
  }
  return notes;
}

// --- 波形生成 ---

List<double> generateSquareWave(
    double frequency, DutyCycle duty, int sampleRate, double duration) {
  final samples = (sampleRate * duration).floor();
  final dutyVal = dutyCycleValue(duty);
  return List.generate(samples, (i) {
    final t = i / sampleRate;
    final phase = (t * frequency) % 1.0;
    return phase < dutyVal ? 1.0 : -1.0;
  });
}

List<double> generateTriangleWave(
    double frequency, int sampleRate, double duration) {
  final samples = (sampleRate * duration).floor();
  return List.generate(samples, (i) {
    final t = i / sampleRate;
    final phase = (t * frequency) % 1.0;
    return phase < 0.5 ? 4.0 * phase - 1.0 : 3.0 - 4.0 * phase;
  });
}

List<double> generateNoise(bool isShort, int sampleRate, double duration) {
  final samples = (sampleRate * duration).floor();
  var shift = 1;
  return List.generate(samples, (_) {
    final bit0 = shift & 1;
    final bit1 = isShort ? (shift >> 6) & 1 : (shift >> 1) & 1;
    final feedback = bit0 ^ bit1;
    shift = (shift >> 1) | (feedback << 14);
    return bit0 == 1 ? 1.0 : -1.0;
  });
}

List<double> noteToWaveform(
    Note note, int sampleRate, double totalDuration) {
  final frequency = midiNoteToFrequency(note.midiNote);
  final volume = (note.velocity / 127.0) * 0.7;
  final startSample = (note.startTime * sampleRate).floor();
  final noteSamples = (note.duration * sampleRate).floor();
  final totalSamples = (totalDuration * sampleRate).floor();

  List<double> waveform;
  switch (note.channel % 4) {
    case 0:
      waveform =
          generateSquareWave(frequency, DutyCycle.duty50, sampleRate, note.duration);
      break;
    case 1:
      waveform =
          generateSquareWave(frequency, DutyCycle.duty25, sampleRate, note.duration);
      break;
    case 2:
      waveform = generateTriangleWave(frequency, sampleRate, note.duration);
      break;
    default:
      waveform = generateNoise(false, sampleRate, note.duration);
  }

  final result = List.filled(totalSamples, 0.0);
  final endSample = math.min(startSample + noteSamples, totalSamples);
  for (var i = startSample; i < endSample && i - startSample < waveform.length; i++) {
    result[i] = waveform[i - startSample] * volume;
  }
  return result;
}

List<double> mixWaveforms(List<List<double>> waveforms) {
  if (waveforms.isEmpty) return [];
  final maxLength = waveforms.map((w) => w.length).reduce(math.max);
  final n = waveforms.length.toDouble();
  return List.generate(maxLength, (i) {
    var sum = 0.0;
    for (final w in waveforms) {
      if (i < w.length) sum += w[i];
    }
    return sum / n;
  });
}

Uint8List createWaveHeader(int sampleRate, int numChannels, int bitsPerSample, int dataSize) {
  final byteRate = sampleRate * numChannels * (bitsPerSample ~/ 8);
  final blockAlign = numChannels * (bitsPerSample ~/ 8);
  final header = ByteData(44);
  var o = 0;
  header.setUint8(o++, 0x52); header.setUint8(o++, 0x49);
  header.setUint8(o++, 0x46); header.setUint8(o++, 0x46);
  header.setUint32(o, 36 + dataSize, Endian.little); o += 4;
  header.setUint8(o++, 0x57); header.setUint8(o++, 0x41);
  header.setUint8(o++, 0x56); header.setUint8(o++, 0x45);
  header.setUint8(o++, 0x66); header.setUint8(o++, 0x6d);
  header.setUint8(o++, 0x74); header.setUint8(o++, 0x20);
  header.setUint32(o, 16, Endian.little); o += 4;
  header.setUint16(o, 1, Endian.little); o += 2;
  header.setUint16(o, numChannels, Endian.little); o += 2;
  header.setUint32(o, sampleRate, Endian.little); o += 4;
  header.setUint32(o, byteRate, Endian.little); o += 4;
  header.setUint16(o, blockAlign, Endian.little); o += 2;
  header.setUint16(o, bitsPerSample, Endian.little); o += 2;
  header.setUint8(o++, 0x64); header.setUint8(o++, 0x61);
  header.setUint8(o++, 0x74); header.setUint8(o++, 0x61);
  header.setUint32(o, dataSize, Endian.little);
  return header.buffer.asUint8List();
}

void writeWaveFile(String filename, List<double> samples, int sampleRate) {
  final sampleData = samples
      .map((s) {
        final clamped = s.clamp(-1.0, 1.0);
        return (clamped * 32767).round().clamp(-32768, 32767);
      })
      .toList();
  final dataSize = sampleData.length * 2;
  final header = createWaveHeader(sampleRate, 1, 16, dataSize);
  final file = File(filename).openSync(mode: FileMode.write);
  file.writeFromSync(header);
  final data = ByteData(sampleData.length * 2);
  for (var i = 0; i < sampleData.length; i++) {
    data.setInt16(i * 2, sampleData[i], Endian.little);
  }
  file.writeFromSync(data.buffer.asUint8List());
  file.closeSync();
}

bool convertMidiToWav(String midiPath, String wavPath, double tempo) {
  try {
    final bytes = File(midiPath).readAsBytesSync();
    final (header, tracks) = readMidiFile(bytes);
    final format = header[0];
    final trackCount = header[1];
    final ticksPerQuarter = header[2];
    final midiFile = MidiFile(
      format: format,
      trackCount: trackCount,
      ticksPerQuarter: ticksPerQuarter,
      tracks: tracks,
    );

    print('MIDIファイルを読み込みました: Format $format, $trackCount tracks, $ticksPerQuarter ticks/quarter');

    final notes = eventsToNotes(midiFile, tempo);
    print('${notes.length}個の音符を検出しました');

    if (notes.isEmpty) {
      print('音符が見つかりませんでした');
      return false;
    }

    double totalDuration = 0;
    for (final n in notes) {
      final end = n.startTime + n.duration;
      if (end > totalDuration) totalDuration = end;
    }
    totalDuration += 1.0;

    const sampleRate = 44100;
    print('総演奏時間: ${totalDuration.toStringAsFixed(2)}秒');
    print('波形を生成中...');

    final waveforms = notes.map((n) => noteToWaveform(n, sampleRate, totalDuration)).toList();
    final mixed = mixWaveforms(waveforms);
    writeWaveFile(wavPath, mixed, sampleRate);
    print("WAVファイル '$wavPath' を生成しました！");
    return true;
  } catch (e, st) {
    print('変換エラー: $e');
    print(st);
    return false;
  }
}

void generateDemoNESMusic() {
  const sampleRate = 44100;
  const duration = 2.0;

  final square1 = generateSquareWave(440.0, DutyCycle.duty50, sampleRate, duration)
      .map((s) => s * 0.3)
      .toList();
  final square2 = generateSquareWave(330.0, DutyCycle.duty25, sampleRate, duration)
      .map((s) => s * 0.25)
      .toList();
  final triangle = generateTriangleWave(110.0, sampleRate, duration)
      .map((s) => s * 0.4)
      .toList();

  final mixed = mixWaveforms([square1, square2, triangle]);
  writeWaveFile('demo_nes_sound.wav', mixed, sampleRate);
  print("デモNES音源のWAVファイル 'demo_nes_sound.wav' を生成しました！");
}

void main(List<String> args) {
  if (args.isNotEmpty) {
    final midiFile = args[0];
    final outputFile = args.length >= 2
        ? args[1]
        : midiFile.replaceAll(RegExp(r'\.mid$'), '.wav');
    final tempo = args.length >= 3 ? double.tryParse(args[2]) ?? 120.0 : 120.0;

    if (!File(midiFile).existsSync()) {
      print("MIDIファイル '$midiFile' が見つかりません");
      exit(1);
    }
    exit(convertMidiToWav(midiFile, outputFile, tempo) ? 0 : 1);
  } else {
    print('使用方法: dart run midi2chiptune <MIDIファイル> [出力WAVファイル] [テンポ]');
    print('デモファイルを生成します...');
    generateDemoNESMusic();
  }
}
