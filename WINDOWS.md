# TextReaderV2 for Windows

The Windows WPF app supports Piper and Kokoro-FastAPI.

## Run

Open TextReaderV2.exe, choose Kokoro from the Engine list, then choose a voice.
The app connects automatically when you switch engines. If the server was not
running yet, start it and click Connect to Kokoro.

Kokoro-FastAPI must be running on this computer at http://127.0.0.1:8880.
The Firefox Kokoro Reader extension can share that same server. TextReaderV2
does not start or stop Kokoro. Use the Start-Kokoro launcher provided with your
server setup. Stop the server only after both readers are finished using it.

Piper remains available on port 5000 with its existing Connect / Start Piper
button and ONNX model picker. The selected engine and each engine's voice are
saved separately. Existing Piper settings are retained.

The Windows x64 executable includes its .NET runtime. Visual Studio and a
separate .NET installation are not required to run it.

Each section includes a 300 ms silent lead-in before speech to protect the
first word when the playback device starts. The generated speech samples are
preserved, and WAV lengths are normalized before playback.

## Build from source

Install the .NET 10 SDK, then run from the repository root:

    dotnet publish TextReaderV2/TextReaderV2.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/windows

Run API regression checks:

    dotnet run --project tests/SpeechApi.Tests.csproj

To also verify voice loading and WAV generation against a running local Kokoro server:

    dotnet run --project tests/SpeechApi.Tests.csproj -- --live

This update adds Windows Kokoro connectivity, voice selection, speed control,
and speech playback. The Linux files are unchanged; the Linux-specific text
cleanup and pronunciation rules have not been ported in this update.
