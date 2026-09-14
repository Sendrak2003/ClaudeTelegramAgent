using System.Globalization;

namespace ClaudeTelegramAgent.Domain;

public readonly record struct ChatId(long Value)
{
    public static implicit operator long(ChatId chatId) => chatId.Value;

    public static implicit operator ChatId(long value) => new(value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
