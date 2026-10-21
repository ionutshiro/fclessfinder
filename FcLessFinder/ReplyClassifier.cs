using System.Linq;
using System.Text.RegularExpressions;

namespace FcLessFinder;

/// <summary>Guesses whether a reply to a recruitment tell means yes or no, from common words.</summary>
public static partial class ReplyClassifier
{
    public const string Yes = "Yes";
    public const string No = "No";

    // Positive phrases that contain "no"/"not"; turned into "yes" before the no-check.
    private static readonly string[] YesWithNegativeWords =
    [
        "why not", "no problem", "no prob", "no probs", "no worries", "not a problem", "don't mind", "dont mind",
        "wouldn't mind", "wouldnt mind",
    ];

    // Checked first, so "no thanks" or "not interested" win over a "sure" elsewhere.
    private static readonly string[] NoPhrases =
    [
        "no thanks", "no thank you", "no thx", "no ty", "not interested", "not really", "not right now",
        "not now", "not looking", "maybe later", "im good", "i'm good", "i am good", "im fine", "i'm fine",
        "im ok", "i'm ok", "already in", "already have", "already got", "have an fc", "have a fc",
        "got an fc", "got a fc", "my own fc", "pass", "no",
        "nope", "nah", "naw", "n",
    ];

    private static readonly string[] YesPhrases =
    [
        "why not", "sounds good", "sounds great", "i'm in", "im in", "count me in", "i'd love", "id love",
        "would love", "i'd like", "id like", "interested", "of course", "go ahead", "send it",
        "yes", "yeah", "yea", "ya", "yep", "yup", "ye", "yas", "sure", "ok", "okay", "okey", "k", "kk",
        "alright", "absolutely", "definitely", "please", "pls", "plz", "invite", "inv", "y", "si", "oui", "ja",
    ];

    /// <summary>Returns <see cref="Yes"/>, <see cref="No"/>, or null when the reply is unclear.</summary>
    public static string? Classify(string message)
    {
        // Lowercase, keep letters/digits/apostrophes, pad with spaces so phrases match whole words.
        var text = " " + string.Join(' ', Words().Matches(message.ToLowerInvariant()).Select(m => m.Value)) + " ";
        if (text.Trim().Length == 0)
            return null;
        foreach (var p in YesWithNegativeWords)
            text = text.Replace($" {p} ", " yes ");
        if (NoPhrases.Any(p => text.Contains($" {p} ")))
            return No;
        if (YesPhrases.Any(p => text.Contains($" {p} ")))
            return Yes;
        return null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Words();
}
