use std::collections::HashMap;
use std::fs::File;
use std::io::{Read, Write};
use std::path::Path;

// WAVファイルのヘッダー構造体
#[derive(Debug)]
struct WaveHeader {
    chunk_id: [u8; 4], // "RIFF"
    chunk_size: u32,
    format: [u8; 4],       // "WAVE"
    subchunk1_id: [u8; 4], // "fmt "
    subchunk1_size: u32,
    audio_format: u16,
    num_channels: u16,
    sample_rate: u32,
    byte_rate: u32,
    block_align: u16,
    bits_per_sample: u16,
    subchunk2_id: [u8; 4], // "data"
    subchunk2_size: u32,
}

// MIDIイベント
#[derive(Debug, Clone, PartialEq)]
enum MidiEvent {
    NoteOn {
        channel: u8,
        note: u8,
        velocity: u8,
        time: u32,
    },
    NoteOff {
        channel: u8,
        note: u8,
        time: u32,
    },
    ProgramChange {
        channel: u8,
        program: u8,
        time: u32,
    },
    Unknown,
}

// MIDIトラック
#[derive(Debug)]
struct MidiTrack {
    events: Vec<MidiEvent>,
}

// MIDIファイル
#[derive(Debug)]
struct MidiFile {
    format: u16,
    track_count: u16,
    ticks_per_quarter: u16,
    tracks: Vec<MidiTrack>,
}

// 音符情報
#[derive(Debug, Clone)]
struct Note {
    midi_note: u8,
    channel: u8,
    start_time: f64,
    duration: f64,
    velocity: u8,
}

// 矩形波のデューティサイクル（ファミコンの4種類）
#[derive(Debug, Clone, Copy)]
enum DutyCycle {
    Duty12_5, // 12.5%
    Duty25,   // 25%
    Duty50,   // 50%
    Duty75,   // 75%
}

impl DutyCycle {
    fn value(&self) -> f64 {
        match self {
            DutyCycle::Duty12_5 => 0.125,
            DutyCycle::Duty25 => 0.25,
            DutyCycle::Duty50 => 0.5,
            DutyCycle::Duty75 => 0.75,
        }
    }
}

// MIDIノート番号を周波数に変換
fn midi_note_to_frequency(midi_note: u8) -> f64 {
    440.0 * 2_f64.powf((midi_note as f64 - 69.0) / 12.0)
}

// Variable Length Quantity (VLQ) の読み込み
fn read_vlq(data: &[u8], pos: &mut usize) -> Result<u32, Box<dyn std::error::Error>> {
    let mut value = 0u32;
    let mut byte;

    loop {
        if *pos >= data.len() {
            return Err("Unexpected end of data while reading VLQ".into());
        }
        byte = data[*pos];
        *pos += 1;
        value = (value << 7) | (byte & 0x7F) as u32;
        if (byte & 0x80) == 0 {
            break;
        }
    }
    Ok(value)
}

// ビッグエンディアンの16ビット読み込み
fn read_be_u16(data: &[u8], pos: &mut usize) -> u16 {
    let result = u16::from_be_bytes([data[*pos], data[*pos + 1]]);
    *pos += 2;
    result
}

// ビッグエンディアンの32ビット読み込み
fn read_be_u32(data: &[u8], pos: &mut usize) -> u32 {
    let result = u32::from_be_bytes([data[*pos], data[*pos + 1], data[*pos + 2], data[*pos + 3]]);
    *pos += 4;
    result
}

// MIDIヘッダーチャンクの読み込み
fn read_midi_header(
    data: &[u8],
    pos: &mut usize,
) -> Result<(u16, u16, u16), Box<dyn std::error::Error>> {
    // "MThd" チェック
    if &data[*pos..*pos + 4] != b"MThd" {
        return Err("Invalid MIDI header".into());
    }
    *pos += 4;

    let chunk_length = read_be_u32(data, pos);
    if chunk_length != 6 {
        return Err("Invalid MIDI header chunk length".into());
    }

    let format = read_be_u16(data, pos);
    let track_count = read_be_u16(data, pos);
    let ticks_per_quarter = read_be_u16(data, pos);

    Ok((format, track_count, ticks_per_quarter))
}

