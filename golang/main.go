package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"math"
	"os"
	"path/filepath"
	"strconv"
)

// WAVファイルのヘッダー構造体
type WaveHeader struct {
	ChunkID       [4]byte // "RIFF"
	ChunkSize     uint32
	Format        [4]byte // "WAVE"
	Subchunk1ID   [4]byte // "fmt "
	Subchunk1Size uint32
	AudioFormat   uint16
	NumChannels   uint16
	SampleRate    uint32
	ByteRate      uint32
	BlockAlign    uint16
	BitsPerSample uint16
	Subchunk2ID   [4]byte // "data"
	Subchunk2Size uint32
}

// MIDIイベントの種類
type MidiEventType int

const (
	NoteOn MidiEventType = iota
	NoteOff
	ProgramChange
	Unknown
)

// MIDIイベント
type MidiEvent struct {
	Type      MidiEventType
	Channel   int
	Note      int
	Velocity  int
	Program   int
	DeltaTime int
}

// MIDIトラック
type MidiTrack struct {
	Events []MidiEvent
}

// MIDIファイル
type MidiFile struct {
	Format          int
	TrackCount      int
	TicksPerQuarter int
	Tracks          []MidiTrack
}

// 音符情報
type Note struct {
	MidiNote  int
	Channel   int
	StartTime float64
	Duration  float64
	Velocity  int
}

// 矩形波のデューティサイクル
type DutyCycle int

const (
	Duty12_5 DutyCycle = iota // 12.5%
	Duty25                    // 25%
	Duty50                    // 50%
	Duty75                    // 75%
)

// デューティサイクルの値を取得
func getDutyCycleValue(duty DutyCycle) float64 {
	switch duty {
	case Duty12_5:
		return 0.125
	case Duty25:
		return 0.25
	case Duty50:
		return 0.5
	case Duty75:
		return 0.75
	default:
		return 0.5
	}
}

// MIDIノート番号を周波数に変換
func midiNoteToFrequency(midiNote int) float64 {
	return 440.0 * math.Pow(2.0, float64(midiNote-69)/12.0)
}

// Variable Length Quantity (VLQ) の読み込み
func readVLQ(reader io.Reader) (int, error) {
	value := 0
	for {
		var b [1]byte
		_, err := reader.Read(b[:])
		if err != nil {
			return 0, err
		}

		byteVal := int(b[0])
		value = (value << 7) | (byteVal & 0x7F)

		if (byteVal & 0x80) == 0 {
			break
		}
	}
	return value, nil
}

// ビッグエンディアンで16ビット読み込み
func readUint16BE(reader io.Reader) (uint16, error) {
	var bytes [2]byte
	_, err := reader.Read(bytes[:])
	if err != nil {
		return 0, err
	}
	return binary.BigEndian.Uint16(bytes[:]), nil
}

// ビッグエンディアンで32ビット読み込み
func readUint32BE(reader io.Reader) (uint32, error) {
	var bytes [4]byte
	_, err := reader.Read(bytes[:])
	if err != nil {
		return 0, err
	}
	return binary.BigEndian.Uint32(bytes[:]), nil
}

// MIDIヘッダーチャンクの読み込み
func readMidiHeader(reader io.Reader) (int, int, int, error) {
	var chunkType [4]byte
	_, err := reader.Read(chunkType[:])
	if err != nil {
		return 0, 0, 0, err
	}

	if string(chunkType[:]) != "MThd" {
		return 0, 0, 0, fmt.Errorf("invalid MIDI header")
	}

	_, err = readUint32BE(reader)
	if err != nil {
		return 0, 0, 0, err
	}

	format, err := readUint16BE(reader)
	if err != nil {
		return 0, 0, 0, err
	}

	trackCount, err := readUint16BE(reader)
	if err != nil {
		return 0, 0, 0, err
	}

	ticksPerQuarter, err := readUint16BE(reader)
	if err != nil {
		return 0, 0, 0, err
	}

	return int(format), int(trackCount), int(ticksPerQuarter), nil
}

