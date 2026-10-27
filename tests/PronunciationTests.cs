using TextReader;

internal static class PronunciationTests
{
    internal static void Run(Action<bool,string> check)
    {
        const string present = "\u0279\u02c8id", past = "\u0279\u02c8\u025bd";
        foreach (var (text,word,sound) in new[] {
            ("Read the pamphlet.","Read",present),
            ("The pamphlet read, \"All will eat.\"","read",past),
            ("I will read the pamphlet.","read",present),
            ("Please read the pamphlet.","read",present),
            ("The pamphlet read: Welcome to the museum.","read",past),
            ("The sign clearly read, Keep out.","read",past),
            ("I have read the pamphlet.","read",past),
            ("She hasn't read it yet.","read",past),
            ("She hasn’t read it yet.","read",past),
            ("I will have read it by tomorrow.","read",past),
            ("I have to read the pamphlet.","read",present),
            ("You should read the pamphlet.","read",present),
            ("Did you read the pamphlet yesterday?","read",present),
            ("Have you read the pamphlet?","read",past),
            ("I did read the pamphlet.","read",present),
            ("The pamphlet was read aloud.","read",past),
            ("The pamphlet can be read aloud.","read",past),
            ("Yesterday I read the pamphlet.","read",past),
            ("I read the pamphlet every day.","read",present),
        })
        {
            string prepared = Pronunciation.PrepareKokoroText(text,"af_heart");
            check(prepared.Contains($"[{word}](/{sound}/)"), "Contextual read: " + text);
            check(Pronunciation.PrepareKokoroText(prepared,"af_heart") == prepared, "Existing pronunciation hints are not rewritten");
        }
        foreach (var (input, expected) in new[] {
            ("I read(red) the pamphlet.", $"I [read](/{past}/) the pamphlet."),
            ("I read (reed) the pamphlet.", $"I [read](/{present}/) the pamphlet."),
            ("The pamphlet read (red), \"All will eat.\"", $"The pamphlet [read](/{past}/), \"All will eat.\""),
            ("I will read (reed) the pamphlet.", $"I will [read](/{present}/) the pamphlet.") })
            check(Pronunciation.PrepareKokoroText(input,"af_heart") == expected, "Explicit red/reed hints are applied without speaking parentheses");
        foreach(string text in new[] {"I read the pamphlet.","They read the pamphlet.","I read yesterday's paper every day.","[read](/custom/)","Readability matters."})
            check(Pronunciation.PrepareKokoroText(text,"af_heart") == text,"Ambiguous text or user override preserved: "+text);
        check(Pronunciation.PrepareKokoroText("Read the pamphlet.","ff_siwis") == "Read the pamphlet.","Other language voices do not receive English hints");
        string mixed = Pronunciation.PrepareKokoroText("I have read it; please read the pamphlet.","bf_emma");
        check(mixed.Contains($"[read](/{past}/)") && mixed.Contains($"[read](/{present}/)"),"Separate clauses can use different read pronunciations");
        using var piper = SpeechApi.CreateSpeechRequest(false,"Read the pamphlet.","Piper default voice",0);
        string request = piper.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        check(!request.Contains("phonemes") && request.Contains("Read the pamphlet."),"Piper text is unchanged");
    }
}
