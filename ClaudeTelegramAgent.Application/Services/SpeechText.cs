using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Подготовка текста ответа к озвучке: убираем разметку, код, ссылки и эмодзи, затем режем на куски по предложениям.
/// Куски озвучиваются и отправляются по очереди, поэтому первое голосовое приходит, не дожидаясь конца всего ответа.
/// </summary>
public static partial class SpeechText
{
    private const int FirstChunkTarget = 45;
    private const int FirstChunkHeadMax = 70;
    private const int FirstChunkSpaceFrom = 60;
    private const int FirstChunkMinTail = 20;
    private const int FirstChunkSlack = 10;
    private static readonly char[] HeadBreaks = [',', ';', ':', '—', '–', '(', ')', '!', '?', '…', '.'];
    private static int ChunkTarget = 100;
    private static int ChunkMax = 160;

    /// <summary>Размер кусков после первого: быстрому голосу (Piper) подходят длинные, чтобы интонация шла на всё предложение.</summary>
    public static void ConfigureChunks(int target, int max)
    {
        ChunkTarget = target;
        ChunkMax = Math.Max(max, target);
    }

    public static string Clean(string markdown)
    {
        var text = CodeBlockRegex().Replace(markdown, " ");
        text = InlineCodeRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = UrlRegex().Replace(text, " ");
        text = HeadingRegex().Replace(text, string.Empty);
        text = ListMarkerRegex().Replace(text, string.Empty);
        text = text.Replace("**", string.Empty).Replace("__", string.Empty);
        text = text.Replace("₽", " рублей").Replace("%", " процентов").Replace("C#", "Си шарп").Replace("F#", "Эф шарп");
        text = SpeechNormalizer.Apply(text);

        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.OtherSymbol:
                case UnicodeCategory.ModifierSymbol:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                    break;
                default:
                    if (rune.Value is '*' or '_' or '#' or '`' or '>' or '|' or '~')
                    {
                        sb.Append(' ');
                    }
                    else
                    {
                        sb.Append(rune.ToString());
                    }

                    break;
            }
        }

        var lines = sb.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var joined = new StringBuilder();
        foreach (var line in lines)
        {
            joined.Append(line);
            joined.Append(line[^1] is '.' or '!' or '?' or '…' or ':' or ';' ? ' ' : ". ");
        }

        var spaced = WhitespaceRegex().Replace(joined.ToString(), " ");
        return SpaceBeforePunctuationRegex().Replace(spaced, "$1").Trim();
    }

    /// <summary>Чистит текст и режет на куски. Пустой список — озвучивать нечего или ответ слишком длинный.</summary>
    public static IReadOnlyList<string> ToChunks(string markdown, int maxChars)
    {
        var clean = Clean(markdown);
        if (clean.Length == 0 || clean.Length > maxChars || !clean.Any(char.IsLetter))
        {
            return [];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in SentenceBreakRegex().Split(clean))
        {
            var pieces = chunks.Count == 0 ? SplitHead(sentence) : SplitLong(sentence);
            foreach (var piece in pieces)
            {
                var first = chunks.Count == 0;
                var target = first ? FirstChunkTarget : ChunkTarget;
                var overflow = first
                    ? current.Length >= FirstChunkMinTail && current.Length + piece.Length + 1 > FirstChunkHeadMax + FirstChunkSlack
                    : current.Length + piece.Length + 1 > ChunkMax;
                if (current.Length > 0 && (current.Length >= target || overflow))
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(piece);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// Первое предложение ответа, если оно длиннее FirstChunkHeadMax, режем по ближайшему знаку препинания
    /// не раньше FirstChunkTarget символов, а если до пробела после FirstChunkSpaceFrom знака нет, то по пробелу:
    /// первый кусок короткий, голос начинается раньше. Выбирается более ранняя из двух границ. Она не меняется,
    /// пока ответ дописывается, поэтому заготовка озвучки не пересчитывается.
    /// </summary>
    private static IEnumerable<string> SplitHead(string sentence)
    {
        var text = sentence.Trim();
        if (text.Length <= FirstChunkHeadMax)
        {
            return SplitLong(text);
        }

        var punctuation = text.IndexOfAny(HeadBreaks, FirstChunkTarget - 1);
        var space = text.IndexOf(' ', FirstChunkSpaceFrom);
        var cut = punctuation < 0 ? space : space < 0 ? punctuation : Math.Min(punctuation, space);
        if (cut < 0 || cut >= text.Length - 1)
        {
            return SplitLong(text);
        }

        return SplitLong(text[..(cut + 1)]).Concat(SplitLong(text[(cut + 1)..].Trim()));
    }

    private static IEnumerable<string> SplitLong(string sentence)
    {
        var rest = sentence.Trim();
        while (rest.Length > ChunkMax)
        {
            var cut = rest.LastIndexOf(',', ChunkMax - 1, ChunkMax - 1);
            if (cut < ChunkMax / 3)
            {
                cut = rest.LastIndexOf(' ', ChunkMax - 1, ChunkMax - 1);
            }

            if (cut <= 0)
            {
                cut = ChunkMax;
            }

            yield return rest[..(cut + 1)].Trim();
            rest = rest[(cut + 1)..].Trim();
        }

        if (rest.Length > 0)
        {
            yield return rest;
        }
    }

    [GeneratedRegex("```[\\s\\S]*?```")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex("`([^`\\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)\s]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^\s*#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+", RegexOptions.Multiline)]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+([,.!?:;…])")]
    private static partial Regex SpaceBeforePunctuationRegex();

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceBreakRegex();
}