// MIDIイベントの解析
fn parse_midi_event(
    data: &[u8],
    pos: &mut usize,
    running_status: &mut u8,
    delta_time: u32,
) -> Result<MidiEvent, Box<dyn std::error::Error>> {
    if *pos >= data.len() {
        return Ok(MidiEvent::Unknown);
    }

    let first_byte = data[*pos];

    if first_byte >= 0x80 {
        *running_status = first_byte;
        *pos += 1;
    }

    let status = *running_status;
    let event_type = status & 0xF0;
    let channel = status & 0x0F;

    match event_type {
        0x90 => {
            // Note On
            if *pos + 1 >= data.len() {
                return Ok(MidiEvent::Unknown);
            }
            let note = data[*pos];
            let velocity = data[*pos + 1];
            *pos += 2;

            if velocity == 0 {
                Ok(MidiEvent::NoteOff {
                    channel,
                    note,
                    time: delta_time,
                })
            } else {
                Ok(MidiEvent::NoteOn {
                    channel,
                    note,
                    velocity,
                    time: delta_time,
                })
            }
        }
        0x80 => {
            // Note Off
            if *pos + 1 >= data.len() {
                return Ok(MidiEvent::Unknown);
            }
            let note = data[*pos];
            *pos += 2; // velocityもスキップ
            Ok(MidiEvent::NoteOff {
                channel,
                note,
                time: delta_time,
            })
        }
        0xC0 => {
            // Program Change
            if *pos >= data.len() {
                return Ok(MidiEvent::Unknown);
            }
            let program = data[*pos];
            *pos += 1;
            Ok(MidiEvent::ProgramChange {
                channel,
                program,
                time: delta_time,
            })
        }
        _ => {
            // その他のイベントはスキップ
            if status == 0xFF {
                // Meta event
                if *pos >= data.len() {
                    return Ok(MidiEvent::Unknown);
                }
                *pos += 1; // event type
                if let Ok(length) = read_vlq(data, pos) {
                    *pos += length as usize;
                }
            } else if status >= 0x80 {
                // その他のMIDIイベント
                if *pos < data.len() {
                    *pos += 1;
                    if (status & 0xE0) != 0xC0 && (status & 0xE0) != 0xD0 {
                        if *pos < data.len() {
                            *pos += 1;
                        }
                    }
                }
            }
            Ok(MidiEvent::Unknown)
        }
    }
}

// MIDIトラックの読み込み
fn read_midi_track(data: &[u8], pos: &mut usize) -> Result<MidiTrack, Box<dyn std::error::Error>> {
    // "MTrk" チェック
    if *pos + 4 > data.len() || &data[*pos..*pos + 4] != b"MTrk" {
        return Err("Invalid MIDI track header".into());
    }
    *pos += 4;

    let chunk_length = read_be_u32(data, pos);
    let end_position = *pos + chunk_length as usize;
    let mut events = Vec::new();
    let mut running_status = 0u8;

    while *pos < end_position && *pos < data.len() {
        if let Ok(delta_time) = read_vlq(data, pos) {
            if let Ok(event) = parse_midi_event(data, pos, &mut running_status, delta_time) {
                if event != MidiEvent::Unknown {
                    events.push(event);
                }
            }
        } else {
            break;
        }
    }

    Ok(MidiTrack { events })
}

// MIDIファイル全体の読み込み
fn read_midi_file<P: AsRef<Path>>(filename: P) -> Result<MidiFile, Box<dyn std::error::Error>> {
    let mut file = File::open(filename)?;
    let mut buffer = Vec::new();
    file.read_to_end(&mut buffer)?;

    let mut pos = 0;
    let (format, track_count, ticks_per_quarter) = read_midi_header(&buffer, &mut pos)?;

    let mut tracks = Vec::new();
    for _ in 0..track_count {
        if let Ok(track) = read_midi_track(&buffer, &mut pos) {
            tracks.push(track);
        }
    }

    Ok(MidiFile {
        format,
        track_count,
        ticks_per_quarter,
        tracks,
    })
}

