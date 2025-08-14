open System
open System.IO
open System.Collections.Generic

// WAVファイルのヘッダー構造体
type WaveHeader = {
    ChunkID: byte[]           // "RIFF"
    ChunkSize: uint32
    Format: byte[]            // "WAVE"
    Subchunk1ID: byte[]       // "fmt "
    Subchunk1Size: uint32
    AudioFormat: uint16
    NumChannels: uint16
    SampleRate: uint32
    ByteRate: uint32
    BlockAlign: uint16
    BitsPerSample: uint16
    Subchunk2ID: byte[]       // "data"
    Subchunk2Size: uint32
}

// MIDIイベント
type MidiEvent = 
    | NoteOn of channel: int * note: int * velocity: int * time: int
    | NoteOff of channel: int * note: int * time: int
    | ProgramChange of channel: int * program: int * time: int
    | Unknown

// MIDIトラック
type MidiTrack = {
    Events: MidiEvent list
}

// MIDIファイル
type MidiFile = {
    Format: int
    TrackCount: int
    TicksPerQuarter: int
    Tracks: MidiTrack list
}

// 音符情報
type Note = {
    MidiNote: int
    Channel: int
    StartTime: float
    Duration: float
    Velocity: int
}

// 矩形波のデューティサイクル（ファミコンの4種類）
type DutyCycle = 
    | Duty12_5  // 12.5%
    | Duty25    // 25%
    | Duty50    // 50%
    | Duty75    // 75%

// デューティサイクルの値を取得
let getDutyCycleValue = function
    | Duty12_5 -> 0.125
    | Duty25 -> 0.25
    | Duty50 -> 0.5
    | Duty75 -> 0.75

// MIDIノート番号を周波数に変換
let midiNoteToFrequency midiNote =
    440.0 * (2.0 ** (float (midiNote - 69) / 12.0))

// Variable Length Quantity (VLQ) の読み込み
let readVLQ (reader: BinaryReader) =
    let mutable value = 0
    let mutable byte = 0
    
    let mutable continue' = true
    while continue' do
        byte <- int (reader.ReadByte())
        value <- (value <<< 7) ||| (byte &&& 0x7F)
        continue' <- (byte &&& 0x80) <> 0
    value

// MIDIヘッダーチャンクの読み込み
let readMidiHeader (reader: BinaryReader) =
    let chunkType = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4))
    let chunkLength = 
        let bytes = reader.ReadBytes(4)
        Array.Reverse(bytes) // ビッグエンディアン
        BitConverter.ToUInt32(bytes, 0)
    
    let format = 
        let bytes = reader.ReadBytes(2)
        Array.Reverse(bytes)
        BitConverter.ToUInt16(bytes, 0)
    
    let trackCount = 
        let bytes = reader.ReadBytes(2)
        Array.Reverse(bytes)
        BitConverter.ToUInt16(bytes, 0)
    
    let ticksPerQuarter = 
        let bytes = reader.ReadBytes(2)
        Array.Reverse(bytes)
        BitConverter.ToUInt16(bytes, 0)
    
    (int format, int trackCount, int ticksPerQuarter)

// MIDIイベントの解析
let parseMidiEvent (reader: BinaryReader) runningStatus deltaTime =
    let mutable status = runningStatus
    let firstByte = reader.ReadByte()
    
    if firstByte >= 128uy then
        status <- int firstByte
    else
        reader.BaseStream.Seek(-1L, SeekOrigin.Current) |> ignore
    
    match status &&& 0xF0 with
    | 0x90 -> // Note On
        let channel = status &&& 0x0F
        let note = int (reader.ReadByte())
        let velocity = int (reader.ReadByte())
        if velocity = 0 then
            NoteOff(channel, note, deltaTime), status
        else
            NoteOn(channel, note, velocity, deltaTime), status
    | 0x80 -> // Note Off
        let channel = status &&& 0x0F
        let note = int (reader.ReadByte())
        reader.ReadByte() |> ignore // velocity
        NoteOff(channel, note, deltaTime), status
    | 0xC0 -> // Program Change
        let channel = status &&& 0x0F
        let program = int (reader.ReadByte())
        ProgramChange(channel, program, deltaTime), status
    | _ -> 
        // その他のイベントはスキップ
        match status with
        | 0xFF -> // Meta event
            let eventType = reader.ReadByte()
            let length = readVLQ reader
            reader.ReadBytes(length) |> ignore
        | _ when status >= 0x80 ->
            // その他のMIDIイベント
            reader.ReadByte() |> ignore
            if (status &&& 0xE0) <> 0xC0 && (status &&& 0xE0) <> 0xD0 then
                reader.ReadByte() |> ignore
        | _ -> ()
        Unknown, status

