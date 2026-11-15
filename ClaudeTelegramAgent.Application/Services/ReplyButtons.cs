using System.Text.RegularExpressions;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Кнопки продолжения под ответом. Агент в конце ответа пишет маркер <c>[[кнопки: Вариант 1 | Вариант 2]]</c>;
/// маркер вырезается из текста, а варианты показываются как inline-кнопки в Telegram.
/// </summary>
public static class ReplyButtons
{
    public const int MaxButtons = 6;
    public const int MaxLabelLength = 60;

    private static readonly Regex Marker = new(@"\[\[\s*кнопки\s*:\s*(.*?)\]\]", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Возвращает текст без маркера и список подписей кнопок (пустой, если маркера нет).</summary>
    public static (string Text, IReadOnlyList<string> Buttons) Extract(string text)
    {
        var buttons = new List<string>();
        var clean = Marker.Replace(text, m =>
        {
            foreach (var part in m.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var label = part.Length > MaxLabelLength ? part[..MaxLabelLength].TrimEnd() : part;
                if (buttons.Count < MaxButtons && !buttons.Contains(label))
                {
                    buttons.Add(label);
                }
            }

            return string.Empty;
        });

        return (clean.TrimEnd(), buttons);
    }

    /// <summary>Для живого черновика и озвучки: убирает готовый или ещё недописанный маркер и строки таблицы (`| a | b |`, `|---|---|`).</summary>
    public static string StripForStreaming(string text) => StripForStreaming(text, out _);

    /// <summary>То же, а <paramref name="hadTable"/> сообщает, что в тексте была таблица (в черновике её показывают заглушкой).</summary>
    public static string StripForStreaming(string text, out bool hadTable)
    {
        var (clean, _) = Extract(text);
        var open = clean.LastIndexOf("[[", StringComparison.Ordinal);
        if (open >= 0 && clean.IndexOf("]]", open, StringComparison.Ordinal) < 0)
        {
            clean = clean[..open].TrimEnd();
        }

        var lines = clean.Split('\n');
        var kept = lines.Where(l => !l.TrimStart().StartsWith('|')).ToArray();
        hadTable = kept.Length != lines.Length;
        return hadTable ? Regex.Replace(string.Join('\n', kept), @"\n{3,}", "\n\n").TrimEnd() : clean;
    }
}
