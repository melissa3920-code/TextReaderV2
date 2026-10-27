using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TextReader
{
    public partial class MainWindow : Window
    {
        private enum ConnectionState
        {
            Disconnected,
            Connecting,
            Connected
        }

        private void UpdateConnectionButton(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Connected:
                    ConnectPiperButton.Content = $"{EngineName} Connected";
                    ConnectPiperButton.Background = Brushes.SeaGreen;
                    ConnectPiperButton.Foreground = Brushes.White;
                    ConnectPiperButton.BorderBrush = Brushes.DarkGreen;
                    break;

                case ConnectionState.Connecting:
                    ConnectPiperButton.Content = $"Connecting to {EngineName}...";
                    ConnectPiperButton.Background = Brushes.Goldenrod;
                    ConnectPiperButton.Foreground = Brushes.Black;
                    ConnectPiperButton.BorderBrush = Brushes.DarkGoldenrod;
                    break;

                default:
                    ConnectPiperButton.Content = IsKokoro ? "Connect to Kokoro" : "Connect / Start Piper";
                    ConnectPiperButton.Background = Brushes.IndianRed;
                    ConnectPiperButton.Foreground = Brushes.White;
                    ConnectPiperButton.BorderBrush = Brushes.DarkRed;
                    break;
            }
        }

        private static readonly Uri PiperInfoAddress =
            new("http://127.0.0.1:5000/info");

        private static readonly Uri PiperVoicesAddress =
            new("http://127.0.0.1:5000/voices");

        private bool IsKokoro => settings.Engine == "kokoro";
        private string EngineName => IsKokoro ? "Kokoro" : "Piper";
        private bool windowReady;

        private readonly HttpClient httpClient;
        private readonly MediaPlayer mediaPlayer;
        private readonly List<string> speechChunks = new();
        private readonly SpeechPrefetch prefetch = new();

        private CancellationTokenSource? speechRequest;
        private Process? piperProcess;
        private string? currentAudioFile;
        private int currentChunkIndex = -1;
        private bool isPaused;
        private bool isReadingSession;
        private bool playWhenOpened;
        private bool isLoadingSettings;

        private AppSettings settings = new();

        private string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TextReader");

        private string SettingsFile =>
            Path.Combine(SettingsDirectory, "settings.json");

        public MainWindow()
        {
            InitializeComponent();

            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            mediaPlayer = new MediaPlayer();
            mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            windowReady = true;

            bool connected = await TryConnectAsync(showFailure: false);

            if (!connected)
            {
                UpdateConnectionButton(ConnectionState.Disconnected);
                StatusText.Text =
                    IsKokoro ? "Start Kokoro-FastAPI, then click Connect to Kokoro." :
                    "Start Piper in the CLI, or click Connect / Start Piper.";
            }
        }

        private async void ConnectPiperButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ConnectPiperButton.IsEnabled = false;
            EngineComboBox.IsEnabled = false;

            try
            {
                if (await TryConnectAsync(showFailure: false))
                {
                    StatusText.Text = $"{EngineName} is connected.";
                    return;
                }

                if (IsKokoro)
                    StatusText.Text = "Start Kokoro-FastAPI on port 8880, then reconnect.";
                else
                {
                    EngineComboBox.IsEnabled = false;
                    ConnectPiperButton.IsEnabled = false;
                    await StartPiperAsync();
                }
            }
            finally
            {
                ConnectPiperButton.IsEnabled = true;
                EngineComboBox.IsEnabled = true;
            }
        }

        private async void EngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!windowReady || isLoadingSettings)
                return;

            StopEverything(showStoppedMessage: false, clearQueue: true);
            settings.Engine = (EngineComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "piper";
            VoiceComboBox.ItemsSource = null;
            SaveSettings();
            bool connected = await TryConnectAsync(showFailure: false);
            StatusText.Text = connected ? $"{EngineName} is connected." :
                IsKokoro ? "Start Kokoro-FastAPI, then click Connect to Kokoro." : "Click Connect / Start Piper.";
        }

        private async Task<bool> TryConnectAsync(bool showFailure)
        {
            EngineComboBox.IsEnabled = false;
            ConnectPiperButton.IsEnabled = false;
            UpdateConnectionButton(ConnectionState.Connecting);
            try
            {
                return IsKokoro ? await TryConnectToKokoroAsync(showFailure) : await TryConnectToPiperAsync(showFailure);
            }
            finally
            {
                EngineComboBox.IsEnabled = true;
                ConnectPiperButton.IsEnabled = true;
            }
        }

        private async Task<bool> TryConnectToKokoroAsync(bool showFailure)
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                List<string> voices = await SpeechApi.LoadKokoroVoicesAsync(httpClient, timeout.Token);
                SelectVoices(voices, voices.Contains("af_heart") ? "af_heart" : voices[0]);
                UpdateConnectionButton(ConnectionState.Connected);
                return true;
            }
            catch (Exception exception) when (exception is HttpRequestException ||
                exception is TaskCanceledException || exception is JsonException || exception is InvalidDataException)
            {
                UpdateConnectionButton(ConnectionState.Disconnected);
                if (showFailure)
                    MessageBox.Show("Start Kokoro-FastAPI at http://127.0.0.1:8880, then reconnect.",
                        "Kokoro connection error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private async Task StartPiperAsync()
        {
            string? modelPath = settings.PiperModelPath;

            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                OpenFileDialog dialog = new()
                {
                    Title = "Select a Piper voice model",
                    Filter = "Piper voice model (*.onnx)|*.onnx",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog(this) != true)
                {
                    StatusText.Text = "Piper was not started.";
                    return;
                }

                modelPath = dialog.FileName;
                settings.PiperModelPath = modelPath;
                SaveSettings();
            }

            UpdateConnectionButton(ConnectionState.Connecting);
            StatusText.Text = "Starting the local Piper server...";

            piperProcess = TryStartPiperProcess("py", modelPath)
                ?? TryStartPiperProcess("python", modelPath);

            if (piperProcess is null)
            {
                UpdateConnectionButton(ConnectionState.Disconnected);

                MessageBox.Show(
                    "The app could not start Python.\n\n" +
                    "Piper can still be started in a Command Prompt with:\n\n" +
                    $"py -m piper.http_server -m \"{modelPath}\"",
                    "Unable to start Piper",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (piperProcess.HasExited)
                    break;

                await Task.Delay(500);

                if (await TryConnectToPiperAsync(showFailure: false))
                {
                    StatusText.Text = "Piper started and connected.";
                    return;
                }
            }

            UpdateConnectionButton(ConnectionState.Disconnected);

            MessageBox.Show(
                "The Piper process started, but its HTTP server did not " +
                "become available on port 5000.\n\n" +
                "Another program may be using that port, or Piper may have " +
                "reported an error while loading the voice model.",
                "Piper did not start",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private static Process? TryStartPiperProcess(
            string pythonCommand,
            string modelPath)
        {
            try
            {
                string? workingDirectory = Path.GetDirectoryName(modelPath);

                ProcessStartInfo startInfo = new()
                {
                    FileName = pythonCommand,
                    Arguments =
                        $"-m piper.http_server -m \"{modelPath}\" " +
                        "--host 127.0.0.1 --port 5000",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                        ? AppContext.BaseDirectory
                        : workingDirectory
                };

                return Process.Start(startInfo);
            }
            catch (Win32Exception)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> TryConnectToPiperAsync(bool showFailure)
        {
            try
            {
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(3));

                using HttpResponseMessage response =
                    await httpClient.GetAsync(PiperInfoAddress, timeout.Token);

                response.EnsureSuccessStatusCode();

                string infoJson = await response.Content.ReadAsStringAsync(
                    timeout.Token);

                string defaultVoice = GetDefaultVoiceName(infoJson);

                await LoadVoicesAsync(defaultVoice, timeout.Token);

                UpdateConnectionButton(ConnectionState.Connected);
                return true;
            }
            catch (Exception exception)
                when (exception is HttpRequestException ||
                      exception is TaskCanceledException)
            {
                UpdateConnectionButton(ConnectionState.Disconnected);

                if (showFailure)
                {
                    MessageBox.Show(
                        "The app could not connect to Piper at " +
                        "http://127.0.0.1:5000.",
                        "Piper connection error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return false;
            }
        }

        private static string GetDefaultVoiceName(string infoJson)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(infoJson);

                return document.RootElement
                    .GetProperty("voice")
                    .GetProperty("name")
                    .GetString() ?? "Piper default voice";
            }
            catch
            {
                return "Piper default voice";
            }
        }

        private async Task LoadVoicesAsync(
            string defaultVoice,
            CancellationToken cancellationToken)
        {
            List<string> voiceNames = new();

            try
            {
                using HttpResponseMessage response =
                    await httpClient.GetAsync(
                        PiperVoicesAddress,
                        cancellationToken);

                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(
                    cancellationToken);

                using JsonDocument document = JsonDocument.Parse(json);

                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    voiceNames.AddRange(
                        document.RootElement
                            .EnumerateObject()
                            .Select(property => property.Name));
                }
            }
            catch
            {
                // The default voice from /info is enough to keep the app usable.
            }

            if (!voiceNames.Contains(defaultVoice))
                voiceNames.Insert(0, defaultVoice);

            voiceNames = voiceNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name)
                .ToList();

            SelectVoices(voiceNames, defaultVoice);
        }

        private string SavedVoice => settings.Voices.GetValueOrDefault(settings.Engine,
            IsKokoro ? "af_heart" : settings.VoiceName);

        private void SelectVoices(List<string> voiceNames, string defaultVoice)
        {
            string previouslySelected = SavedVoice;
            VoiceComboBox.ItemsSource = voiceNames;
            VoiceComboBox.SelectedItem = voiceNames.FirstOrDefault(name =>
                string.Equals(name, previouslySelected, StringComparison.OrdinalIgnoreCase)) ?? defaultVoice;
        }

        private async void ReadButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await StartReadingAsync(TextInput.Text);
        }

        private async void ReadSelectionButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            string selectedText = TextInput.SelectedText;

            if (string.IsNullOrWhiteSpace(selectedText))
            {
                StatusText.Text = "Select some text first.";
                TextInput.Focus();
                return;
            }

            await StartReadingAsync(selectedText);
        }

        private async Task StartReadingAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText.Text = "Paste or type something to read.";
                TextInput.Focus();
                return;
            }

            if (!await TryConnectAsync(showFailure: false))
            {
                StatusText.Text =
                    IsKokoro ? "Kokoro is not connected. Start Kokoro-FastAPI, then reconnect." :
                    "Piper is not connected. Click Connect / Start Piper.";
                return;
            }

            StopEverything(showStoppedMessage: false, clearQueue: true);

            speechChunks.AddRange(CreateSpeechChunks(text));

            if (speechChunks.Count == 0)
            {
                StatusText.Text = "No readable text was found.";
                return;
            }

            currentChunkIndex = 0;
            isReadingSession = true;

            ReadingProgress.Minimum = 0;
            ReadingProgress.Maximum = speechChunks.Count;
            ReadingProgress.Value = 0;
            UpdateProgressDisplay();

            await GenerateAndPlayCurrentChunkAsync();
        }

        private async Task GenerateAndPlayCurrentChunkAsync()
        {
            if (!isReadingSession ||
                currentChunkIndex < 0 ||
                currentChunkIndex >= speechChunks.Count)
            {
                FinishReadingSession();
                return;
            }

            CancelSpeechRequest();
            CloseAndDeleteCurrentAudio();

            playWhenOpened = false;
            speechRequest = new CancellationTokenSource();
            CancellationTokenSource thisRequest = speechRequest;

            try
            {
                UpdateProgressDisplay();
                StatusText.Text =
                    $"Generating section {currentChunkIndex + 1} " +
                    $"of {speechChunks.Count}...";

                byte[] wavAudio = await prefetch.TakeAsync(
                    CreateSpeechKey(currentChunkIndex), GenerateAudioAsync, thisRequest.Token);
                thisRequest.Token.ThrowIfCancellationRequested();

                string tempFile = Path.Combine(
                    Path.GetTempPath(),
                    $"TextReader_{Guid.NewGuid():N}.wav");

                await File.WriteAllBytesAsync(
                    tempFile,
                    wavAudio,
                    thisRequest.Token);

                thisRequest.Token.ThrowIfCancellationRequested();

                currentAudioFile = tempFile;
                mediaPlayer.Volume = VolumeSlider.Value / 100.0;
                playWhenOpened = true;
                mediaPlayer.Open(new Uri(currentAudioFile));

                StatusText.Text = "Loading audio...";
            }
            catch (OperationCanceledException)
            {
                // Stop, Previous, Next, or a new reading request cancelled it.
            }
            catch (HttpRequestException exception)
            {
                isReadingSession = false;
                prefetch.Clear();
                UpdateConnectionButton(ConnectionState.Disconnected);
                StatusText.Text = "The speech request failed.";

                MessageBox.Show(
                    exception.Message,
                    $"{EngineName} request error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception exception)
            {
                isReadingSession = false;
                prefetch.Clear();
                StatusText.Text = "Unable to generate or play the speech.";

                MessageBox.Show(
                    exception.Message,
                    "Text Reader error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(speechRequest, thisRequest))
                    speechRequest = null;

                thisRequest.Dispose();
            }
        }

        private SpeechKey CreateSpeechKey(int index) => new(index, IsKokoro, speechChunks[index],
            VoiceComboBox.SelectedItem as string ?? SavedVoice, RateSlider.Value, settings.LeadInMilliseconds);

        private async Task<byte[]> GenerateAudioAsync(SpeechKey key, CancellationToken token)
        {
            using HttpRequestMessage request = SpeechApi.CreateSpeechRequest(
                key.Kokoro, key.Text, key.Voice, key.Rate);
            using HttpResponseMessage response = await httpClient.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            byte[] audio = await response.Content.ReadAsByteArrayAsync(token);
            token.ThrowIfCancellationRequested();
            return WaveAudio.AddPlaybackLeadIn(audio, key.LeadInMilliseconds);
        }

        private void PrefetchNextChunk()
        {
            int nextIndex = currentChunkIndex + 1;
            if (isReadingSession && nextIndex < speechChunks.Count)
                prefetch.Prime(CreateSpeechKey(nextIndex), GenerateAudioAsync);
        }

        private void LeadInComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!windowReady || isLoadingSettings) return;
            if (int.TryParse(LeadInComboBox.SelectedValue as string, out int milliseconds))
            {
                settings.LeadInMilliseconds = milliseconds;
                RefreshPrefetch();
                SaveSettings();
            }
        }

        private void RefreshPrefetch()
        {
            prefetch.Clear();
            if (currentAudioFile is not null && !playWhenOpened)
                PrefetchNextChunk();
        }

        private void MediaPlayer_MediaOpened(
            object? sender,
            EventArgs e)
        {
            if (!playWhenOpened || !isReadingSession)
                return;

            playWhenOpened = false;
            isPaused = false;
            mediaPlayer.Position = TimeSpan.Zero;
            mediaPlayer.Play();
            PrefetchNextChunk();

            ReadingProgress.Value = currentChunkIndex + 1;
            UpdateProgressDisplay();

            StatusText.Text =
                $"Reading section {currentChunkIndex + 1} " +
                $"of {speechChunks.Count}...";
        }

        private async void MediaPlayer_MediaEnded(
            object? sender,
            EventArgs e)
        {
            if (!isReadingSession)
                return;

            CloseAndDeleteCurrentAudio();
            currentChunkIndex++;

            if (currentChunkIndex >= speechChunks.Count)
            {
                FinishReadingSession();
                return;
            }

            await GenerateAndPlayCurrentChunkAsync();
        }

        private void MediaPlayer_MediaFailed(
            object? sender,
            ExceptionEventArgs e)
        {
            isReadingSession = false;
            playWhenOpened = false;
            isPaused = false;
            prefetch.Clear();

            CloseAndDeleteCurrentAudio();

            StatusText.Text = "The generated audio could not be played.";

            MessageBox.Show(
                e.ErrorException?.Message ??
                "The generated WAV file could not be played.",
                "Audio playback error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private void PauseButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (isReadingSession && !isPaused && mediaPlayer.CanPause)
            {
                mediaPlayer.Pause();
                isPaused = true;
                StatusText.Text = "Paused.";
            }
        }

        private void ResumeButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (isReadingSession && isPaused)
            {
                mediaPlayer.Play();
                isPaused = false;
                StatusText.Text =
                    $"Reading section {currentChunkIndex + 1} " +
                    $"of {speechChunks.Count}...";
            }
        }

        private async void PreviousButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!isReadingSession || speechChunks.Count == 0)
                return;

            if (mediaPlayer.Position.TotalSeconds <= 2.0 &&
                currentChunkIndex > 0)
            {
                currentChunkIndex--;
            }

            mediaPlayer.Stop();
            await GenerateAndPlayCurrentChunkAsync();
        }

        private async void NextButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!isReadingSession || speechChunks.Count == 0)
                return;

            mediaPlayer.Stop();
            currentChunkIndex++;

            if (currentChunkIndex >= speechChunks.Count)
            {
                FinishReadingSession();
                return;
            }

            await GenerateAndPlayCurrentChunkAsync();
        }

        private void StopButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopEverything(showStoppedMessage: true, clearQueue: false);
        }

        private void PasteButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!Clipboard.ContainsText())
            {
                StatusText.Text =
                    "The clipboard does not contain text.";
                return;
            }

            TextInput.Text = Clipboard.GetText();
            TextInput.CaretIndex = TextInput.Text.Length;
            TextInput.Focus();

            StatusText.Text = "Text pasted.";
        }

        private void ClearButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            StopEverything(showStoppedMessage: false, clearQueue: true);

            TextInput.Clear();
            TextInput.Focus();

            ReadingProgress.Maximum = 1;
            ReadingProgress.Value = 0;
            ProgressText.Text = "0 / 0";
            StatusText.Text = "Ready.";
        }

        private async void OpenFileMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            OpenFileDialog dialog = new()
            {
                Title = "Open a text file",
                Filter =
                    "Text files (*.txt;*.md;*.log)|*.txt;*.md;*.log|" +
                    "All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                StopEverything(showStoppedMessage: false, clearQueue: true);
                TextInput.Text = await File.ReadAllTextAsync(dialog.FileName);
                TextInput.CaretIndex = 0;
                TextInput.Focus();

                Title = $"Text Reader - {Path.GetFileName(dialog.FileName)}";
                StatusText.Text = $"Opened {dialog.FileName}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "Unable to open file",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void SaveFileMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            SaveFileDialog dialog = new()
            {
                Title = "Save text",
                Filter = "Text file (*.txt)|*.txt|Markdown file (*.md)|*.md",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                await File.WriteAllTextAsync(
                    dialog.FileName,
                    TextInput.Text);

                Title = $"Text Reader - {Path.GetFileName(dialog.FileName)}";
                StatusText.Text = $"Saved {dialog.FileName}";
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    exception.Message,
                    "Unable to save file",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExitMenuItem_Click(
            object sender,
            RoutedEventArgs e)
        {
            Close();
        }

        private void RateSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (RateValueText is not null)
                RateValueText.Text = ((int)e.NewValue).ToString();

            if (!isLoadingSettings)
            {
                settings.Rate = e.NewValue;
                RefreshPrefetch();
                SaveSettings();
            }
        }

        private void VolumeSlider_ValueChanged(
            object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeValueText is not null)
                VolumeValueText.Text = ((int)e.NewValue).ToString();

            if (mediaPlayer is not null)
                mediaPlayer.Volume = e.NewValue / 100.0;

            if (!isLoadingSettings)
            {
                settings.Volume = e.NewValue;
                SaveSettings();
            }
        }

        private void VoiceComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (VoiceComboBox.SelectedItem is string voiceName)
            {
                settings.Voices[settings.Engine] = voiceName;
                if (!IsKokoro)
                    settings.VoiceName = voiceName;

                if (!isLoadingSettings)
                {
                    RefreshPrefetch();
                    SaveSettings();
                }
            }
        }

        private static List<string> CreateSpeechChunks(
            string text,
            int maximumCharacters = 700)
        {
            List<string> chunks = new();

            string normalized = text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                return chunks;

            string[] paragraphs = Regex.Split(
                normalized,
                @"\n\s*\n|\n");

            foreach (string rawParagraph in paragraphs)
            {
                string paragraph = Regex.Replace(
                    rawParagraph.Trim(),
                    @"\s+",
                    " ");

                if (string.IsNullOrWhiteSpace(paragraph))
                    continue;

                AddParagraphChunks(
                    paragraph,
                    maximumCharacters,
                    chunks);
            }

            return chunks;
        }

        private static void AddParagraphChunks(
            string paragraph,
            int maximumCharacters,
            List<string> chunks)
        {
            if (paragraph.Length <= maximumCharacters)
            {
                chunks.Add(paragraph);
                return;
            }

            string[] sentences = Regex.Split(
                paragraph,
                @"(?<=[.!?])\s+");

            StringBuilder currentChunk = new();

            foreach (string rawSentence in sentences)
            {
                string sentence = rawSentence.Trim();

                if (string.IsNullOrWhiteSpace(sentence))
                    continue;

                if (sentence.Length > maximumCharacters)
                {
                    FlushChunk(currentChunk, chunks);
                    AddWordChunks(sentence, maximumCharacters, chunks);
                    continue;
                }

                int proposedLength = currentChunk.Length == 0
                    ? sentence.Length
                    : currentChunk.Length + 1 + sentence.Length;

                if (proposedLength > maximumCharacters)
                    FlushChunk(currentChunk, chunks);

                if (currentChunk.Length > 0)
                    currentChunk.Append(' ');

                currentChunk.Append(sentence);
            }

            FlushChunk(currentChunk, chunks);
        }

        private static void AddWordChunks(
            string text,
            int maximumCharacters,
            List<string> chunks)
        {
            StringBuilder currentChunk = new();

            foreach (string word in text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries))
            {
                int proposedLength = currentChunk.Length == 0
                    ? word.Length
                    : currentChunk.Length + 1 + word.Length;

                if (proposedLength > maximumCharacters)
                    FlushChunk(currentChunk, chunks);

                if (currentChunk.Length > 0)
                    currentChunk.Append(' ');

                currentChunk.Append(word);
            }

            FlushChunk(currentChunk, chunks);
        }

        private static void FlushChunk(
            StringBuilder currentChunk,
            List<string> chunks)
        {
            if (currentChunk.Length == 0)
                return;

            chunks.Add(currentChunk.ToString());
            currentChunk.Clear();
        }

        private void FinishReadingSession()
        {
            isReadingSession = false;
            playWhenOpened = false;
            isPaused = false;

            CancelSpeechRequest();
            prefetch.Clear();
            CloseAndDeleteCurrentAudio();

            if (speechChunks.Count > 0)
            {
                ReadingProgress.Value = speechChunks.Count;
                ProgressText.Text =
                    $"{speechChunks.Count} / {speechChunks.Count}";
            }

            StatusText.Text = "Finished.";
        }

        private void StopEverything(
            bool showStoppedMessage,
            bool clearQueue)
        {
            isReadingSession = false;
            playWhenOpened = false;
            isPaused = false;

            CancelSpeechRequest();
            prefetch.Clear();
            CloseAndDeleteCurrentAudio();

            if (clearQueue)
            {
                speechChunks.Clear();
                currentChunkIndex = -1;
            }

            if (showStoppedMessage)
                StatusText.Text = "Stopped.";
        }

        private void CancelSpeechRequest()
        {
            if (speechRequest is null)
                return;

            try
            {
                speechRequest.Cancel();
            }
            catch
            {
                // A request that has already completed needs no action.
            }
            finally
            {
                speechRequest = null;
            }
        }

        private void CloseAndDeleteCurrentAudio()
        {
            mediaPlayer.Stop();
            mediaPlayer.Close();

            if (string.IsNullOrWhiteSpace(currentAudioFile))
                return;

            try
            {
                if (File.Exists(currentAudioFile))
                    File.Delete(currentAudioFile);
            }
            catch
            {
                // A temporary WAV left behind is harmless.
            }
            finally
            {
                currentAudioFile = null;
            }
        }

        private void UpdateProgressDisplay()
        {
            if (speechChunks.Count == 0 || currentChunkIndex < 0)
            {
                ProgressText.Text = "0 / 0";
                return;
            }

            ProgressText.Text =
                $"{Math.Min(currentChunkIndex + 1, speechChunks.Count)} " +
                $"/ {speechChunks.Count}";
        }

        private void LoadSettings()
        {
            isLoadingSettings = true;

            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    settings = JsonSerializer.Deserialize<AppSettings>(json)
                        ?? new AppSettings();
                }

                settings.Voices ??= new();
                if (settings.Engine != "kokoro") settings.Engine = "piper";
                EngineComboBox.SelectedIndex = IsKokoro ? 1 : 0;
                string leadInValue = settings.LeadInMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!LeadInComboBox.Items.Cast<ComboBoxItem>().Any(item => (string)item.Tag == leadInValue))
                {
                    settings.LeadInMilliseconds = WaveAudio.DefaultLeadInMilliseconds;
                    leadInValue = settings.LeadInMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                LeadInComboBox.SelectedValue = leadInValue;
                RateSlider.Value = Math.Clamp(settings.Rate, -10, 10);
                VolumeSlider.Value = Math.Clamp(settings.Volume, 0, 100);
            }
            catch
            {
                settings = new AppSettings();
                EngineComboBox.SelectedIndex = 0;
                LeadInComboBox.SelectedValue = "100";
                RateSlider.Value = 0;
                VolumeSlider.Value = 100;
            }
            finally
            {
                isLoadingSettings = false;
            }
        }

        private void SaveSettings()
        {
            if (isLoadingSettings)
                return;

            try
            {
                Directory.CreateDirectory(SettingsDirectory);

                string json = JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                File.WriteAllText(SettingsFile, json);
            }
            catch
            {
                // Settings are convenient, but never worth crashing the app.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveSettings();
            StopEverything(showStoppedMessage: false, clearQueue: true);

            mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
            mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;

            httpClient.Dispose();

            if (piperProcess is not null && !piperProcess.HasExited)
            {
                try
                {
                    piperProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may already have closed.
                }
            }

            piperProcess?.Dispose();

            base.OnClosed(e);
        }

        private sealed class AppSettings
        {
            public string Engine { get; set; } = "piper";
            public int LeadInMilliseconds { get; set; } = WaveAudio.DefaultLeadInMilliseconds;
            public Dictionary<string, string> Voices { get; set; } = new();
            public string PiperModelPath { get; set; } = string.Empty;
            public string VoiceName { get; set; } = string.Empty;
            public double Rate { get; set; }
            public double Volume { get; set; } = 100;
        }
    }
}
