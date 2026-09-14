using ClaudeTelegramAgent.Application.Contracts;

namespace ClaudeTelegramAgent.Application.Abstractions;

public interface IIncomingMessageListener
{
    void OnMessageReceived(Func<IncomingMessage, CancellationToken, Task> handler);

    Task StartAsync(CancellationToken ct);
}