// MIDIイベントから音符リストに変換
fn events_to_notes(midi_file: &MidiFile, tempo: f64) -> Vec<Note> {
    let ticks_to_seconds =
        |tick: u32| -> f64 { tick as f64 / midi_file.ticks_per_quarter as f64 * 60.0 / tempo };

    let mut note_on_events: HashMap<(u8, u8), (u8, f64)> = HashMap::new(); // (channel, note) -> (velocity, start_time)
    let mut notes = Vec::new();

    for track in &midi_file.tracks {
        let mut current_tick = 0u32;
        for event in &track.events {
            match event {
                MidiEvent::NoteOn {
                    channel,
                    note,
                    velocity,
                    time,
                } => {
                    current_tick += time;
                    let start_time = ticks_to_seconds(current_tick);
                    note_on_events.insert((*channel, *note), (*velocity, start_time));
                }
                MidiEvent::NoteOff {
                    channel,
                    note,
                    time,
                } => {
                    current_tick += time;
                    let end_time = ticks_to_seconds(current_tick);
                    if let Some((velocity, start_time)) = note_on_events.remove(&(*channel, *note))
                    {
                        let duration = end_time - start_time;
                        if duration > 0.0 {
                            notes.push(Note {
                                midi_note: *note,
                                channel: *channel,
                                start_time,
                                duration,
                                velocity,
                            });
                        }
                    }
                }
                _ => {}
            }
        }
    }

    notes
}

// 波形生成関数
fn generate_square_wave(
    frequency: f64,
    duty: DutyCycle,
    sample_rate: u32,
    duration: f64,
) -> Vec<f64> {
    let samples = (sample_rate as f64 * duration) as usize;
    let duty_value = duty.value();

    (0..samples)
        .map(|i| {
            let t = i as f64 / sample_rate as f64;
            let phase = (t * frequency) % 1.0;
            if phase < duty_value { 1.0 } else { -1.0 }
        })
        .collect()
}

fn generate_triangle_wave(frequency: f64, sample_rate: u32, duration: f64) -> Vec<f64> {
    let samples = (sample_rate as f64 * duration) as usize;

    (0..samples)
        .map(|i| {
            let t = i as f64 / sample_rate as f64;
            let phase = (t * frequency) % 1.0;
            if phase < 0.5 {
                4.0 * phase - 1.0
            } else {
                3.0 - 4.0 * phase
            }
        })
        .collect()
}

fn generate_noise(is_short: bool, sample_rate: u32, duration: f64) -> Vec<f64> {
    let samples = (sample_rate as f64 * duration) as usize;
    let mut shift = 1u16;

    (0..samples)
        .map(|_| {
            let bit0 = shift & 1;
            let bit1 = if is_short {
                (shift >> 6) & 1
            } else {
                (shift >> 1) & 1
            };
            let feedback = bit0 ^ bit1;
            shift = (shift >> 1) | (feedback << 14);

            if bit0 == 1 { 1.0 } else { -1.0 }
        })
        .collect()
}

// 音符を波形に変換
fn note_to_waveform(note: &Note, sample_rate: u32, total_duration: f64) -> Vec<f64> {
    let frequency = midi_note_to_frequency(note.midi_note);
    let volume = note.velocity as f64 / 127.0 * 0.7;
    let start_sample = (note.start_time * sample_rate as f64) as usize;
    let note_samples = (note.duration * sample_rate as f64) as usize;
    let total_samples = (total_duration * sample_rate as f64) as usize;

    let waveform = match note.channel % 4 {
        0 => generate_square_wave(frequency, DutyCycle::Duty50, sample_rate, note.duration),
        1 => generate_square_wave(frequency, DutyCycle::Duty25, sample_rate, note.duration),
        2 => generate_triangle_wave(frequency, sample_rate, note.duration),
        _ => generate_noise(false, sample_rate, note.duration),
    };

    let mut result = vec![0.0; total_samples];
    let end_sample = std::cmp::min(start_sample + note_samples, total_samples);

    for i in start_sample..end_sample {
        if i - start_sample < waveform.len() {
            result[i] = waveform[i - start_sample] * volume;
        }
    }

    result
}

