import std/[os, streams, endians, tables, sequtils, math, strutils, algorithm, options]

# WAVファイルのヘッダー構造体
type
  WaveHeader = object
    chunkID: array[4, char]        # "RIFF"
    chunkSize: uint32
    format: array[4, char]         # "WAVE"
    subchunk1ID: array[4, char]    # "fmt "
    subchunk1Size: uint32
    audioFormat: uint16
    numChannels: uint16
    sampleRate: uint32
    byteRate: uint32
    blockAlign: uint16
    bitsPerSample: uint16
    subchunk2ID: array[4, char]    # "data"
    subchunk2Size: uint32

# MIDIイベント
type
  MidiEventKind = enum
    NoteOn, NoteOff, ProgramChange, Unknown
  
  MidiEvent = object
    case kind: MidiEventKind
    of NoteOn:
      noteOnChannel: int
      noteOnNote: int
      velocity: int
      noteOnTime: int
    of NoteOff:
      noteOffChannel: int
      noteOffNote: int
      noteOffTime: int
    of ProgramChange:
      progChannel: int
      program: int
      progTime: int
    of Unknown:
      discard

# MIDIトラック
type
  MidiTrack = object
    events: seq[MidiEvent]

# MIDIファイル
type
  MidiFile = object
    format: int
    trackCount: int
    ticksPerQuarter: int
    tracks: seq[MidiTrack]

# 音符情報
type
  Note = object
    midiNote: int
    channel: int
    startTime: float
    duration: float
    velocity: int

# 矩形波のデューティサイクル（ファミコンの4種類）
type
  DutyCycle = enum
    Duty12_5  # 12.5%
    Duty25    # 25%
    Duty50    # 50%
    Duty75    # 75%

# デューティサイクルの値を取得
proc getDutyCycleValue(duty: DutyCycle): float =
  case duty
  of Duty12_5: 0.125
  of Duty25: 0.25
  of Duty50: 0.5
  of Duty75: 0.75

# MIDIノート番号を周波数に変換
proc midiNoteToFrequency(midiNote: int): float =
  440.0 * pow(2.0, float(midiNote - 69) / 12.0)

# Variable Length Quantity (VLQ) の読み込み
proc readVLQ(stream: Stream): int =
  var value = 0
  var byte: uint8
  
  while true:
    byte = stream.readUint8()
    value = (value shl 7) or int(byte and 0x7F)
    if (byte and 0x80) == 0:
      break
  
  result = value

# ビッグエンディアンで16ビット整数を読み込み
proc readBigEndian16(stream: Stream): uint16 =
  var bytes: array[2, uint8]
  discard stream.readData(addr bytes, 2)
  result = (uint16(bytes[0]) shl 8) or uint16(bytes[1])

# ビッグエンディアンで32ビット整数を読み込み
proc readBigEndian32(stream: Stream): uint32 =
  var bytes: array[4, uint8]
  discard stream.readData(addr bytes, 4)
  result = (uint32(bytes[0]) shl 24) or (uint32(bytes[1]) shl 16) or 
           (uint32(bytes[2]) shl 8) or uint32(bytes[3])

# MIDIヘッダーチャンクの読み込み
proc readMidiHeader(stream: Stream): (int, int, int) =
  var chunkType: array[4, char]
  discard stream.readData(addr chunkType, 4)
  
  let chunkLength = stream.readBigEndian32()
  let format = stream.readBigEndian16()
  let trackCount = stream.readBigEndian16()
  let ticksPerQuarter = stream.readBigEndian16()
  
  (int(format), int(trackCount), int(ticksPerQuarter))