// MIDIトラックの読み込み
let readMidiTrack (reader: BinaryReader) =
    let chunkType = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4))
    let chunkLength = 
        let bytes = reader.ReadBytes(4)
        Array.Reverse(bytes)
        BitConverter.ToUInt32(bytes, 0)
    
    let endPosition = reader.BaseStream.Position + int64 chunkLength
    let events = ResizeArray<MidiEvent>()
    let mutable runningStatus = 0
    
    while reader.BaseStream.Position < endPosition do
        try
            let deltaTime = readVLQ reader
            let event, newStatus = parseMidiEvent reader runningStatus deltaTime
            runningStatus <- newStatus
            if event <> Unknown then
                events.Add(event)
        with
        | _ -> 
            if reader.BaseStream.Position < endPosition then
                reader.ReadByte() |> ignore
    
    { Events = events |> List.ofSeq }

// MIDIファイル全体の読み込み
let readMidiFile filename =
    use stream = new FileStream(filename, FileMode.Open, FileAccess.Read)
    use reader = new BinaryReader(stream)
    
    try
        let format, trackCount, ticksPerQuarter = readMidiHeader reader
        
        let tracks = [
            for i in 0 .. trackCount - 1 do
                readMidiTrack reader
        ]
        
        Some {
            Format = format
            TrackCount = trackCount
            TicksPerQuarter = ticksPerQuarter
            Tracks = tracks
        }
    with
    | ex ->
        printfn "MIDIファイルの読み込みエラー: %s" ex.Message
        None

// MIDIイベントから音符リストに変換
let eventsToNotes (midiFile: MidiFile) tempo =
    let ticksToSeconds tick = 
        float tick / float midiFile.TicksPerQuarter * 60.0 / tempo
    
    let noteOnEvents = Dictionary<int * int, int * float>() // (channel, note) -> (velocity, startTime)
    let notes = ResizeArray<Note>()
    let mutable currentTick = 0
    
    for track in midiFile.Tracks do
        currentTick <- 0
        for event in track.Events do
            match event with
            | NoteOn(channel, note, velocity, deltaTime) ->
                currentTick <- currentTick + deltaTime
                let startTime = ticksToSeconds currentTick
                noteOnEvents.[(channel, note)] <- (velocity, startTime)
            | NoteOff(channel, note, deltaTime) ->
                currentTick <- currentTick + deltaTime
                let endTime = ticksToSeconds currentTick
                if noteOnEvents.ContainsKey((channel, note)) then
                    let velocity, startTime = noteOnEvents.[(channel, note)]
                    let duration = endTime - startTime
                    if duration > 0.0 then
                        notes.Add({
                            MidiNote = note
                            Channel = channel
                            StartTime = startTime
                            Duration = duration
                            Velocity = velocity
                        })
                    noteOnEvents.Remove((channel, note)) |> ignore
            | _ -> ()
    
    notes |> List.ofSeq

// 波形生成関数
let generateSquareWave frequency duty sampleRate duration =
    let samples = int (float sampleRate * duration)
    let dutyValue = getDutyCycleValue duty
    
    [| for i in 0 .. samples - 1 do
        let t = float i / float sampleRate
        let phase = (t * frequency) % 1.0
        if phase < dutyValue then 1.0 else -1.0 |]