// 複数の波形をミックス
fn mix_waveforms(waveforms: Vec<Vec<f64>>) -> Vec<f64> {
    if waveforms.is_empty() {
        return Vec::new();
    }

    let max_length = waveforms.iter().map(|w| w.len()).max().unwrap_or(0);
    let num_waveforms = waveforms.len() as f64;

    (0..max_length)
        .map(|i| {
            let sum: f64 = waveforms
                .iter()
                .map(|w| if i < w.len() { w[i] } else { 0.0 })
                .sum();
            sum / num_waveforms
        })
        .collect()
}

// 16ビット整数に変換
fn convert_to_16bit(samples: &[f64]) -> Vec<i16> {
    samples
        .iter()
        .map(|&s| {
            let clamped = s.clamp(-1.0, 1.0);
            (clamped * 32767.0) as i16
        })
        .collect()
}

// WAVヘッダー作成
fn create_wave_header(
    sample_rate: u32,
    num_channels: u16,
    bits_per_sample: u16,
    data_size: u32,
) -> WaveHeader {
    let byte_rate = sample_rate * num_channels as u32 * bits_per_sample as u32 / 8;
    let block_align = num_channels * bits_per_sample / 8;

    WaveHeader {
        chunk_id: *b"RIFF",
        chunk_size: 36 + data_size,
        format: *b"WAVE",
        subchunk1_id: *b"fmt ",
        subchunk1_size: 16,
        audio_format: 1,
        num_channels,
        sample_rate,
        byte_rate,
        block_align,
        bits_per_sample,
        subchunk2_id: *b"data",
        subchunk2_size: data_size,
    }
}

// WAVファイル書き込み
fn write_wave_file<P: AsRef<Path>>(
    filename: P,
    samples: &[f64],
    sample_rate: u32,
) -> Result<(), Box<dyn std::error::Error>> {
    let mut file = File::create(filename)?;

    let sample_data = convert_to_16bit(samples);
    let data_size = (sample_data.len() * 2) as u32;
    let header = create_wave_header(sample_rate, 1, 16, data_size);

    // ヘッダー書き込み
    file.write_all(&header.chunk_id)?;
    file.write_all(&header.chunk_size.to_le_bytes())?;
    file.write_all(&header.format)?;
    file.write_all(&header.subchunk1_id)?;
    file.write_all(&header.subchunk1_size.to_le_bytes())?;
    file.write_all(&header.audio_format.to_le_bytes())?;
    file.write_all(&header.num_channels.to_le_bytes())?;
    file.write_all(&header.sample_rate.to_le_bytes())?;
    file.write_all(&header.byte_rate.to_le_bytes())?;
    file.write_all(&header.block_align.to_le_bytes())?;
    file.write_all(&header.bits_per_sample.to_le_bytes())?;
    file.write_all(&header.subchunk2_id)?;
    file.write_all(&header.subchunk2_size.to_le_bytes())?;

    // サンプルデータ書き込み
    for sample in sample_data {
        file.write_all(&sample.to_le_bytes())?;
    }

    Ok(())
}