# MIDIイベントの解析
proc parseMidiEvent(stream: Stream, runningStatus: var int, deltaTime: int): MidiEvent =
  var status = runningStatus
  let firstByte = stream.readUint8()
  
  if firstByte >= 128:
    status = int(firstByte)
  else:
    stream.setPosition(stream.getPosition() - 1)
  
  let statusType = status and 0xF0
  
  case statusType
  of 0x90: # Note On
    let channel = status and 0x0F
    let note = int(stream.readUint8())
    let velocity = int(stream.readUint8())
    runningStatus = status
    if velocity == 0:
      return MidiEvent(kind: NoteOff, noteOffChannel: channel, noteOffNote: note, noteOffTime: deltaTime)
    else:
      return MidiEvent(kind: NoteOn, noteOnChannel: channel, noteOnNote: note, velocity: velocity, noteOnTime: deltaTime)
  
  of 0x80: # Note Off
    let channel = status and 0x0F
    let note = int(stream.readUint8())
    discard stream.readUint8() # velocity
    runningStatus = status
    return MidiEvent(kind: NoteOff, noteOffChannel: channel, noteOffNote: note, noteOffTime: deltaTime)
  
  of 0xC0: # Program Change
    let channel = status and 0x0F
    let program = int(stream.readUint8())
    runningStatus = status
    return MidiEvent(kind: ProgramChange, progChannel: channel, program: program, progTime: deltaTime)
  
  else:
    # その他のイベントはスキップ
    if status == 0xFF: # Meta event
      discard stream.readUint8() # event type
      let length = readVLQ(stream)
      for _ in 0..<length:
        discard stream.readUint8()
    elif status >= 0x80:
      # その他のMIDIイベント
      discard stream.readUint8()
      if (status and 0xE0) != 0xC0 and (status and 0xE0) != 0xD0:
        discard stream.readUint8()
    
    runningStatus = status
    return MidiEvent(kind: Unknown)

# MIDIトラックの読み込み
proc readMidiTrack(stream: Stream): MidiTrack =
  var chunkType: array[4, char]
  discard stream.readData(addr chunkType, 4)
  
  let chunkLength = stream.readBigEndian32()
  let endPosition = stream.getPosition() + int64(chunkLength)
  
  var events: seq[MidiEvent] = @[]
  var runningStatus = 0
  
  while stream.getPosition() < endPosition:
    try:
      let deltaTime = readVLQ(stream)
      let event = parseMidiEvent(stream, runningStatus, deltaTime)
      if event.kind != Unknown:
        events.add(event)
    except:
      if stream.getPosition() < endPosition:
        discard stream.readUint8()
  
  MidiTrack(events: events)

# MIDIファイル全体の読み込み
proc readMidiFile(filename: string): Option[MidiFile] =
  try:
    let stream = newFileStream(filename, fmRead)
    defer: stream.close()
    
    let (format, trackCount, ticksPerQuarter) = readMidiHeader(stream)
    
    var tracks: seq[MidiTrack] = @[]
    for i in 0..<trackCount:
      tracks.add(readMidiTrack(stream))
    
    some(MidiFile(
      format: format,
      trackCount: trackCount,
      ticksPerQuarter: ticksPerQuarter,
      tracks: tracks
    ))
  except:
    echo "MIDIファイルの読み込みエラー: ", getCurrentExceptionMsg()
    none(MidiFile)

# MIDIイベントから音符リストに変換
proc eventsToNotes(midiFile: MidiFile, tempo: float): seq[Note] =
  proc ticksToSeconds(tick: int): float =
    float(tick) / float(midiFile.ticksPerQuarter) * 60.0 / tempo
  
  var noteOnEvents = initTable[(int, int), (int, float)]() # (channel, note) -> (velocity, startTime)
  var notes: seq[Note] = @[]
  
  for track in midiFile.tracks:
    var currentTick = 0
    for event in track.events:
      case event.kind
      of NoteOn:
        currentTick += event.noteOnTime
        let startTime = ticksToSeconds(currentTick)
        noteOnEvents[(event.noteOnChannel, event.noteOnNote)] = (event.velocity, startTime)
      
      of NoteOff:
        currentTick += event.noteOffTime
        let endTime = ticksToSeconds(currentTick)
        let key = (event.noteOffChannel, event.noteOffNote)
        if key in noteOnEvents:
          let (velocity, startTime) = noteOnEvents[key]
          let duration = endTime - startTime
          if duration > 0.0:
            notes.add(Note(
              midiNote: event.noteOffNote,
              channel: event.noteOffChannel,
              startTime: startTime,
              duration: duration,
              velocity: velocity
            ))
          noteOnEvents.del(key)
      
      else:
        discard
  
  notes

