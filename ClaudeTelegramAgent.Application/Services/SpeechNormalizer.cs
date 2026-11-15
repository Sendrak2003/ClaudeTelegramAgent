using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeTelegramAgent.Application.Services;

/// <summary>
/// Приведение аббревиатур и терминов к тому, как их произносят вслух. Синтезатор читает «SSD» и «США» по-своему
/// и часто неверно, поэтому подставляем произношение заранее. Словарь Exceptions проверяется первым и важнее общих правил.
/// </summary>
public static partial class SpeechNormalizer
{
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["США"] = "сэ ша а",
        ["ВУЗ"] = "вуз",
        ["ООН"] = "о о эн",
        ["РФ"] = "эр эф",
        ["ИИ"] = "и и",
        ["ОЗУ"] = "оу зэ у",
        ["Wi-Fi"] = "вай фай",
        ["WiFi"] = "вай фай",
        ["GitHub"] = "гитхаб",
        ["Telegram"] = "телеграм",
        ["YouTube"] = "ютуб",
        ["Docker"] = "докер",
        ["Linux"] = "линукс",
        ["Windows"] = "виндоус",
        ["API"] = "эй пи ай",
        ["SSD"] = "эс эс ди",
        ["HDD"] = "эйч ди ди",
        ["GPU"] = "джи пи ю",
        ["CPU"] = "си пи ю",
        ["RAM"] = "рэм",
        ["USB"] = "ю эс би",
        ["PDF"] = "пи ди эф",
        ["URL"] = "ю эр эл",
        ["JSON"] = "джейсон",
        ["TTS"] = "ти ти эс",
        ["LLM"] = "эл эл эм",
    };

    private static readonly Dictionary<char, string> LatinLetters = new()
    {
        ['A'] = "эй", ['B'] = "би", ['C'] = "си", ['D'] = "ди", ['E'] = "и", ['F'] = "эф", ['G'] = "джи",
        ['H'] = "эйч", ['I'] = "ай", ['J'] = "джей", ['K'] = "кей", ['L'] = "эл", ['M'] = "эм", ['N'] = "эн",
        ['O'] = "оу", ['P'] = "пи", ['Q'] = "кью", ['R'] = "эр", ['S'] = "эс", ['T'] = "ти", ['U'] = "ю",
        ['V'] = "ви", ['W'] = "дабл ю", ['X'] = "икс", ['Y'] = "уай", ['Z'] = "зэд",
    };

    private static readonly Dictionary<string, string> Digits = new()
    {
        ["2"] = "два", ["3"] = "три", ["4"] = "четыре", ["5"] = "пять", ["6"] = "шесть",
    };

    public static string Apply(string text)
    {
        text = GenerationRegex().Replace(text, m => Digits.TryGetValue(m.Groups[1].Value, out var word)
            ? $"{word} джи"
            : m.Value);

        text = TermRegex().Replace(text, m => Exceptions.TryGetValue(m.Value, out var spoken) ? spoken : m.Value);
        return LatinAcronymRegex().Replace(text, m => Spell(m.Value));
    }

    private static string Spell(string acronym)
    {
        var sb = new StringBuilder();
        foreach (var letter in acronym)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(LatinLetters.TryGetValue(letter, out var name) ? name : letter.ToString());
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"(?<![\p{L}\d])[\p{L}][\p{L}\d]*(?:-[\p{L}\d]+)*(?![\p{L}\d])")]
    private static partial Regex TermRegex();

    [GeneratedRegex(@"(?<![\p{L}\d])([2-6])G(?![\p{L}\d])")]
    private static partial Regex GenerationRegex();

    [GeneratedRegex(@"(?<![\p{L}\d])[A-Z]{2,5}(?![\p{L}\d])")]
    private static partial Regex LatinAcronymRegex();
}
