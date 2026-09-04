#!/usr/bin/env python3
"""Native GTK 4 version of TextReaderV2 for Omarchy/Linux."""

from __future__ import annotations

import json
import os
import re
import signal
import subprocess
import sys
import tempfile
import threading
import time
import unicodedata
from pathlib import Path
from typing import Any

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Gst", "1.0")
from gi.repository import GLib, Gst, Gtk  # noqa: E402

import requests  # noqa: E402


APP_ID = "com.local.TextReaderV2"
PIPER_BASE_URL = "http://127.0.0.1:5000"
KOKORO_BASE_URL = "http://127.0.0.1:8880"
HTTP_TIMEOUT = (3, 300)

SYMBOL_TRANSLATIONS = {
    "&": " and ", "@": " at ", "#": " hash ", "%": " percent ",
    "+": " plus ", "=": " equals ", "$": " dollars ", "£": " pounds ",
    "€": " euros ", "©": " copyright ", "®": " registered trademark ",
    "™": " trademark ", "°": " degrees ", "×": " times ", "÷": " divided by ",
    "<": " less than ", ">": " greater than ", "|": " or ",
}


def process_special_characters(text: str, mode: str) -> str:
    """Keep, remove, or speak non-punctuation symbols in text."""
    if mode == "translate":
        text = "".join(SYMBOL_TRANSLATIONS.get(character, character) for character in text)
    elif mode == "remove":
        text = "".join(
            character for character in text
            if not unicodedata.category(character).startswith(("S", "C"))
            or character in "\n\t"
        )
    return re.sub(r"[ \t]+", " ", text)


def create_speech_chunks(text: str, maximum_characters: int = 700) -> list[str]:
    """Split text at paragraphs, sentences, then words for Piper."""
    normalized = text.replace("\r\n", "\n").replace("\r", "\n").strip()
    if not normalized:
        return []

    chunks: list[str] = []
    for raw_paragraph in re.split(r"\n\s*\n|\n", normalized):
        paragraph = re.sub(r"\s+", " ", raw_paragraph.strip())
        if not paragraph:
            continue
        if len(paragraph) <= maximum_characters:
            chunks.append(paragraph)
            continue

        current = ""
        for sentence in re.split(r"(?<=[.!?])\s+", paragraph):
            sentence = sentence.strip()
            if not sentence:
                continue
            if len(sentence) > maximum_characters:
                if current:
                    chunks.append(current)
                    current = ""
                word_chunk = ""
                for word in sentence.split():
                    proposed = f"{word_chunk} {word}".strip()
                    if len(proposed) > maximum_characters and word_chunk:
                        chunks.append(word_chunk)
                        word_chunk = word
                    else:
                        word_chunk = proposed
                if word_chunk:
                    chunks.append(word_chunk)
                continue

            proposed = f"{current} {sentence}".strip()
            if len(proposed) > maximum_characters and current:
                chunks.append(current)
                current = sentence
            else:
                current = proposed
        if current:
            chunks.append(current)

    return chunks


