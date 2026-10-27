namespace TextReader;

internal sealed record SpeechKey(int Index, bool Kokoro, string Text, string Voice, double Rate);

// Accessed on the UI thread; only the background generation task runs concurrently.
internal sealed class SpeechPrefetch
{
    private sealed record Entry(SpeechKey Key, CancellationTokenSource Cancellation, Task<byte[]> Audio);
    private Entry? next;

    internal void Prime(SpeechKey key, Func<SpeechKey, CancellationToken, Task<byte[]>> generate)
    {
        if (next?.Key == key && !next.Audio.IsFaulted && !next.Audio.IsCanceled) return;
        Clear();
        CancellationTokenSource cancellation = new();
        Task<byte[]> audio = GenerateAsync(key, generate, cancellation.Token);
        next = new(key, cancellation, audio);
        _ = audio.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal async Task<byte[]> TakeAsync(SpeechKey key,
        Func<SpeechKey, CancellationToken, Task<byte[]>> generate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Entry? entry = next;
        if (entry?.Key != key)
        {
            Clear();
            return await generate(key, token);
        }
        next = null;
        using CancellationTokenSource cancellation = entry.Cancellation;
        using CancellationTokenRegistration registration = token.Register(() => cancellation.Cancel());
        try
        {
            byte[] audio = await entry.Audio;
            token.ThrowIfCancellationRequested();
            return audio;
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // A failed lookahead must not interrupt the section currently playing.
            return await generate(key, token);
        }
    }

    internal void Clear()
    {
        Entry? entry = next;
        next = null;
        if (entry is null) return;
        entry.Cancellation.Cancel();
        _ = entry.Audio.ContinueWith(task =>
        {
            _ = task.Exception;
            entry.Cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<byte[]> GenerateAsync(SpeechKey key,
        Func<SpeechKey, CancellationToken, Task<byte[]>> generate, CancellationToken token) =>
        await generate(key, token);
}