// MIDIイベントの解析
func parseMidiEvent(reader io.Reader, runningStatus *int, deltaTime int) (MidiEvent, error) {
	var firstByte [1]byte
	_, err := reader.Read(firstByte[:])
	if err != nil {
		return MidiEvent{Type: Unknown}, err
	}

	status := *runningStatus
	if firstByte[0] >= 128 {
		status = int(firstByte[0])
		*runningStatus = status
	} else {
		// Running statusの場合、バイトを戻す
		// Go では Seek を使用（実際のファイルリーダーの場合）
		// 簡単のため、firstByteを次の処理で使用
	}

	event := MidiEvent{DeltaTime: deltaTime}

	switch status & 0xF0 {
	case 0x90: // Note On
		event.Type = NoteOn
		event.Channel = status & 0x0F

		var noteVel [2]byte
		if firstByte[0] < 128 {
			noteVel[0] = firstByte[0]
			_, err = reader.Read(noteVel[1:])
		} else {
			_, err = reader.Read(noteVel[:])
		}
		if err != nil {
			return event, err
		}

		event.Note = int(noteVel[0])
		event.Velocity = int(noteVel[1])

		if event.Velocity == 0 {
			event.Type = NoteOff
		}

	case 0x80: // Note Off
		event.Type = NoteOff
		event.Channel = status & 0x0F

		var noteVel [2]byte
		if firstByte[0] < 128 {
			noteVel[0] = firstByte[0]
			_, err = reader.Read(noteVel[1:])
		} else {
			_, err = reader.Read(noteVel[:])
		}
		if err != nil {
			return event, err
		}

		event.Note = int(noteVel[0])

	case 0xC0: // Program Change
		event.Type = ProgramChange
		event.Channel = status & 0x0F

		var program [1]byte
		if firstByte[0] < 128 {
			program[0] = firstByte[0]
		} else {
			_, err = reader.Read(program[:])
			if err != nil {
				return event, err
			}
		}
		event.Program = int(program[0])

	default:
		event.Type = Unknown
		// その他のイベントは適切にスキップ
		if status == 0xFF { // Meta event
			var eventType [1]byte
			reader.Read(eventType[:])
			length, _ := readVLQ(reader)
			data := make([]byte, length)
			reader.Read(data)
		} else if status >= 0x80 {
			// その他のMIDIイベント
			var dummy [1]byte
			reader.Read(dummy[:])
			if (status&0xE0) != 0xC0 && (status&0xE0) != 0xD0 {
				reader.Read(dummy[:])
			}
		}
	}

	return event, nil
}

// MIDIトラックの読み込み
func readMidiTrack(reader io.Reader) (MidiTrack, error) {
	var chunkType [4]byte
	_, err := reader.Read(chunkType[:])
	if err != nil {
		return MidiTrack{}, err
	}

	chunkLength, err := readUint32BE(reader)
	if err != nil {
		return MidiTrack{}, err
	}

	trackData := make([]byte, chunkLength)
	_, err = reader.Read(trackData)
	if err != nil {
		return MidiTrack{}, err
	}

	trackReader := &ByteReader{data: trackData, pos: 0}
	var events []MidiEvent
	runningStatus := 0

	for trackReader.pos < len(trackReader.data) {
		deltaTime, err := readVLQ(trackReader)
		if err != nil {
			break
		}

		event, err := parseMidiEvent(trackReader, &runningStatus, deltaTime)
		if err != nil {
			break
		}

		if event.Type != Unknown {
			events = append(events, event)
		}
	}

	return MidiTrack{Events: events}, nil
}

// ByteReader は byte slice から読み込むためのヘルパー
type ByteReader struct {
	data []byte
	pos  int
}

func (br *ByteReader) Read(p []byte) (int, error) {
	if br.pos >= len(br.data) {
		return 0, io.EOF
	}

	n := copy(p, br.data[br.pos:])
	br.pos += n
	return n, nil
}

// MIDIファイル全体の読み込み
func readMidiFile(filename string) (*MidiFile, error) {
	file, err := os.Open(filename)
	if err != nil {
		return nil, err
	}
	defer file.Close()

	format, trackCount, ticksPerQuarter, err := readMidiHeader(file)
	if err != nil {
		return nil, err
	}

	tracks := make([]MidiTrack, trackCount)
	for i := 0; i < trackCount; i++ {
		track, err := readMidiTrack(file)
		if err != nil {
			fmt.Printf("Warning: Error reading track %d: %v\n", i, err)
			break
		}
		tracks[i] = track
	}

	return &MidiFile{
		Format:          format,
		TrackCount:      trackCount,
		TicksPerQuarter: ticksPerQuarter,
		Tracks:          tracks,
	}, nil
}

// MIDIイベントから音符リストに変換
func eventsToNotes(midiFile *MidiFile, tempo float64) []Note {
	ticksToSeconds := func(tick int) float64 {
		return float64(tick) / float64(midiFile.TicksPerQuarter) * 60.0 / tempo
	}

	noteOnEvents := make(map[string]struct {
		velocity  int
		startTime float64
	})

	var notes []Note

	for _, track := range midiFile.Tracks {
		currentTick := 0
		for _, event := range track.Events {
			currentTick += event.DeltaTime

			key := fmt.Sprintf("%d-%d", event.Channel, event.Note)

			switch event.Type {
			case NoteOn:
				startTime := ticksToSeconds(currentTick)
				noteOnEvents[key] = struct {
					velocity  int
					startTime float64
				}{event.Velocity, startTime}

			case NoteOff:
				if noteOn, exists := noteOnEvents[key]; exists {
					endTime := ticksToSeconds(currentTick)
					duration := endTime - noteOn.startTime

					if duration > 0 {
						notes = append(notes, Note{
							MidiNote:  event.Note,
							Channel:   event.Channel,
							StartTime: noteOn.startTime,
							Duration:  duration,
							Velocity:  noteOn.velocity,
						})
					}
					delete(noteOnEvents, key)
				}
			}
		}
	}

	return notes
}