# 波形生成関数
proc generateSquareWave(frequency: float, duty: DutyCycle, sampleRate: int, duration: float): seq[float] =
  let samples = int(float(sampleRate) * duration)
  let dutyValue = getDutyCycleValue(duty)
  
  result = newSeq[float](samples)
  for i in 0..<samples:
    let t = float(i) / float(sampleRate)
    let phase = (t * frequency) mod 1.0
    if phase < dutyValue:
      result[i] = 1.0
    else:
      result[i] = -1.0

proc generateTriangleWave(frequency: float, sampleRate: int, duration: float): seq[float] =
  let samples = int(float(sampleRate) * duration)
  
  result = newSeq[float](samples)
  for i in 0..<samples:
    let t = float(i) / float(sampleRate)
    let phase = (t * frequency) mod 1.0
    if phase < 0.5:
      result[i] = 4.0 * phase - 1.0
    else:
      result[i] = 3.0 - 4.0 * phase

proc generateNoise(isShort: bool, sampleRate: int, duration: float): seq[float] =
  let samples = int(float(sampleRate) * duration)
  var shift = 1'u16
  
  result = newSeq[float](samples)
  for i in 0..<samples:
    let bit0 = shift and 1
    let bit1 = if isShort: (shift shr 6) and 1 else: (shift shr 1) and 1
    let feedback = bit0 xor bit1
    shift = (shift shr 1) or (feedback shl 14)
    
    if bit0 == 1:
      result[i] = 1.0
    else:
      result[i] = -1.0

# 音符を波形に変換
proc noteToWaveform(note: Note, sampleRate: int, totalDuration: float): seq[float] =
  let frequency = midiNoteToFrequency(note.midiNote)
  let volume = float(note.velocity) / 127.0 * 0.7
  let startSample = int(note.startTime * float(sampleRate))
  let noteSamples = int(note.duration * float(sampleRate))
  let totalSamples = int(totalDuration * float(sampleRate))
  
  let waveform = case note.channel mod 4
    of 0: generateSquareWave(frequency, Duty50, sampleRate, note.duration)
    of 1: generateSquareWave(frequency, Duty25, sampleRate, note.duration)
    of 2: generateTriangleWave(frequency, sampleRate, note.duration)
    else: generateNoise(false, sampleRate, note.duration)
  
  result = newSeq[float](totalSamples)
  let endSample = min(startSample + noteSamples, totalSamples)
  
  for i in startSample..<endSample:
    if i - startSample < waveform.len:
      result[i] = waveform[i - startSample] * volume

# 複数の波形をミックス
proc mixWaveforms(waveforms: seq[seq[float]]): seq[float] =
  if waveforms.len == 0:
    return @[]
  
  let maxLength = waveforms.mapIt(it.len).max()
  result = newSeq[float](maxLength)
  
  for i in 0..<maxLength:
    var sum = 0.0
    for w in waveforms:
      if i < w.len:
        sum += w[i]
    result[i] = sum / float(waveforms.len)

# 16ビット整数に変換
proc convertTo16Bit(samples: seq[float]): seq[int16] =
  result = newSeq[int16](samples.len)
  for i, sample in samples:
    let clamped = max(-1.0, min(1.0, sample))
    result[i] = int16(clamped * 32767.0)

# WAVヘッダー作成
proc createWaveHeader(sampleRate, numChannels, bitsPerSample, dataSize: int): WaveHeader =
  let byteRate = uint32(sampleRate * numChannels * bitsPerSample div 8)
  let blockAlign = uint16(numChannels * bitsPerSample div 8)
  
  result.chunkID = ['R', 'I', 'F', 'F']
  result.chunkSize = uint32(36 + dataSize)
  result.format = ['W', 'A', 'V', 'E']
  result.subchunk1ID = ['f', 'm', 't', ' ']
  result.subchunk1Size = 16
  result.audioFormat = 1
  result.numChannels = uint16(numChannels)
  result.sampleRate = uint32(sampleRate)
  result.byteRate = byteRate
  result.blockAlign = blockAlign
  result.bitsPerSample = uint16(bitsPerSample)
  result.subchunk2ID = ['d', 'a', 't', 'a']
  result.subchunk2Size = uint32(dataSize)

