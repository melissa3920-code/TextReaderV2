using System.Text.RegularExpressions;

namespace TextReader;

internal static class Pronunciation
{
    private const string Present = "\u0279\u02c8id";
    private const string Past = "\u0279\u02c8\u025bd";
    private static readonly Regex Words = new(@"\b[a-z]+(?:['’][a-z]+)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Clauses = new(@"[^.!?;:\n,]+", RegexOptions.CultureInvariant);
    private static readonly Regex Overrides = new(@"\[[^\[\]]*\]\([^\n)]*\)", RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitRead = new(@"\b(read)\s*\((red|reed)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Auxiliary = new(
        @"\b(have|has|had|haven't|hasn't|hadn't|will|would|can|could|should|must|may|might|do|does|did|don't|doesn't|didn't|am|is|are|was|were|be|been|being|isn't|aren't|wasn't|weren't|to)(?: (?:i|you|we|they|he|she|it))?(?: (?:not|never|already|just|ever|often|always|really|yet))*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DocumentSubject = new(
        @"^(?:(?:the|a|an|this|that|my|your|his|her|our|their|its) (?:\w+ ){0,2}(?:pamphlet|sign|label|notice|letter|note|message|headline|poster|banner|placard|email|document|report)|it)(?: (?:clearly|simply|plainly|actually|originally))*$",
        RegexOptions.CultureInvariant);

    internal static string PrepareKokoroText(string text, string voice)
    {
        if (!new[] { "af_", "am_", "bf_", "bm_" }.Any(prefix => voice.StartsWith(prefix, StringComparison.Ordinal)))
            return text;
        var protectedSpans = Overrides.Matches(text).Cast<Match>().ToArray();
        List<(int Start, int Length, string Value)> replacements = new();
        Match[] explicitHints = ExplicitRead.Matches(text).Cast<Match>()
            .Where(hint => !protectedSpans.Any(span => span.Index <= hint.Index && hint.Index < span.Index + span.Length)).ToArray();
        foreach (Match hint in explicitHints)
        {
            string sound = hint.Groups[2].Value.Equals("red", StringComparison.OrdinalIgnoreCase) ? Past : Present;
            replacements.Add((hint.Index, hint.Length, $"[{hint.Groups[1].Value}](/{sound}/)"));
        }
        foreach (Match clause in Clauses.Matches(text))
        {
            Match[] words = Words.Matches(clause.Value).Cast<Match>().ToArray();
            string[] lower = words.Select(word => word.Value.ToLowerInvariant().Replace('’', '\'')).ToArray();
            for (int index = 0; index < words.Length; index++)
            {
                if (lower[index] != "read") continue;
                Match word = words[index];
                int start = clause.Index + word.Index;
                if (protectedSpans.Any(span => span.Index <= start && start < span.Index + span.Length) ||
                    explicitHints.Any(hint => hint.Index <= start && start < hint.Index + hint.Length)) continue;
                string before = string.Join(" ", lower.Skip(Math.Max(0, index - 8)).Take(Math.Min(index, 8)));
                string after = string.Join(" ", lower.Skip(index + 1).Take(8));
                string? sound = ReadSound(before, after);
                if (sound is not null) replacements.Add((start, word.Length, $"[{word.Value}](/{sound}/)"));
            }
        }
        foreach (var change in replacements.OrderByDescending(change => change.Start))
            text = text.Remove(change.Start, change.Length).Insert(change.Start, change.Value);
        return text;
    }

    private static string? ReadSound(string before, string after)
    {
        Match auxiliary = Auxiliary.Match(before);
        if (auxiliary.Success)
            return new[] { "have", "has", "had", "haven't", "hasn't", "hadn't", "am", "is", "are", "was", "were", "be", "been", "being", "isn't", "aren't", "wasn't", "weren't" }
                .Contains(auxiliary.Groups[1].Value) ? Past : Present;
        if ((before == "" || before == "please") && Regex.IsMatch(after, @"^(?:the|a|an|this|that|these|those|it|them|aloud|carefully|slowly)\b"))
            return Present;
        // A singular document subject takes "reads" in the present, so bare "read" is past here.
        if (DocumentSubject.IsMatch(before)) return Past;
        string context = before + " read " + after;
        bool past = Regex.IsMatch(context, @"\byesterday\b|\blast (?:night|week|month|year)\b|\b\w+ ago\b");
        bool habitual = Regex.IsMatch(context, @"\bevery (?:day|night|evening|morning|week)\b|\busually\b|\bdaily\b");
        return past == habitual ? null : past ? Past : Present;
    }
}
