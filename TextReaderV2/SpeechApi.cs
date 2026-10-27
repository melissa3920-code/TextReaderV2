using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TextReader;

internal static class SpeechApi
{
    internal static readonly Uri KokoroVoicesAddress = new("http://127.0.0.1:8880/v1/audio/voices");

    internal static async Task<List<string>> LoadKokoroVoicesAsync(HttpClient client, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(KokoroVoicesAddress, token);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        JsonElement root = document.RootElement;
        JsonElement voices = root.ValueKind == JsonValueKind.Array ? root :
            root.TryGetProperty("voices", out JsonElement list) ? list :
            root.TryGetProperty("data", out list) ? list : default;
        if (voices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Kokoro returned an invalid voice list.");
        List<string> names = new();
        foreach (JsonElement voice in voices.EnumerateArray())
        {
            string? name = voice.ValueKind == JsonValueKind.String ? voice.GetString() :
                voice.ValueKind == JsonValueKind.Object && voice.TryGetProperty("id", out JsonElement id) ? id.GetString() :
                voice.ValueKind == JsonValueKind.Object && voice.TryGetProperty("name", out JsonElement label) ? label.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        if (names.Count == 0) throw new InvalidDataException("Kokoro returned no voices.");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
    }

    internal static HttpRequestMessage CreateSpeechRequest(bool kokoro, string text, string voice, double rate)
    {
        Dictionary<string, object> payload;
        if (kokoro)
        {
            payload = new()
            {
                ["model"] = "kokoro", ["input"] = text,
                ["voice"] = string.IsNullOrWhiteSpace(voice) ? "af_heart" : voice,
                ["response_format"] = "wav", ["stream"] = false,
                ["speed"] = Math.Clamp(1.0 + rate * 0.05, 0.5, 2.0)
            };
        }
        else
        {
            payload = new() { ["text"] = text, ["length_scale"] = 1.0 - rate * 0.05 };
            if (!string.IsNullOrWhiteSpace(voice) && voice != "Piper default voice") payload["voice"] = voice;
        }
        return new HttpRequestMessage(HttpMethod.Post, kokoro ?
            "http://127.0.0.1:8880/v1/audio/speech" : "http://127.0.0.1:5000/synthesize")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }
}