// 波形生成関数
func generateSquareWave(frequency float64, duty DutyCycle, sampleRate int, duration float64) []float64 {
	samples := int(float64(sampleRate) * duration)
	dutyValue := getDutyCycleValue(duty)
	waveform := make([]float64, samples)

	for i := 0; i < samples; i++ {
		t := float64(i) / float64(sampleRate)
		phase := math.Mod(t*frequency, 1.0)

		if phase < dutyValue {
			waveform[i] = 1.0
		} else {
			waveform[i] = -1.0
		}
	}

	return waveform
}

func generateTriangleWave(frequency float64, sampleRate int, duration float64) []float64 {
	samples := int(float64(sampleRate) * duration)
	waveform := make([]float64, samples)

	for i := 0; i < samples; i++ {
		t := float64(i) / float64(sampleRate)
		phase := math.Mod(t*frequency, 1.0)

		if phase < 0.5 {
			waveform[i] = 4.0*phase - 1.0
		} else {
			waveform[i] = 3.0 - 4.0*phase
		}
	}

	return waveform
}

func generateNoise(isShort bool, sampleRate int, duration float64) []float64 {
	samples := int(float64(sampleRate) * duration)
	waveform := make([]float64, samples)
	shift := uint16(1)

	for i := 0; i < samples; i++ {
		bit0 := shift & 1
		var bit1 uint16
		if isShort {
			bit1 = (shift >> 6) & 1
		} else {
			bit1 = (shift >> 1) & 1
		}

		feedback := bit0 ^ bit1
		shift = (shift >> 1) | (feedback << 14)

		if bit0 == 1 {
			waveform[i] = 1.0
		} else {
			waveform[i] = -1.0
		}
	}

	return waveform
}

// 音符を波形に変換
func noteToWaveform(note Note, sampleRate int, totalDuration float64) []float64 {
	frequency := midiNoteToFrequency(note.MidiNote)
	volume := float64(note.Velocity) / 127.0 * 0.7
	startSample := int(note.StartTime * float64(sampleRate))
	noteSamples := int(note.Duration * float64(sampleRate))
	totalSamples := int(totalDuration * float64(sampleRate))

	var waveform []float64

	switch note.Channel % 4 {
	case 0:
		waveform = generateSquareWave(frequency, Duty50, sampleRate, note.Duration)
	case 1:
		waveform = generateSquareWave(frequency, Duty25, sampleRate, note.Duration)
	case 2:
		waveform = generateTriangleWave(frequency, sampleRate, note.Duration)
	default:
		waveform = generateNoise(false, sampleRate, note.Duration)
	}

	result := make([]float64, totalSamples)
	endSample := startSample + noteSamples
	if endSample > totalSamples {
		endSample = totalSamples
	}

	for i := startSample; i < endSample && i-startSample < len(waveform); i++ {
		result[i] = waveform[i-startSample] * volume
	}

	return result
}

// 複数の波形をミックス
func mixWaveforms(waveforms [][]float64) []float64 {
	if len(waveforms) == 0 {
		return []float64{}
	}

	maxLength := 0
	for _, w := range waveforms {
		if len(w) > maxLength {
			maxLength = len(w)
		}
	}

	result := make([]float64, maxLength)
	for i := 0; i < maxLength; i++ {
		sum := 0.0
		for _, w := range waveforms {
			if i < len(w) {
				sum += w[i]
			}
		}
		result[i] = sum / float64(len(waveforms))
	}

	return result
}

// 16ビット整数に変換
func convertTo16Bit(samples []float64) []int16 {
	result := make([]int16, len(samples))
	for i, s := range samples {
		clamped := math.Max(-1.0, math.Min(1.0, s))
		result[i] = int16(clamped * 32767.0)
	}
	return result
}

