# MIDI to Chiptune

MIDIデータをNES(ファミコン)の音源をエミュレートしたWAVEデータに変換するCLIです。

以下の言語の実装とコンパイルオプションが含まれています。

|言語|バージョン|非同期処理|AOTコンパイル|単一ファイル|
|---|---|---|---|---|
|C#|.NET 9|❌|✅|✅|
|F#|.NET 9|❌|❌|✅|
|Go|1.25.0|❌|✅|✅|

## C#

### Run

```shell
dotnet run --project ./csharp/Midi2Chiptune ./dragon_quest_overture.mid ./dragon_quest_overture.cs.wav
```

### Compile

```shell
dotnet publish ./csharp/Midi2Chiptune -o ./publish/csharp
```

## F#

### Run

```shell
dotnet run --project ./fsharp/Midi2Chiptune ./dragon_quest_overture.mid ./dragon_quest_overture.fs.wav
```

### Compile

```shell
dotnet publish ./fsharp/Midi2Chiptune -o ./publish/fsharp
```

## Go

### Run

```shell
go run ./golang/main.go ./dragon_quest_overture.mid ./dragon_quest_overture.go.wav
```

### Compile

```shell
go build -o ./publish/golang/Midi2Chiptune ./golang/main.go
```
