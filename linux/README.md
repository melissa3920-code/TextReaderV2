# TextReaderV2 for Omarchy

This is a native GTK 4 port of the Windows WPF application. It supports Piper
and Kokoro-FastAPI local speech servers with GStreamer audio playback. The
Windows project is unchanged.

## One-time Piper setup

From this `linux` directory, create an isolated Python environment and install
Piper with its HTTP-server dependencies:

```bash
python -m venv --system-site-packages .venv
.venv/bin/pip install 'piper-tts[http]'
```

You may reuse an existing `.onnx` model from Windows. Its matching
`.onnx.json` file must be beside it. Alternatively, download a voice:

```bash
.venv/bin/python -m piper.download_voices en_US-lessac-medium
```

## Run

```bash
./run-text-reader
```

Click **Connect / Start Piper**. A voice in the bundled `voices` directory is
used automatically; otherwise, the app asks you to select an `.onnx` voice
model. It remembers the model, voice, speed, and volume in
`~/.config/text-reader-v2/settings.json`.

If a Piper server is already running at `127.0.0.1:5000`, the app connects to
it instead of starting another one.

## Optional Kokoro setup

Start a [Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) server at
its default address, `http://127.0.0.1:8880`, then select **Kokoro** from the
Engine list and click **Connect to Kokoro**. Available Kokoro voices are loaded
from the server automatically. Unlike Piper, Text Reader does not start or
stop the Kokoro server.

The reader prepares the next audio section while the current section plays to
reduce pauses between sections. The **Special characters** list can preserve
the input, remove Unicode symbols, or speak common symbols (for example,
`&` as “and” and `%` as “percent”). These choices and the voice selected for
each engine are saved in the settings file.