// MIDIからWAV変換のメイン関数
fn convert_midi_to_wav<P1: AsRef<Path>, P2: AsRef<Path>>(
    midi_filename: P1,
    wav_filename: P2,
    tempo: f64,
) -> Result<(), Box<dyn std::error::Error>> {
    let midi_file = read_midi_file(midi_filename)?;
    println!(
        "MIDIファイルを読み込みました: Format {}, {} tracks, {} ticks/quarter",
        midi_file.format, midi_file.track_count, midi_file.ticks_per_quarter
    );

    let notes = events_to_notes(&midi_file, tempo);
    println!("{}個の音符を検出しました", notes.len());

    if notes.is_empty() {
        return Err("音符が見つかりませんでした".into());
    }

    let total_duration = notes
        .iter()
        .map(|n| n.start_time + n.duration)
        .fold(0.0f64, f64::max)
        + 1.0; // 余裕を持たせる

    let sample_rate = 44100;

    println!("総演奏時間: {:.2}秒", total_duration);
    println!("波形を生成中...");

    let waveforms: Vec<Vec<f64>> = notes
        .iter()
        .map(|note| note_to_waveform(note, sample_rate, total_duration))
        .collect();

    let mixed = mix_waveforms(waveforms);
    write_wave_file(wav_filename, &mixed, sample_rate)?;

    println!("WAVファイルを生成しました！");
    Ok(())
}

// デモ用のサンプル生成
fn generate_demo_nes_music() -> Result<(), Box<dyn std::error::Error>> {
    let sample_rate = 44100;
    let duration = 2.0;

    // チャンネル1: 矩形波（メロディ）
    let square1: Vec<f64> = generate_square_wave(440.0, DutyCycle::Duty50, sample_rate, duration)
        .iter()
        .map(|s| s * 0.3)
        .collect();

    // チャンネル2: 矩形波（ハーモニー）
    let square2: Vec<f64> = generate_square_wave(330.0, DutyCycle::Duty25, sample_rate, duration)
        .iter()
        .map(|s| s * 0.25)
        .collect();

    // チャンネル3: 三角波（ベース）
    let triangle: Vec<f64> = generate_triangle_wave(110.0, sample_rate, duration)
        .iter()
        .map(|s| s * 0.4)
        .collect();

    // ミックス
    let mixed = mix_waveforms(vec![square1, square2, triangle]);

    // WAVファイルに出力
    write_wave_file("demo_nes_sound.wav", &mixed, sample_rate)?;
    println!("デモNES音源のWAVファイル 'demo_nes_sound.wav' を生成しました！");

    Ok(())
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = std::env::args().collect();

    if args.len() >= 2 {
        let midi_file = &args[1];
        let output_file = if args.len() >= 3 {
            args[2].clone()
        } else {
            Path::new(midi_file)
                .with_extension("wav")
                .to_string_lossy()
                .to_string()
        };
        let tempo = if args.len() >= 4 {
            args[3].parse()?
        } else {
            120.0
        };

        if Path::new(midi_file).exists() {
            convert_midi_to_wav(midi_file, output_file, tempo)?;
        } else {
            eprintln!("MIDIファイル '{}' が見つかりません", midi_file);
            std::process::exit(1);
        }
    } else {
        println!("使用方法: cargo run <MIDIファイル> [出力WAVファイル] [テンポ]");
        println!("デモファイルを生成します...");
        generate_demo_nes_music()?;
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_midi_note_to_frequency() {
        // A4 (440Hz)
        assert!((midi_note_to_frequency(69) - 440.0).abs() < 0.001);

        // C4 (261.63Hz)
        assert!((midi_note_to_frequency(60) - 261.625565).abs() < 0.001);
    }

    #[test]
    fn test_duty_cycle_values() {
        assert_eq!(DutyCycle::Duty12_5.value(), 0.125);
        assert_eq!(DutyCycle::Duty25.value(), 0.25);
        assert_eq!(DutyCycle::Duty50.value(), 0.5);
        assert_eq!(DutyCycle::Duty75.value(), 0.75);
    }

    #[test]
    fn test_waveform_generation() {
        let square = generate_square_wave(440.0, DutyCycle::Duty50, 44100, 0.1);
        assert!(!square.is_empty());
        assert_eq!(square.len(), 4410); // 44100 * 0.1

        let triangle = generate_triangle_wave(440.0, 44100, 0.1);
        assert!(!triangle.is_empty());
        assert_eq!(triangle.len(), 4410);

        let noise = generate_noise(false, 44100, 0.1);
        assert!(!noise.is_empty());
        assert_eq!(noise.len(), 4410);
    }
}