class TextReaderWindow(Gtk.ApplicationWindow):
    def __init__(self, application: Gtk.Application) -> None:
        super().__init__(application=application, title="Text Reader")
        self.set_default_size(1180, 760)
        self.set_size_request(900, 520)

        self.session = requests.Session()
        self.piper_process: subprocess.Popen[bytes] | None = None
        self.audio_path: Path | None = None
        self.prefetched_audio: dict[int, Path] = {}
        self.pending_chunks: set[int] = set()
        self.waiting_for_chunk: int | None = None
        self.speech_chunks: list[str] = []
        self.chunk_index = -1
        self.request_generation = 0
        self.reading = False
        self.paused = False
        self.loading_settings = True

        config_home = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
        self.settings_path = config_home / "text-reader-v2" / "settings.json"
        self.settings: dict[str, Any] = {
            "piper_model_path": "",
            "engine": "piper",
            "voice_name": "",
            "voices": {},
            "special_characters": "keep",
            "rate": 0,
            "volume": 100,
        }
        self._load_settings()

        Gst.init(None)
        self.player = Gst.ElementFactory.make("playbin", "player")
        bus = self.player.get_bus()
        bus.add_signal_watch()
        bus.connect("message", self._on_player_message)

        self._build_ui()
        self.loading_settings = False
        self.connect("close-request", self._on_close_request)
        GLib.idle_add(self._initial_connect)

    def _build_ui(self) -> None:
        css = Gtk.CssProvider()
        css.load_from_data(b"""
            textview { font-family: 'Atkinson Hyperlegible', Sans; font-size: 20px; }
            .connected { background: #2e8b57; color: white; }
            .disconnected { background: #cd5c5c; color: white; }
            .connecting { background: #daa520; color: black; }
            .status { color: #666; }
        """)
        Gtk.StyleContext.add_provider_for_display(
            self.get_display(), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )

        root = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        root.set_margin_top(16)
        root.set_margin_bottom(16)
        root.set_margin_start(16)
        root.set_margin_end(16)
        self.set_child(root)

        toolbar = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10)
        root.append(toolbar)

        open_button = Gtk.Button(label="Open…")
        open_button.connect("clicked", self._open_file)
        toolbar.append(open_button)
        save_button = Gtk.Button(label="Save As…")
        save_button.connect("clicked", self._save_file)
        toolbar.append(save_button)

        toolbar.append(Gtk.Separator(orientation=Gtk.Orientation.VERTICAL))
        toolbar.append(Gtk.Label(label="Engine:"))
        self.engine_combo = Gtk.ComboBoxText()
        self.engine_combo.append("piper", "Piper")
        self.engine_combo.append("kokoro", "Kokoro")
        self.engine_combo.set_active_id(str(self.settings["engine"]))
        self.engine_combo.connect("changed", self._engine_changed)
        toolbar.append(self.engine_combo)

        toolbar.append(Gtk.Label(label="Voice:"))
        self.voice_combo = Gtk.ComboBoxText()
        self.voice_combo.set_size_request(210, -1)
        self.voice_combo.connect("changed", self._voice_changed)
        toolbar.append(self.voice_combo)

        toolbar.append(Gtk.Label(label="Speed:"))
        self.rate = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, -10, 10, 1)
        self.rate.set_value(float(self.settings["rate"]))
        self.rate.set_size_request(130, -1)
        self.rate.set_draw_value(True)
        self.rate.connect("value-changed", self._setting_changed)
        toolbar.append(self.rate)

        toolbar.append(Gtk.Label(label="Volume:"))
        self.volume = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 100, 5)
        self.volume.set_value(float(self.settings["volume"]))
        self.volume.set_size_request(140, -1)
        self.volume.set_draw_value(True)
        self.volume.connect("value-changed", self._volume_changed)
        toolbar.append(self.volume)

        self.connect_button = Gtk.Button(label="Connect")
        self.connect_button.add_css_class("disconnected")
        self.connect_button.connect("clicked", self._connect_clicked)
        toolbar.append(self.connect_button)

        options = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10)
        root.append(options)
        options.append(Gtk.Label(label="Special characters:"))
        self.symbol_combo = Gtk.ComboBoxText()
        self.symbol_combo.append("keep", "Keep as written")
        self.symbol_combo.append("remove", "Remove symbols")
        self.symbol_combo.append("translate", "Speak common symbols")
        self.symbol_combo.set_active_id(str(self.settings["special_characters"]))
        self.symbol_combo.connect("changed", self._symbol_mode_changed)
        options.append(self.symbol_combo)

        scroller = Gtk.ScrolledWindow(vexpand=True)
        scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.text_view = Gtk.TextView()
        self.text_view.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.text_view.set_top_margin(12)
        self.text_view.set_bottom_margin(12)
        self.text_view.set_left_margin(12)
        self.text_view.set_right_margin(12)
        scroller.set_child(self.text_view)
        root.append(scroller)

        progress_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10)
        self.progress = Gtk.ProgressBar(hexpand=True)
        self.progress_label = Gtk.Label(label="0 / 0")
        progress_row.append(self.progress)
        progress_row.append(self.progress_label)
        root.append(progress_row)

        controls = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        root.append(controls)
        for label, callback in (
            ("Paste", self._paste),
            ("Read All", self._read_all),
            ("Read Selection", self._read_selection),
            ("Previous", self._previous),
            ("Pause", self._pause),
            ("Resume", self._resume),
            ("Next", self._next),
            ("Stop", self._stop_clicked),
            ("Clear", self._clear),
        ):
            button = Gtk.Button(label=label)
            button.connect("clicked", callback)
            controls.append(button)

        self.status = Gtk.Label(label="Ready.", xalign=0, wrap=True)
        self.status.add_css_class("status")
        root.append(self.status)

    def _set_status(self, message: str) -> None:
        self.status.set_text(message)

    def _set_connection_state(self, state: str) -> None:
        for css_class in ("connected", "disconnected", "connecting"):
            self.connect_button.remove_css_class(css_class)
        self.connect_button.add_css_class(state)
        engine = self._engine().title()
        labels = {
            "connected": f"{engine} Connected",
            "connecting": f"Connecting to {engine}…",
            "disconnected": "Connect / Start Piper" if self._engine() == "piper" else "Connect to Kokoro",
        }
        self.connect_button.set_label(labels[state])

    def _initial_connect(self) -> bool:
        threading.Thread(target=self._connect_worker, args=(False,), daemon=True).start()
        return GLib.SOURCE_REMOVE

    def _connect_clicked(self, _button: Gtk.Button) -> None:
        self.connect_button.set_sensitive(False)
        threading.Thread(target=self._connect_or_start_worker, daemon=True).start()

    def _connect_worker(self, report_failure: bool = True) -> None:
        if self._engine() == "kokoro":
            self._connect_kokoro_worker(report_failure)
            return
        try:
            response = self.session.get(f"{PIPER_BASE_URL}/info", timeout=3)
            response.raise_for_status()
            info = response.json()
            default_voice = info.get("voice", {}).get("name", "Piper default voice")
            voices = [default_voice]
            try:
                voices_response = self.session.get(f"{PIPER_BASE_URL}/voices", timeout=3)
                voices_response.raise_for_status()
                voices.extend(voices_response.json().keys())
            except (requests.RequestException, ValueError):
                pass
            GLib.idle_add(self._connected, sorted(set(filter(None, voices))), default_voice)
        except (requests.RequestException, ValueError):
            GLib.idle_add(self._disconnected, report_failure)

    def _connected(self, voices: list[str], default_voice: str) -> bool:
        wanted = self._saved_voice()
        self.voice_combo.remove_all()
        selected = 0
        for index, voice in enumerate(voices):
            self.voice_combo.append_text(voice)
            if voice == wanted or (not wanted and voice == default_voice):
                selected = index
        self.voice_combo.set_active(selected)
        self._set_connection_state("connected")
        self.connect_button.set_sensitive(True)
        self._set_status(f"{self._engine().title()} is connected.")
        return GLib.SOURCE_REMOVE

    def _disconnected(self, report_failure: bool) -> bool:
        self._set_connection_state("disconnected")
        self.connect_button.set_sensitive(True)
        if report_failure:
            port = "5000" if self._engine() == "piper" else "8880"
            self._set_status(f"Could not connect to {self._engine().title()} on port {port}.")
        else:
            self._set_status("Click Connect / Start Piper.")
        return GLib.SOURCE_REMOVE

    def _connect_or_start_worker(self) -> None:
        if self._engine() == "kokoro":
            self._connect_worker()
            return
        try:
            response = self.session.get(f"{PIPER_BASE_URL}/info", timeout=2)
            response.raise_for_status()
            self._connect_worker()
            return
        except requests.RequestException:
            pass

        model_path = Path(str(self.settings.get("piper_model_path", "")))
        if not model_path.is_file():
            bundled_voices = sorted((Path(__file__).parent / "voices").glob("*.onnx"))
            if bundled_voices:
                model_path = bundled_voices[0]
                self.settings["piper_model_path"] = str(model_path)
                GLib.idle_add(self._save_settings)
            else:
                GLib.idle_add(self._choose_model)
                return
        self._start_piper_worker(model_path)

    def _connect_kokoro_worker(self, report_failure: bool = True) -> None:
        try:
            response = self.session.get(f"{KOKORO_BASE_URL}/v1/audio/voices", timeout=3)
            response.raise_for_status()
            data = response.json()
            raw_voices = data.get("voices", data.get("data", [])) if isinstance(data, dict) else data
            voices = []
            for voice in raw_voices:
                name = voice.get("id", voice.get("name")) if isinstance(voice, dict) else voice
                if name:
                    voices.append(str(name))
            if not voices:
                raise ValueError("Kokoro returned no voices")
            default_voice = "af_heart" if "af_heart" in voices else voices[0]
            GLib.idle_add(self._connected, sorted(set(voices)), default_voice)
        except (requests.RequestException, ValueError, TypeError):
            GLib.idle_add(self._disconnected, report_failure)

    def _choose_model(self) -> bool:
        self.connect_button.set_sensitive(True)
        dialog = Gtk.FileChooserNative(
            title="Select a Piper voice model",
            transient_for=self,
            action=Gtk.FileChooserAction.OPEN,
            accept_label="Select",
            cancel_label="Cancel",
        )
        file_filter = Gtk.FileFilter()
        file_filter.set_name("Piper voice model (*.onnx)")
        file_filter.add_pattern("*.onnx")
        dialog.add_filter(file_filter)
        dialog.connect("response", self._model_chosen)
        dialog.show()
        return GLib.SOURCE_REMOVE

    def _model_chosen(self, dialog: Gtk.FileChooserNative, response: int) -> None:
        if response != Gtk.ResponseType.ACCEPT:
            self._set_status("Piper was not started.")
            return
        selected = dialog.get_file()
        path = Path(selected.get_path()) if selected and selected.get_path() else None
        if not path:
            self._set_status("A local Piper model file is required.")
            return
        self.settings["piper_model_path"] = str(path)
        self._save_settings()
        self.connect_button.set_sensitive(False)
        threading.Thread(target=self._start_piper_worker, args=(path,), daemon=True).start()

    def _start_piper_worker(self, model_path: Path) -> None:
        GLib.idle_add(self._set_connection_state, "connecting")
        GLib.idle_add(self._set_status, "Starting the local Piper server…")
        project_python = Path(__file__).parent / ".venv" / "bin" / "python"
        python = str(project_python) if project_python.is_file() else sys.executable
        command = [
            python, "-m", "piper.http_server", "-m", str(model_path),
            "--host", "127.0.0.1", "--port", "5000",
        ]
        try:
            self.piper_process = subprocess.Popen(
                command,
                cwd=model_path.parent,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                start_new_session=True,
            )
        except OSError as error:
            GLib.idle_add(self._start_failed, f"Could not start Python: {error}")
            return

        for _ in range(40):
            if self.piper_process.poll() is not None:
                GLib.idle_add(self._start_failed, "Piper exited before starting.")
                return
            try:
                response = self.session.get(f"{PIPER_BASE_URL}/info", timeout=1)
                response.raise_for_status()
                self._connect_worker()
                return
            except requests.RequestException:
                time.sleep(0.5)
        GLib.idle_add(self._start_failed, "Piper did not become available on port 5000.")

    def _start_failed(self, detail: str) -> bool:
        self._set_connection_state("disconnected")
        self.connect_button.set_sensitive(True)
        if "No module named" in detail and "piper" in detail:
            detail = "Piper TTS is not installed. Follow linux/README.md, then try again."
        self._set_status(detail)
        return GLib.SOURCE_REMOVE

    def _buffer_text(self, selection_only: bool = False) -> str:
        buffer = self.text_view.get_buffer()
        if selection_only:
            bounds = buffer.get_selection_bounds()
            if not bounds:
                return ""
            return buffer.get_text(bounds[0], bounds[1], True)
        return buffer.get_text(buffer.get_start_iter(), buffer.get_end_iter(), True)

    def _read_all(self, _button: Gtk.Button) -> None:
        self._start_reading(self._buffer_text())

    def _read_selection(self, _button: Gtk.Button) -> None:
        text = self._buffer_text(selection_only=True)
        if not text.strip():
            self._set_status("Select some text first.")
            return
        self._start_reading(text)

    def _start_reading(self, text: str) -> None:
        if not text.strip():
            self._set_status("Paste or type something to read.")
            return
        mode = self.symbol_combo.get_active_id() or "keep"
        text = process_special_characters(text, mode)
        if not text.strip():
            self._set_status("There is no readable text after removing special characters.")
            return
        self._stop(clear_queue=True)
        self.speech_chunks = create_speech_chunks(text)
        self.chunk_index = 0
        self.reading = True
        self._update_progress()
        self._synthesize_current()

    def _synthesize_current(self) -> None:
        if not self.reading or not 0 <= self.chunk_index < len(self.speech_chunks):
            self._finish()
            return
        self.player.set_state(Gst.State.NULL)
        self._delete_audio()
        self._update_progress()
        cached = self.prefetched_audio.pop(self.chunk_index, None)
        if cached:
            self._play_audio(cached, self.request_generation, self.chunk_index)
            return
        self.waiting_for_chunk = self.chunk_index
        self._set_status(
            f"Generating section {self.chunk_index + 1} of {len(self.speech_chunks)}…"
        )
        self._request_chunk(self.chunk_index)

    def _request_chunk(self, index: int) -> None:
        if (not self.reading or index in self.pending_chunks or
                index in self.prefetched_audio or not 0 <= index < len(self.speech_chunks)):
            return
        self.pending_chunks.add(index)
        generation = self.request_generation
        engine = self._engine()
        voice = self.voice_combo.get_active_text() or ""
        speed = max(0.5, min(2.0, 1.0 + self.rate.get_value() * 0.05))
        if engine == "kokoro":
            url = f"{KOKORO_BASE_URL}/v1/audio/speech"
            payload: dict[str, Any] = {
                "model": "kokoro", "input": self.speech_chunks[index],
                "voice": voice or "af_heart", "response_format": "wav", "speed": speed,
            }
        else:
            url = f"{PIPER_BASE_URL}/synthesize"
            payload = {
                "text": self.speech_chunks[index],
                "length_scale": 1.0 / speed,
            }
            if voice and voice != "Piper default voice":
                payload["voice"] = voice
        threading.Thread(
            target=self._synthesize_worker,
            args=(url, payload, generation, index), daemon=True,
        ).start()

    def _synthesize_worker(
        self, url: str, payload: dict[str, Any], generation: int, index: int
    ) -> None:
        try:
            response = self.session.post(url, json=payload, timeout=HTTP_TIMEOUT)
            response.raise_for_status()
            with tempfile.NamedTemporaryFile(
                prefix="TextReader_", suffix=".wav", delete=False
            ) as audio_file:
                audio_file.write(response.content)
                path = Path(audio_file.name)
            GLib.idle_add(self._audio_ready, path, generation, index)
        except requests.RequestException as error:
            GLib.idle_add(self._speech_failed, str(error), generation, index)

    def _audio_ready(self, path: Path, generation: int, index: int) -> bool:
        self.pending_chunks.discard(index)
        if generation != self.request_generation or not self.reading:
            path.unlink(missing_ok=True)
        elif index == self.waiting_for_chunk and index == self.chunk_index:
            self._play_audio(path, generation, index)
        else:
            old_path = self.prefetched_audio.pop(index, None)
            if old_path:
                old_path.unlink(missing_ok=True)
            self.prefetched_audio[index] = path
        return GLib.SOURCE_REMOVE

    def _play_audio(self, path: Path, generation: int, index: int) -> bool:
        if generation != self.request_generation or not self.reading:
            path.unlink(missing_ok=True)
            return GLib.SOURCE_REMOVE
        if index != self.chunk_index:
            self.prefetched_audio[index] = path
            return GLib.SOURCE_REMOVE
        self.waiting_for_chunk = None
        self.audio_path = path
        self.player.set_property("uri", path.as_uri())
        self.player.set_property("volume", self.volume.get_value() / 100.0)
        self.player.set_state(Gst.State.PLAYING)
        self.paused = False
        self._set_status(f"Reading section {self.chunk_index + 1} of {len(self.speech_chunks)}…")
        self._update_progress(mark_current=True)
        self._request_chunk(self.chunk_index + 1)
        return GLib.SOURCE_REMOVE

    def _speech_failed(self, detail: str, generation: int, index: int) -> bool:
        self.pending_chunks.discard(index)
        if generation == self.request_generation and index == self.chunk_index:
            self.reading = False
            self._set_connection_state("disconnected")
            self._set_status(f"The speech request failed: {detail}")
        elif generation == self.request_generation:
            self._set_status(
                f"Reading section {self.chunk_index + 1}; the next section could not be prepared."
            )
        return GLib.SOURCE_REMOVE

    def _on_player_message(self, _bus: Gst.Bus, message: Gst.Message) -> None:
        if message.type == Gst.MessageType.EOS and self.reading:
            self.player.set_state(Gst.State.NULL)
            self._delete_audio()
            self.chunk_index += 1
            self._synthesize_current()
        elif message.type == Gst.MessageType.ERROR:
            error, _debug = message.parse_error()
            self.reading = False
            self.player.set_state(Gst.State.NULL)
            self._delete_audio()
            self._set_status(f"The generated audio could not be played: {error.message}")

    def _pause(self, _button: Gtk.Button) -> None:
        if self.reading and not self.paused:
            self.player.set_state(Gst.State.PAUSED)
            self.paused = True
            self._set_status("Paused.")

    def _resume(self, _button: Gtk.Button) -> None:
        if self.reading and self.paused:
            self.player.set_state(Gst.State.PLAYING)
            self.paused = False
            self._set_status(f"Reading section {self.chunk_index + 1} of {len(self.speech_chunks)}…")

    def _previous(self, _button: Gtk.Button) -> None:
        if not self.reading:
            return
        success, position = self.player.query_position(Gst.Format.TIME)
        if (not success or position <= 2 * Gst.SECOND) and self.chunk_index > 0:
            self.chunk_index -= 1
        self._synthesize_current()

    def _next(self, _button: Gtk.Button) -> None:
        if not self.reading:
            return
        self.chunk_index += 1
        self._synthesize_current()

    def _stop_clicked(self, _button: Gtk.Button) -> None:
        self._stop(clear_queue=False)
        self._set_status("Stopped.")

    def _stop(self, clear_queue: bool) -> None:
        self.reading = False
        self.paused = False
        self.request_generation += 1
        self.waiting_for_chunk = None
        self.pending_chunks.clear()
        self.player.set_state(Gst.State.NULL)
        self._delete_audio()
        for path in self.prefetched_audio.values():
            path.unlink(missing_ok=True)
        self.prefetched_audio.clear()
        if clear_queue:
            self.speech_chunks.clear()
            self.chunk_index = -1

    def _finish(self) -> None:
        self._stop(clear_queue=False)
        self.progress.set_fraction(1.0 if self.speech_chunks else 0.0)
        count = len(self.speech_chunks)
        self.progress_label.set_text(f"{count} / {count}")
        self._set_status("Finished.")

    def _update_progress(self, mark_current: bool = False) -> None:
        count = len(self.speech_chunks)
        if not count or self.chunk_index < 0:
            self.progress.set_fraction(0)
            self.progress_label.set_text("0 / 0")
            return
        number = min(self.chunk_index + 1, count)
        self.progress.set_fraction(number / count if mark_current else self.chunk_index / count)
        self.progress_label.set_text(f"{number} / {count}")

    def _delete_audio(self) -> None:
        if self.audio_path:
            self.audio_path.unlink(missing_ok=True)
            self.audio_path = None

    def _paste(self, _button: Gtk.Button) -> None:
        clipboard = self.get_clipboard()
        clipboard.read_text_async(None, self._paste_ready)

    def _paste_ready(self, clipboard: Any, result: Any) -> None:
        try:
            text = clipboard.read_text_finish(result)
        except GLib.Error:
            text = None
        if not text:
            self._set_status("The clipboard does not contain text.")
            return
        self.text_view.get_buffer().set_text(text)
        self._set_status("Text pasted.")

    def _clear(self, _button: Gtk.Button) -> None:
        self._stop(clear_queue=True)
        self.text_view.get_buffer().set_text("")
        self._update_progress()
        self._set_status("Ready.")

    def _open_file(self, _button: Gtk.Button) -> None:
        dialog = Gtk.FileChooserNative(
            title="Open a text file", transient_for=self,
            action=Gtk.FileChooserAction.OPEN, accept_label="Open", cancel_label="Cancel"
        )
        file_filter = Gtk.FileFilter()
        file_filter.set_name("Text files")
        for pattern in ("*.txt", "*.md", "*.log"):
            file_filter.add_pattern(pattern)
        dialog.add_filter(file_filter)
        dialog.connect("response", self._open_response)
        dialog.show()

    def _open_response(self, dialog: Gtk.FileChooserNative, response: int) -> None:
        if response != Gtk.ResponseType.ACCEPT:
            return
        selected = dialog.get_file()
        path = Path(selected.get_path()) if selected and selected.get_path() else None
        if not path:
            return
        try:
            self._stop(clear_queue=True)
            self.text_view.get_buffer().set_text(path.read_text(encoding="utf-8"))
            self.set_title(f"Text Reader — {path.name}")
            self._set_status(f"Opened {path}")
        except (OSError, UnicodeError) as error:
            self._set_status(f"Unable to open file: {error}")

    def _save_file(self, _button: Gtk.Button) -> None:
        dialog = Gtk.FileChooserNative(
            title="Save text", transient_for=self,
            action=Gtk.FileChooserAction.SAVE, accept_label="Save", cancel_label="Cancel"
        )
        dialog.set_current_name("text.txt")
        dialog.connect("response", self._save_response)
        dialog.show()

    def _save_response(self, dialog: Gtk.FileChooserNative, response: int) -> None:
        if response != Gtk.ResponseType.ACCEPT:
            return
        selected = dialog.get_file()
        path = Path(selected.get_path()) if selected and selected.get_path() else None
        if not path:
            return
        try:
            path.write_text(self._buffer_text(), encoding="utf-8")
            self.set_title(f"Text Reader — {path.name}")
            self._set_status(f"Saved {path}")
        except OSError as error:
            self._set_status(f"Unable to save file: {error}")

    def _voice_changed(self, combo: Gtk.ComboBoxText) -> None:
        voice = combo.get_active_text()
        if voice:
            self.settings["voice_name"] = voice
            voices = self.settings.setdefault("voices", {})
            if isinstance(voices, dict):
                voices[self._engine()] = voice
            self._save_settings()

    def _engine(self) -> str:
        engine = str(self.settings.get("engine", "piper"))
        return engine if engine in ("piper", "kokoro") else "piper"

    def _saved_voice(self) -> str:
        voices = self.settings.get("voices", {})
        if isinstance(voices, dict) and voices.get(self._engine()):
            return str(voices[self._engine()])
        return str(self.settings.get("voice_name", ""))

    def _engine_changed(self, combo: Gtk.ComboBoxText) -> None:
        engine = combo.get_active_id()
        if not engine or engine == self.settings.get("engine"):
            return
        self._stop(clear_queue=False)
        self.settings["engine"] = engine
        self._save_settings()
        self.voice_combo.remove_all()
        self._set_connection_state("disconnected")
        self._set_status(f"Click Connect to use {engine.title()}.")
        self._initial_connect()

    def _symbol_mode_changed(self, combo: Gtk.ComboBoxText) -> None:
        mode = combo.get_active_id()
        if mode and not self.loading_settings:
            self.settings["special_characters"] = mode
            self._save_settings()

    def _setting_changed(self, _scale: Gtk.Scale) -> None:
        if not self.loading_settings:
            self.settings["rate"] = round(self.rate.get_value())
            self._save_settings()

    def _volume_changed(self, _scale: Gtk.Scale) -> None:
        self.player.set_property("volume", self.volume.get_value() / 100.0)
        if not self.loading_settings:
            self.settings["volume"] = round(self.volume.get_value())
            self._save_settings()

    def _load_settings(self) -> None:
        try:
            loaded = json.loads(self.settings_path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                self.settings.update(loaded)
        except (OSError, ValueError):
            pass
        self.settings["rate"] = max(-10, min(10, float(self.settings["rate"])))
        self.settings["volume"] = max(0, min(100, float(self.settings["volume"])))
        if self.settings.get("engine") not in ("piper", "kokoro"):
            self.settings["engine"] = "piper"
        if self.settings.get("special_characters") not in ("keep", "remove", "translate"):
            self.settings["special_characters"] = "keep"

    def _save_settings(self) -> None:
        if self.loading_settings:
            return
        try:
            self.settings_path.parent.mkdir(parents=True, exist_ok=True)
            self.settings_path.write_text(
                json.dumps(self.settings, indent=2) + "\n", encoding="utf-8"
            )
        except OSError:
            pass

    def _on_close_request(self, _window: Gtk.Window) -> bool:
        self._save_settings()
        self._stop(clear_queue=True)
        self.session.close()
        if self.piper_process and self.piper_process.poll() is None:
            try:
                os.killpg(self.piper_process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        return False


class TextReaderApplication(Gtk.Application):
    def __init__(self) -> None:
        super().__init__(application_id=APP_ID)

    def do_activate(self) -> None:
        window = self.props.active_window
        if window is None:
            window = TextReaderWindow(self)
        window.present()


if __name__ == "__main__":
    raise SystemExit(TextReaderApplication().run(sys.argv))