let generateTriangleWave frequency sampleRate duration =
    let samples = int (float sampleRate * duration)
    
    [| for i in 0 .. samples - 1 do
        let t = float i / float sampleRate
        let phase = (t * frequency) % 1.0
        if phase < 0.5 then
            4.0 * phase - 1.0
        else
            3.0 - 4.0 * phase |]

let generateNoise isShort sampleRate duration =
    let samples = int (float sampleRate * duration)
    let mutable shift = 1us
    
    [| for i in 0 .. samples - 1 do
        let bit0 = shift &&& 1us
        let bit1 = if isShort then (shift >>> 6) &&& 1us else (shift >>> 1) &&& 1us
        let feedback = bit0 ^^^ bit1
        shift <- (shift >>> 1) ||| (feedback <<< 14)
        
        if bit0 = 1us then 1.0 else -1.0 |]

// 音符を波形に変換
let noteToWaveform note sampleRate totalDuration =
    let frequency = midiNoteToFrequency note.MidiNote
    let volume = float note.Velocity / 127.0 * 0.7
    let startSample = int (note.StartTime * float sampleRate)
    let noteSamples = int (note.Duration * float sampleRate)
    let totalSamples = int (totalDuration * float sampleRate)
    
    let waveform = 
        match note.Channel % 4 with
        | 0 -> generateSquareWave frequency Duty50 sampleRate note.Duration
        | 1 -> generateSquareWave frequency Duty25 sampleRate note.Duration  
        | 2 -> generateTriangleWave frequency sampleRate note.Duration
        | _ -> generateNoise false sampleRate note.Duration
    
    let result = Array.create totalSamples 0.0
    let endSample = min (startSample + noteSamples) totalSamples
    
    for i in startSample .. endSample - 1 do
        if i - startSample < waveform.Length then
            result.[i] <- waveform.[i - startSample] * volume
    
    result

// 複数の波形をミックス
let mixWaveforms waveforms =
    if Array.isEmpty waveforms then
        [| |]
    else
        let maxLength = waveforms |> Array.map Array.length |> Array.max
        [| for i in 0 .. maxLength - 1 do
            let sum = waveforms |> Array.sumBy (fun w -> 
                if i < Array.length w then w.[i] else 0.0)
            sum / float waveforms.Length |]

// 16ビット整数に変換
let convertTo16Bit samples =
    samples 
    |> Array.map (fun s -> 
        let clamped = max -1.0 (min 1.0 s)
        int16 (clamped * 32767.0))

// WAVヘッダー作成
let createWaveHeader sampleRate numChannels bitsPerSample dataSize =
    let byteRate = uint32 (sampleRate * numChannels * bitsPerSample / 8)
    let blockAlign = uint16 (numChannels * bitsPerSample / 8)
    
    {
        ChunkID = System.Text.Encoding.ASCII.GetBytes("RIFF")
        ChunkSize = uint32 (36 + dataSize)
        Format = System.Text.Encoding.ASCII.GetBytes("WAVE")
        Subchunk1ID = System.Text.Encoding.ASCII.GetBytes("fmt ")
        Subchunk1Size = 16u
        AudioFormat = 1us
        NumChannels = uint16 numChannels
        SampleRate = uint32 sampleRate
        ByteRate = byteRate
        BlockAlign = blockAlign
        BitsPerSample = uint16 bitsPerSample
        Subchunk2ID = System.Text.Encoding.ASCII.GetBytes("data")
        Subchunk2Size = uint32 dataSize
    }

