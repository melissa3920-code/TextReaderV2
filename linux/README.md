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

## Contextual English pronunciation

Kokoro English voices now receive speech-only pronunciation hints for `read`,
`bass`, `bow`, `wind`, `tear`, and `lead` when nearby context identifies a sense.
The reader uses Misaki's supported `[word](/phonemes/)` syntax; the server's
existing Misaki/POS frontend handles `record`, `present`, `object`, and `conduct`.
No additional reader dependency is required. This requires a Kokoro-FastAPI
server using the English Misaki frontend with inline pronunciation support.
See the [Misaki documentation](https://github.com/hexgrad/misaki#english-usage).

Hints are added only to the outgoing Kokoro English request after cleanup and
chunking. Displayed/saved text, Piper requests, and other languages retain their
existing behavior. Conflicting or absent cues fall back to normal G2P. These are
conservative local rules, not full semantic understanding; clues across chunk
or clause boundaries may be missed. Existing inline overrides take precedence.

Run the reader regression checks from the repository root:

```bash
python -m unittest discover -s linux -p 'test_*.py' -v
python -m compileall -q linux/*.py
```

Run the same unittest command with the Kokoro server environment's Python to
also verify the 22 regression sentences against actual Misaki phonemes. The
Misaki check skips when that dependency is unavailable; GTK integration checks
skip in a server environment without GTK. Live audio and desktop controls still
need a running speech server and desktop session for an end-to-end check.
