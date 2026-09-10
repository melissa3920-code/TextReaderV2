"""Run with unittest; Misaki integration runs when the server dependencies exist."""
import unittest
import re
from unittest.mock import Mock, patch

from pronunciation import prepare_kokoro_text

CASES = [
    ('I read every evening.', 'read', 'ɹˈid'),
    ('I will read it tomorrow.', 'read', 'ɹˈid'),
    ('Yesterday I read the whole book.', 'read', 'ɹˈɛd'),
    ('I have read that chapter already.', 'read', 'ɹˈɛd'),
    ('Please record the meeting.', 'record', 'ɹəkˈɔɹd'),
    ('The record is on the table.', 'record', 'ɹˈɛkəɹd'),
    ('They will present the award.', 'present', 'pɹizˈɛnt'),
    ('She opened the present.', 'present', 'pɹˈɛzᵊnt'),
    ('I object to that proposal.', 'object', 'əbʤˈɛkt'),
    ('The object was on the desk.', 'object', 'ˈɑbʤəkt'),
    ('Please conduct the orchestra.', 'conduct', 'kəndˈʌkt'),
    ('Her conduct was excellent.', 'conduct', 'kˈɑndˌʌkt'),
    ('The bass swam under the boat.', 'bass', 'bˈæs'),
    ('Turn up the bass on the speakers.', 'bass', 'bˈAs'),
    ('Tie a bow around the package.', 'bow', 'bˈO'),
    ('The bow of the ship cut through the water.', 'bow', 'bˈW'),
    ('The wind blew through the trees.', 'wind', 'wˈɪnd'),
    ('Wind the clock before bed.', 'wind', 'wˈInd'),
    ('A tear rolled down her cheek.', 'tear', 'tˈɪɹ'),
    ('There was a tear in the paper.', 'tear', 'tˈɛɹ'),
    ('She will lead the group.', 'lead', 'lˈid'),
    ('The pipe was made of lead.', 'lead', 'lˈɛd'),
]


class PronunciationTests(unittest.TestCase):
    def test_contextual_cases(self):
        for text, word, phones in CASES:
            with self.subTest(text=text):
                result = prepare_kokoro_text(text, 'af_heart')
                if word in {'record', 'present', 'object', 'conduct'}:
                    self.assertEqual(result, text)
                else:
                    self.assertIn(f'](/{phones}/)', result)
                    self.assertEqual(re.sub(r'\[([^]]+)\]\(/[^/]+/\)', r'\1', result), text)

    def test_fallback_and_existing_markup(self):
        for text in ['I saw the bass.', 'The bow is here.', 'I read it.',
                     'The bass swam beside the speakers.',
                     'The bass guitar was beside the bass.',
                     '[bass](/bˈæs/) guitar', '[bass](https://example.com/music)',
                     'The wind blew. I read it.', 'Yesterday I will read it.']:
            with self.subTest(text=text):
                if text == 'The wind blew. I read it.':
                    self.assertTrue(prepare_kokoro_text(text, 'af_heart').endswith('I read it.'))
                elif text == 'Yesterday I will read it.':
                    self.assertIn('[read](/ɹˈid/)', prepare_kokoro_text(text, 'af_heart'))
                else:
                    self.assertEqual(prepare_kokoro_text(text, 'af_heart'), text)

    def test_auxiliaries(self):
        for text, phones in [('I will have read it.', 'ɹˈɛd'),
                             ('Yesterday I did not read it.', 'ɹˈid'),
                             ('I haven’t read it.', 'ɹˈɛd')]:
            self.assertIn(f'[read](/{phones}/)', prepare_kokoro_text(text, 'af_heart'))

    def test_other_languages_and_british(self):
        for voice in ['ff_siwis', 'jf_alpha', '']:
            self.assertEqual(prepare_kokoro_text(CASES[0][0], voice), CASES[0][0])
        self.assertIn('[tear](/tˈɪə/)', prepare_kokoro_text('A tear rolled down her cheek.', 'bf_emma'))

    def test_real_misaki(self):
        try:
            from misaki import en
        except ImportError:
            self.skipTest('Run with the Kokoro server Python environment for Misaki integration')
        g2p = en.G2P()
        for text, word, phones in CASES:
            with self.subTest(text=text):
                _, tokens = g2p(prepare_kokoro_text(text, 'af_heart'))
                self.assertEqual([t.phonemes for t in tokens if t.text.lower() == word], [phones])


class ReaderIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        try:
            import text_reader
        except ImportError:
            raise unittest.SkipTest('GTK reader dependencies unavailable')
        cls.reader = text_reader

    def test_request_routing_and_preload(self):
        for engine in ('kokoro', 'piper'):
            with self.subTest(engine=engine):
                window = Mock()
                window.reading = True
                window.pending_chunks = set()
                window.prefetched_audio = {}
                window.speech_chunks = ['I read every evening.', 'Yesterday I read it.']
                window.request_generation = 9
                window._engine.return_value = engine
                window.voice_combo.get_active_text.return_value = 'af_heart' if engine == 'kokoro' else 'Piper default voice'
                window.rate.get_value.return_value = 2
                with patch.object(self.reader.threading, 'Thread') as thread:
                    self.reader.TextReaderWindow._request_chunk(window, 1)
                    _, payload, generation, index = thread.call_args.kwargs['args']
                    self.assertEqual((generation, index), (9, 1))
                    self.assertEqual(window.speech_chunks[1], 'Yesterday I read it.')
                    if engine == 'kokoro':
                        self.assertIn('[read](/ɹˈɛd/)', payload['input'])
                        self.assertEqual(payload['speed'], 1.1)
                    else:
                        self.assertEqual(payload['text'], window.speech_chunks[1])
                        self.assertAlmostEqual(payload['length_scale'], 1 / 1.1)
                    self.reader.TextReaderWindow._request_chunk(window, 1)
                    self.assertEqual(thread.call_count, 1)

    def test_cleanup_and_chunking(self):
        clean = self.reader.process_special_characters('Books & music.\n\nWind the clock.', 'translate')
        self.assertEqual(self.reader.create_speech_chunks(clean), ['Books and music.', 'Wind the clock.'])

    def test_playback_controls(self):
        window = Mock(reading=True, paused=False, chunk_index=1)
        window.speech_chunks = ['one', 'two']
        methods = self.reader.TextReaderWindow
        methods._pause(window, None)
        self.assertTrue(window.paused)
        window.player.set_state.assert_called_with(self.reader.Gst.State.PAUSED)
        methods._resume(window, None)
        self.assertFalse(window.paused)
        window.player.set_state.assert_called_with(self.reader.Gst.State.PLAYING)
        window.player.query_position.return_value = (True, 0)
        methods._previous(window, None)
        self.assertEqual(window.chunk_index, 0)
        methods._next(window, None)
        self.assertEqual(window.chunk_index, 1)
        self.assertEqual(window._synthesize_current.call_count, 2)
        window.volume.get_value.return_value = 65
        window.loading_settings = True
        methods._volume_changed(window, None)
        window.player.set_property.assert_called_with('volume', 0.65)

    def test_playback_starts_preload_and_shutdown_cleans_queue(self):
        from pathlib import Path
        window = Mock(reading=True, request_generation=2, chunk_index=0)
        window.prefetched_audio = {1: Mock()}
        window.pending_chunks = {1}
        window.speech_chunks = ['one', 'two']
        window.volume.get_value.return_value = 50
        window.piper_process = None
        methods = self.reader.TextReaderWindow
        methods._play_audio(window, Path('/tmp/reader-test.wav'), 2, 0)
        window._request_chunk.assert_called_once_with(1)
        window._stop.side_effect = lambda **kwargs: methods._stop(window, **kwargs)
        cached = window.prefetched_audio[1]
        self.assertFalse(methods._on_close_request(window, None))
        cached.unlink.assert_called_once_with(missing_ok=True)
        self.assertEqual(window.speech_chunks, [])
        self.assertEqual(window.pending_chunks, set())
        self.assertFalse(window.reading)
        window.session.close.assert_called_once()


if __name__ == '__main__':
    unittest.main()
