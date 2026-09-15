using System.Text;
using System.Text.RegularExpressions;

namespace Puluj.Processing.Text;

public interface INormalizer
{
    NormalizedMessage Normalize(string text);
}

/// <summary>
/// Cleans a raw message and splits it into segments (one fact per segment is the parser's assumption).
/// Emojis common in monitoring channels are turned into words so aliases can match them.
/// </summary>
public sealed partial class Normalizer : INormalizer
{
    /// <summary>`normalization_version` of the contracts (ADR-0003): bump when the output of <see cref="Normalize"/> changes.</summary>
    public const string Version = "norm-1";

    private static readonly Dictionary<string, string> EmojiWords = new()
    {
        ["🛵"] = "шахед",
        ["🚀"] = "ракета",
        ["✈️"] = "авіація",
        ["✈"] = "авіація",
        ["🛩️"] = "авіація",
        ["🛩"] = "авіація",
        ["🚁"] = "гелікоптер",
        ["💥"] = "вибух",
        ["🔴"] = "тривога",
        ["🟢"] = "відбій",
        ["🟡"] = "",
        ["🚨"] = "тривога",
        ["⚠️"] = "",
        ["⚠"] = "",
        ["❗"] = "",
        ["❗️"] = "",
        ["‼️"] = "",
        ["🎯"] = "",
        ["📍"] = "",
        ["➡️"] = "курсом на",
        ["➡"] = "курсом на",
        ["→"] = "курсом на",
        ["⬆️"] = "у північному напрямку",
        ["⬇️"] = "у південному напрямку",
        ["⬅️"] = "у західному напрямку",
        ["↗️"] = "у північно-східному напрямку",
        ["↘️"] = "у південно-східному напрямку",
        ["↙️"] = "у південно-західному напрямку",
        ["↖️"] = "у північно-західному напрямку",
    };

    private static readonly HashSet<string> Abbreviations = ["м", "обл", "с", "смт", "р-н", "вул", "пд", "пн", "сх", "зх", "ст", "о", "н.п", "н", "п", "напр", "т.ч", "год", "хв", "кр", "бр", "ін", "тис"];

    public NormalizedMessage Normalize(string text)
    {
        var cleaned = Clean(text);
        var segments = new List<Segment>();
        foreach (var sentence in Split(cleaned))
        {
            var tokens = Tokenizer.Tokenize(sentence);
            if (tokens.Count == 0)
            {
                continue;
            }
            segments.Add(new Segment(segments.Count, sentence, tokens));
        }
        return new NormalizedMessage(cleaned, segments, DetectLanguage(cleaned));
    }

    public static string Clean(string text)
    {
        var s = text.Normalize(NormalizationForm.FormC);
        s = UrlRegex().Replace(s, " ");
        s = MarkdownLinkRegex().Replace(s, "$1");
        s = s.Replace("**", "").Replace("__", "");
        foreach (var (emoji, word) in EmojiWords)
        {
            s = s.Replace(emoji, $" {word} ");
        }
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'ё' => 'е',
                '’' or '`' or 'ʼ' or '‘' => '\'',
                '‑' or '‐' or '–' or '—' or '−' => '-',
                '«' or '»' or '"' or '“' or '”' => ' ',
                _ when char.IsSurrogate(ch) || (ch >= '☀' && ch <= '➿') || ch == '️' => ' ',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Lines first, then sentences; abbreviations like "м." do not end a sentence.</summary>
    private static IEnumerable<string> Split(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('-', '•', '·', '*', '>', ' ');
            if (line.Length == 0)
            {
                continue;
            }
            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] is not ('.' or '!' or '?' or ';'))
                {
                    continue;
                }
                if (line[i] == '.' && IsAbbreviationBefore(line, i))
                {
                    continue;
                }
                if (line[i] == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1]))
                {
                    continue;
                }
                var piece = line[start..(i + 1)].Trim();
                if (piece.Length > 1)
                {
                    yield return piece;
                }
                start = i + 1;
            }
            var rest = line[start..].Trim();
            if (rest.Length > 0)
            {
                yield return rest;
            }
        }
    }

    private static bool IsAbbreviationBefore(string line, int dot)
    {
        var j = dot - 1;
        while (j >= 0 && (char.IsLetter(line[j]) || line[j] == '-' || line[j] == '.'))
        {
            j--;
        }
        var word = line[(j + 1)..dot];
        return word.Length > 0 && (Abbreviations.Contains(word) || word.Length == 1);
    }

    private static string DetectLanguage(string s)
    {
        int uk = 0, ru = 0, latin = 0;
        foreach (var ch in s)
        {
            switch (ch)
            {
                case 'і' or 'ї' or 'є' or 'ґ': uk++; break;
                case 'ы' or 'э' or 'ъ': ru++; break;
                case >= 'a' and <= 'z': latin++; break;
            }
        }
        if (uk == 0 && ru == 0)
        {
            return latin > 0 ? "en" : "uk";
        }
        return ru > uk ? "ru" : "uk";
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkRegex();
}
