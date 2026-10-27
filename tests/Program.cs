using System.Net;
using System.Text;
using System.Text.Json;
using TextReader;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

using (var kokoro = SpeechApi.CreateSpeechRequest(true, "Read this.", "af_heart", 5))
{
    using var json = JsonDocument.Parse(await kokoro.Content!.ReadAsStringAsync());
    var p = json.RootElement;
    Check(kokoro.RequestUri!.AbsolutePath == "/v1/audio/speech" && p.GetProperty("input").GetString() == Pronunciation.PrepareKokoroText("Read this.", "af_heart"), "Kokoro endpoint and text");
    Check(p.GetProperty("speed").GetDouble() == 1.25 && p.GetProperty("response_format").GetString() == "wav" && !p.GetProperty("stream").GetBoolean(), "Kokoro speed and WAV response");
    Check(!p.TryGetProperty("length_scale", out _), "Kokoro does not receive Piper fields");
}
using (var piper = SpeechApi.CreateSpeechRequest(false, "Piper text", "Piper default voice", -4))
{
    using var json = JsonDocument.Parse(await piper.Content!.ReadAsStringAsync());
    var p = json.RootElement;
    Check(piper.RequestUri!.Port == 5000 && p.GetProperty("length_scale").GetDouble() == 1.2 && p.GetProperty("text").GetString() == "Piper text", "Piper request remains compatible");
    Check(!p.TryGetProperty("voice", out _) && !p.TryGetProperty("model", out _), "Piper default voice remains optional");
}
foreach (string body in new[] { "{\"voices\":[\"af_heart\",\"af_heart\",\"bf_emma\"]}", "{\"voices\":[{\"id\":\"bf_emma\"},{\"id\":\"af_heart\"}]}", "{\"data\":[{\"name\":\"af_heart\"},{\"name\":\"bf_emma\"}]}" })
{
    using var client = new HttpClient(new VoiceHandler(body));
    var voices = await SpeechApi.LoadKokoroVoicesAsync(client, default);
    Check(voices.SequenceEqual(new[] { "af_heart", "bf_emma" }), "Kokoro voice formats are parsed and deduplicated");
}
foreach (string body in new[] { "{}", "{\"voices\":[]}" })
{
    using var client = new HttpClient(new VoiceHandler(body));
    try { await SpeechApi.LoadKokoroVoicesAsync(client, default); throw new Exception("Expected invalid voice response"); }
    catch (InvalidDataException) { Console.WriteLine("PASS: Empty or malformed voice list rejected"); }
}
PronunciationTests.Run(Check);
WaveAudioTests.Run(Check);
await SpeechPrefetchTests.RunAsync(Check);

if (args.Contains("--live"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var voices = await SpeechApi.LoadKokoroVoicesAsync(client, default);
    Check(voices.Contains("af_heart"), "Live server returns af_heart");
    using var request = SpeechApi.CreateSpeechRequest(true, "The Windows text reader can now use Kokoro.", "af_heart", 0);
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    byte[] audio = await response.Content.ReadAsByteArrayAsync();
    byte[] padded = WaveAudio.AddPlaybackLeadIn(audio);
    int dataStart = WaveAudioTests.DataStart(audio);
    int lead = padded.Length - audio.Length;
    Check(padded.AsSpan(dataStart + lead).SequenceEqual(audio.AsSpan(dataStart)), "Live speech samples preserved after lead-in");
    Check(audio.Length > 1000 && Encoding.ASCII.GetString(audio, 0, 4) == "RIFF" && Encoding.ASCII.GetString(audio, 8, 4) == "WAVE", "Live server returns playable WAV audio");
}

sealed class VoiceHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