// WAVファイル書き込み
let writeWaveFile filename samples sampleRate =
    use stream = new FileStream(filename, FileMode.Create)
    use writer = new BinaryWriter(stream)
    
    let sampleData = convertTo16Bit samples
    let dataSize = sampleData.Length * 2
    let header = createWaveHeader sampleRate 1 16 dataSize
    
    // ヘッダー書き込み
    writer.Write(header.ChunkID)
    writer.Write(header.ChunkSize)
    writer.Write(header.Format)
    writer.Write(header.Subchunk1ID)
    writer.Write(header.Subchunk1Size)
    writer.Write(header.AudioFormat)
    writer.Write(header.NumChannels)
    writer.Write(header.SampleRate)
    writer.Write(header.ByteRate)
    writer.Write(header.BlockAlign)
    writer.Write(header.BitsPerSample)
    writer.Write(header.Subchunk2ID)
    writer.Write(header.Subchunk2Size)
    
    // サンプルデータ書き込み
    for sample in sampleData do
        writer.Write(sample)

// MIDIからWAV変換のメイン関数
let convertMidiToWav midiFilename wavFilename tempo =
    match readMidiFile midiFilename with
    | Some midiFile ->
        printfn "MIDIファイルを読み込みました: Format %d, %d tracks, %d ticks/quarter" 
                midiFile.Format midiFile.TrackCount midiFile.TicksPerQuarter
        
        let notes = eventsToNotes midiFile tempo
        printfn "%d個の音符を検出しました" notes.Length
        
        if notes.IsEmpty then
            printfn "音符が見つかりませんでした"
            false
        else
            let totalDuration = 
                notes 
                |> List.map (fun n -> n.StartTime + n.Duration) 
                |> List.max
                |> fun d -> d + 1.0 // 余裕を持たせる
            
            let sampleRate = 44100
            
            printfn "総演奏時間: %.2f秒" totalDuration
            printfn "波形を生成中..."
            
            let waveforms = 
                notes 
                |> List.map (fun note -> noteToWaveform note sampleRate totalDuration)
                |> Array.ofList
            
            let mixed = mixWaveforms waveforms
            writeWaveFile wavFilename mixed sampleRate
            
            printfn "WAVファイル '%s' を生成しました！" wavFilename
            true
    | None ->
        printfn "MIDIファイルの読み込みに失敗しました"
        false

// デモ用のサンプル生成
let generateDemoNESMusic () =
    let sampleRate = 44100
    let duration = 2.0
    
    // チャンネル1: 矩形波（メロディ）
    let square1 = generateSquareWave 440.0 Duty50 sampleRate duration
    let square1Final = square1 |> Array.map (fun s -> s * 0.3)
    
    // チャンネル2: 矩形波（ハーモニー）
    let square2 = generateSquareWave 330.0 Duty25 sampleRate duration
    let square2Final = square2 |> Array.map (fun s -> s * 0.25)
    
    // チャンネル3: 三角波（ベース）
    let triangle = generateTriangleWave 110.0 sampleRate duration
    let triangleFinal = triangle |> Array.map (fun s -> s * 0.4)
    
    // ミックス
    let mixed = mixWaveforms [|square1Final; square2Final; triangleFinal|]
    
    // WAVファイルに出力
    writeWaveFile "demo_nes_sound.wav" mixed sampleRate
    printfn "デモNES音源のWAVファイル 'demo_nes_sound.wav' を生成しました！"

// メイン実行関数
[<EntryPoint>]
let main args =
    try
        if args.Length >= 1 then
            let midiFile = args.[0]
            let outputFile = 
                if args.Length >= 2 then args.[1] 
                else Path.ChangeExtension(midiFile, ".wav")
            let tempo = 
                if args.Length >= 3 then float args.[2] 
                else 120.0
            
            if File.Exists(midiFile) then
                if convertMidiToWav midiFile outputFile tempo then 0 else 1
            else
                printfn "MIDIファイル '%s' が見つかりません" midiFile
                1
        else
            printfn "使用方法: program.exe <MIDIファイル> [出力WAVファイル] [テンポ]"
            printfn "デモファイルを生成します..."
            generateDemoNESMusic()
            0
    with
    | ex -> 
        printfn "エラーが発生しました: %s" ex.Message
        1