// WAVファイル書き込み
func writeWaveFile(filename string, samples []float64, sampleRate int) error {
	file, err := os.Create(filename)
	if err != nil {
		return err
	}
	defer file.Close()

	sampleData := convertTo16Bit(samples)
	dataSize := len(sampleData) * 2

	header := WaveHeader{
		ChunkID:       [4]byte{'R', 'I', 'F', 'F'},
		ChunkSize:     uint32(36 + dataSize),
		Format:        [4]byte{'W', 'A', 'V', 'E'},
		Subchunk1ID:   [4]byte{'f', 'm', 't', ' '},
		Subchunk1Size: 16,
		AudioFormat:   1,
		NumChannels:   1,
		SampleRate:    uint32(sampleRate),
		ByteRate:      uint32(sampleRate * 1 * 16 / 8),
		BlockAlign:    uint16(1 * 16 / 8),
		BitsPerSample: 16,
		Subchunk2ID:   [4]byte{'d', 'a', 't', 'a'},
		Subchunk2Size: uint32(dataSize),
	}

	// ヘッダー書き込み
	err = binary.Write(file, binary.LittleEndian, header)
	if err != nil {
		return err
	}

	// サンプルデータ書き込み
	err = binary.Write(file, binary.LittleEndian, sampleData)
	if err != nil {
		return err
	}

	return nil
}

// MIDIからWAV変換のメイン関数
func convertMidiToWav(midiFilename, wavFilename string, tempo float64) error {
	midiFile, err := readMidiFile(midiFilename)
	if err != nil {
		return fmt.Errorf("MIDI file read error: %v", err)
	}

	fmt.Printf("MIDIファイルを読み込みました: Format %d, %d tracks, %d ticks/quarter\n",
		midiFile.Format, midiFile.TrackCount, midiFile.TicksPerQuarter)

	notes := eventsToNotes(midiFile, tempo)
	fmt.Printf("%d個の音符を検出しました\n", len(notes))

	if len(notes) == 0 {
		return fmt.Errorf("音符が見つかりませんでした")
	}

	// 総演奏時間を計算
	totalDuration := 0.0
	for _, note := range notes {
		endTime := note.StartTime + note.Duration
		if endTime > totalDuration {
			totalDuration = endTime
		}
	}
	totalDuration += 1.0 // 余裕を持たせる

	sampleRate := 44100
	fmt.Printf("総演奏時間: %.2f秒\n", totalDuration)
	fmt.Println("波形を生成中...")

	var waveforms [][]float64
	for _, note := range notes {
		waveform := noteToWaveform(note, sampleRate, totalDuration)
		waveforms = append(waveforms, waveform)
	}

	mixed := mixWaveforms(waveforms)
	err = writeWaveFile(wavFilename, mixed, sampleRate)
	if err != nil {
		return err
	}

	fmt.Printf("WAVファイル '%s' を生成しました！\n", wavFilename)
	return nil
}

// デモ用のサンプル生成
func generateDemoNESMusic() error {
	sampleRate := 44100
	duration := 2.0

	// チャンネル1: 矩形波（メロディ）
	square1 := generateSquareWave(440.0, Duty50, sampleRate, duration)
	for i := range square1 {
		square1[i] *= 0.3
	}

	// チャンネル2: 矩形波（ハーモニー）
	square2 := generateSquareWave(330.0, Duty25, sampleRate, duration)
	for i := range square2 {
		square2[i] *= 0.25
	}

	// チャンネル3: 三角波（ベース）
	triangle := generateTriangleWave(110.0, sampleRate, duration)
	for i := range triangle {
		triangle[i] *= 0.4
	}

	// ミックス
	mixed := mixWaveforms([][]float64{square1, square2, triangle})

	// WAVファイルに出力
	err := writeWaveFile("demo_nes_sound.wav", mixed, sampleRate)
	if err != nil {
		return err
	}

	fmt.Println("デモNES音源のWAVファイル 'demo_nes_sound.wav' を生成しました！")
	return nil
}

// メイン実行関数
func main() {
	if len(os.Args) >= 2 {
		midiFile := os.Args[1]

		var outputFile string
		if len(os.Args) >= 3 {
			outputFile = os.Args[2]
		} else {
			outputFile = filepath.Base(midiFile)
			outputFile = outputFile[:len(outputFile)-len(filepath.Ext(outputFile))] + ".wav"
		}

		tempo := 120.0
		if len(os.Args) >= 4 {
			if t, err := strconv.ParseFloat(os.Args[3], 64); err == nil {
				tempo = t
			}
		}

		if _, err := os.Stat(midiFile); os.IsNotExist(err) {
			fmt.Printf("MIDIファイル '%s' が見つかりません\n", midiFile)
			os.Exit(1)
		}

		if err := convertMidiToWav(midiFile, outputFile, tempo); err != nil {
			fmt.Printf("変換エラー: %v\n", err)
			os.Exit(1)
		}
	} else {
		fmt.Println("使用方法: program <MIDIファイル> [出力WAVファイル] [テンポ]")
		fmt.Println("デモファイルを生成します...")
		if err := generateDemoNESMusic(); err != nil {
			fmt.Printf("デモ生成エラー: %v\n", err)
			os.Exit(1)
		}
	}
}
