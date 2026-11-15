using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ClaudeTelegramAgent.Live;

/// <summary>Проверка подписи данных, которые Telegram передаёт мини-приложению (initData): так видно, что пользователь настоящий.</summary>
public static class TelegramInitData
{
    /// <summary>Id пользователя, если подпись верна и данные свежие; иначе null.</summary>
    public static long? ValidateUserId(string? initData, string botToken, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(initData))
        {
            return null;
        }

        var fields = HttpUtility.ParseQueryString(initData);
        var hash = fields["hash"];
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var dataCheck = string.Join(
            '\n',
            fields.AllKeys.OfType<string>()
                .Where(key => key != "hash")
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => $"{key}={fields[key]}"));

        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheck));

        byte[] given;
        try
        {
            given = Convert.FromHexString(hash);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            return null;
        }

        if (!long.TryParse(fields["auth_date"], out var authDate)
            || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authDate) > maxAge)
        {
            return null;
        }

        try
        {
            using var user = JsonDocument.Parse(fields["user"] ?? "{}");
            return user.RootElement.TryGetProperty("id", out var id) ? id.GetInt64() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
