using TextReader;

internal static class SpeechPrefetchTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        SpeechKey key = new(1, true, "Next section", "af_heart", 0);
        SpeechPrefetch cache = new();
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<byte[]> Generate(SpeechKey _, CancellationToken token) { calls++; return gate.Task.WaitAsync(token); }
        cache.Prime(key, Generate);
        check(calls == 1, "Next section starts generating before it is requested for playback");
        Task<byte[]> waiting = cache.TakeAsync(key, Generate, default);
        check(!waiting.IsCompleted && calls == 1, "Transition reuses an in-flight prefetch instead of duplicating it");
        byte[] audio = [1, 2, 3];
        gate.SetResult(audio);
        check(ReferenceEquals(await waiting, audio) && calls == 1, "Prepared audio is reused without regeneration");

        foreach (SpeechKey changed in new[] {key with { Voice = "bf_emma" }, key with { Rate = 5 },
            key with { Kokoro = false }, key with { Index = 2 }, key with { Text = "New session text" }})
        {
            CancellationToken oldToken = default;
            cache.Prime(key, (_, token) => { oldToken = token; return Task.FromResult(audio); });
            int freshCalls = 0;
            byte[] fresh = await cache.TakeAsync(changed, (_, _) => { freshCalls++; return Task.FromResult(new byte[] {9}); }, default);
            check(oldToken.IsCancellationRequested && freshCalls == 1 && fresh[0] == 9,
                "Changed engine, voice, speed, position or text discards stale prefetch");
        }

        CancellationToken stoppedToken = default;
        cache.Prime(key, (_, token) => { stoppedToken = token; return Task.FromResult(audio); });
        cache.Clear();
        check(stoppedToken.IsCancellationRequested, "Stop cancels speculative generation");

        var uncooperative = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.Prime(key, (_, _) => uncooperative.Task);
        using CancellationTokenSource stop = new();
        Task<byte[]> stale = cache.TakeAsync(key, (_, _) => Task.FromResult(audio), stop.Token);
        stop.Cancel();
        uncooperative.SetResult(audio);
        try { await stale; throw new Exception("Stopped audio was returned"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS: Late results cannot play after Stop"); }

        cache.Prime(key, (_, _) => Task.FromException<byte[]>(new HttpRequestException("Temporary prefetch failure")));
        int retries = 0;
        byte[] retry = await cache.TakeAsync(key, (_, _) => { retries++; return Task.FromResult(audio); }, default);
        check(retries == 1 && ReferenceEquals(retry, audio), "Failed lookahead retries when that section is needed");
        cache.Clear();
    }
}
