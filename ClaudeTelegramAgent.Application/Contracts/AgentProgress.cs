namespace ClaudeTelegramAgent.Application.Contracts;

public enum AgentProgressKind
{
    Thinking,
    ToolStarted,
    ToolFinished,
    TextStarted,
    TextDelta,
}

/// <param name="Text">Имя инструмента для ToolStarted, очередной кусок текста для TextDelta.</param>
public sealed record AgentProgress(AgentProgressKind Kind, string? Text = null);
