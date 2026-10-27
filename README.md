# TextReaderV2

A desktop text reader with local **Piper** and **Kokoro-FastAPI** speech engines.
The Windows app uses WPF; the Linux app uses GTK 4 and GStreamer.

## Install on Windows

### 1. Get TextReaderV2

Download this repository using **Code > Download ZIP**, then extract it.
Alternatively, clone it:

```powershell
git clone https://github.com/melissa3920-code/TextReaderV2.git
cd TextReaderV2
```

There are currently no prebuilt GitHub releases. To build the Windows app,
install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
for Windows x64. **Visual Studio is not required.** Open PowerShell in the
extracted repository folder (the folder containing `TextReaderV2.slnx`), then run:

```powershell
dotnet publish .\TextReaderV2\TextReaderV2.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\windows
```

Start the app:

```powershell
.\publish\windows\TextReaderV2.exe
```

The published executable includes its .NET runtime. After building, you can
copy it to another Windows x64 computer without installing the SDK there.
The speech server is installed separately, as described below.

### 2. Set up a speech engine

You only need one engine. If Kokoro-FastAPI is already running for a browser
extension, use that same server; you do not need another installation.

#### Kokoro-FastAPI

Run [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) on this computer
at `http://127.0.0.1:8880`.

One option is to install and start [Docker Desktop](https://www.docker.com/products/docker-desktop/),
switch to Linux containers if needed, then run this CPU version in PowerShell:

```powershell
docker run -d --name kokoro-fastapi -p 127.0.0.1:8880:8880 ghcr.io/remsky/kokoro-fastapi-cpu:latest
```

Wait for the model to finish loading. Open
[the server's API documentation](http://127.0.0.1:8880/docs) to check it is ready.
For later sessions, start the existing container with:

```powershell
docker start kokoro-fastapi
```

For a native Windows installation or GPU setup, follow the
[upstream installation instructions](https://github.com/remsky/Kokoro-FastAPI#readme).
Installing only `pip install kokoro` does **not** install the FastAPI server.

In TextReaderV2:

1. Choose **Kokoro** from **Engine**. The app connects and loads the voice list.
2. If the server was not ready, click **Connect to Kokoro** after starting it.
3. Choose a voice, paste text, and click **Read All** or **Read Selection**.

The Windows app uses `/v1/audio/voices` and `/v1/audio/speech` on port 8880.
It does not start or stop Kokoro-FastAPI. Keep the server running while reading.
The Firefox Kokoro Reader addon can share it; that addon's API URL is
`http://127.0.0.1:8880/v1/`.

#### Piper

Install Python and Piper's HTTP server dependencies:

```powershell
python -m pip install "piper-tts[http]"
python -m piper.download_voices en_US-lessac-medium
```

Keep the downloaded `.onnx` model and matching `.onnx.json` file together.
Choose **Piper** in TextReaderV2 and click **Connect / Start Piper**. Select the
`.onnx` file when prompted. The app can also connect to a Piper server already
running at `http://127.0.0.1:5000`.

## Using the Windows app

Use **Pause**, **Resume**, **Previous**, **Next**, and **Stop** to control reading.
While a section plays, the app prepares the next section in the background to
reduce pauses. Changing the voice or speed discards outdated prefetched audio;
Stop, navigation, and engine changes cancel unneeded requests.

Speed applies to generated speech; volume controls playback. Use **Lead-in** to choose the silence before each section: 0, 50, 100, 150,
200, 300, or 500 ms. The default is **100 ms**. If opening words are clipped,
increase it to 200 or 300 ms. Changes apply to the next section and are saved
for future sessions. The app remembers
the selected engine, a voice for each engine, speed, volume, lead-in delay, and Piper model path.
Existing Piper settings are retained. Settings are stored in
`%LOCALAPPDATA%\TextReader\settings.json`.

If Kokoro will not connect, check that `http://127.0.0.1:8880/docs` opens and
that the server has finished loading, then reconnect. If `dotnet` is not found,
install the **SDK** (not just the runtime), reopen PowerShell, and try again.

## English pronunciation of “read”

With an English Kokoro voice, the Windows app adds speech-only pronunciation
hints when there are clear tense cues:

- **Read the pamphlet** and **I will read the pamphlet** use “reed.”
- **The pamphlet read, "All will eat."** and **I have read the pamphlet** use “red.”

**I read the pamphlet** can mean either present or past tense. For an ambiguous
sentence, type **I read(red) the pamphlet** for the past tense, or
**I read(reed) the pamphlet** for the present tense. A space before the
parentheses is also accepted. These pronunciation markers are not spoken.

Automatic hints affect only the outgoing speech request; displayed and saved
text are unchanged. Existing inline pronunciation overrides are respected.
Unclear cases are left to Kokoro's normal pronunciation. Piper and non-English
voices do not receive these English hints.

## Install on Linux

See [linux/README.md](linux/README.md) for the GTK 4 app, dependencies, Piper
setup, and Kokoro setup. The Linux app also includes text cleanup and contextual
English pronunciation hints. The Windows app currently adds hints for “read”; Linux has additional
word-specific pronunciation rules and its own cleanup features.

## Tests

With the .NET 10 SDK installed, run from the repository root:

```powershell
dotnet run --project .\tests\SpeechApi.Tests.csproj
```

To also test voice loading and WAV generation against a running local
Kokoro-FastAPI server:

```powershell
dotnet run --project .\tests\SpeechApi.Tests.csproj -- --live
```

See [WINDOWS.md](WINDOWS.md) for additional Windows build notes and
[linux/README.md](linux/README.md) for Linux tests.