# WAVファイル書き込み
proc writeWaveFile(filename: string, samples: seq[float], sampleRate: int) =
  let stream = newFileStream(filename, fmWrite)
  defer: stream.close()
  
  let sampleData = convertTo16Bit(samples)
  let dataSize = sampleData.len * 2
  let header = createWaveHeader(sampleRate, 1, 16, dataSize)
  
  # ヘッダー書き込み
  stream.write(header.chunkID)
  stream.write(header.chunkSize)
  stream.write(header.format)
  stream.write(header.subchunk1ID)
  stream.write(header.subchunk1Size)
  stream.write(header.audioFormat)
  stream.write(header.numChannels)
  stream.write(header.sampleRate)
  stream.write(header.byteRate)
  stream.write(header.blockAlign)
  stream.write(header.bitsPerSample)
  stream.write(header.subchunk2ID)
  stream.write(header.subchunk2Size)
  
  # サンプルデータ書き込み
  for sample in sampleData:
    stream.write(sample)

# MIDIからWAV変換のメイン関数
proc convertMidiToWav(midiFilename, wavFilename: string, tempo: float): bool =
  let midiFileOpt = readMidiFile(midiFilename)
  if midiFileOpt.isNone:
    echo "MIDIファイルの読み込みに失敗しました"
    return false
  
  let midiFile = midiFileOpt.get()
  echo "MIDIファイルを読み込みました: Format ", midiFile.format, 
       ", ", midiFile.trackCount, " tracks, ", midiFile.ticksPerQuarter, " ticks/quarter"
  
  let notes = eventsToNotes(midiFile, tempo)
  echo notes.len, "個の音符を検出しました"
  
  if notes.len == 0:
    echo "音符が見つかりませんでした"
    return false
  
  let totalDuration = notes.mapIt(it.startTime + it.duration).max() + 1.0 # 余裕を持たせる
  let sampleRate = 44100
  
  echo "総演奏時間: ", totalDuration.formatFloat(ffDecimal, 2), "秒"
  echo "波形を生成中..."
  
  let waveforms = notes.mapIt(noteToWaveform(it, sampleRate, totalDuration))
  let mixed = mixWaveforms(waveforms)
  writeWaveFile(wavFilename, mixed, sampleRate)
  
  echo "WAVファイル '", wavFilename, "' を生成しました！"
  return true

# デモ用のサンプル生成
proc generateDemoNESMusic() =
  let sampleRate = 44100
  let duration = 2.0
  
  # チャンネル1: 矩形波（メロディ）
  let square1 = generateSquareWave(440.0, Duty50, sampleRate, duration)
  let square1Final = square1.mapIt(it * 0.3)
  
  # チャンネル2: 矩形波（ハーモニー）
  let square2 = generateSquareWave(330.0, Duty25, sampleRate, duration)
  let square2Final = square2.mapIt(it * 0.25)
  
  # チャンネル3: 三角波（ベース）
  let triangle = generateTriangleWave(110.0, sampleRate, duration)
  let triangleFinal = triangle.mapIt(it * 0.4)
  
  # ミックス
  let mixed = mixWaveforms(@[square1Final, square2Final, triangleFinal])
  
  # WAVファイルに出力
  writeWaveFile("demo_nes_sound.wav", mixed, sampleRate)
  echo "デモNES音源のWAVファイル 'demo_nes_sound.wav' を生成しました！"

# メイン実行部分
when isMainModule:
  try:
    let args = commandLineParams()
    if args.len >= 1:
      let midiFile = args[0]
      let outputFile = if args.len >= 2: args[1] else: changeFileExt(midiFile, "wav")
      let tempo = if args.len >= 3: parseFloat(args[2]) else: 120.0
      
      if fileExists(midiFile):
        if convertMidiToWav(midiFile, outputFile, tempo):
          quit(0)
        else:
          quit(1)
      else:
        echo "MIDIファイル '", midiFile, "' が見つかりません"
        quit(1)
    else:
      echo "使用方法: program <MIDIファイル> [出力WAVファイル] [テンポ]"
      echo "デモファイルを生成します..."
      generateDemoNESMusic()
      quit(0)
  except:
    echo "エラーが発生しました: ", getCurrentExceptionMsg()
    quit(1)
