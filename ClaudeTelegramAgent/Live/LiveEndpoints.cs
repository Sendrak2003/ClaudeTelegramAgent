using ClaudeTelegramAgent.Application.Services;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Live;

/// <summary>
/// Серверная часть мини-приложения с живой озвучкой: события ответа вместе со звуком (длинным запросом)
/// и пробный запуск озвучки без самого бота. Страница плеера лежит в wwwroot/live.
/// </summary>
public static class LiveEndpoints
{
    private static readonly TimeSpan InitDataMaxAge = TimeSpan.FromHours(24);
    private static readonly char[] SentenceEnds = ['.', '!', '?', '…', '\n'];

    public static void MapLive(
        this WebApplication app, string botToken, IReadOnlySet<long> allowedUserIds, string? devKey)
    {
        var live = app.MapGroup("/live");

        live.MapGet("/poll", async (HttpContext http, LiveVoiceHub hub, LiveVoiceService service, ILogger<LiveVoiceHub> logger, long? after, string? epoch) =>
        {
            if (Authorize(http.Request, botToken, allowedUserIds, devKey) is not { } userId)
            {
                logger.LogWarning("Live player rejected: no valid Telegram data (initData sent: {HasInitData})", http.Request.Headers.ContainsKey("X-Init-Data"));
                return Results.Unauthorized();
            }

            service.WarmUp();

            var from = epoch == hub.Epoch ? after ?? 0 : 0;
            var events = await hub.WaitAsync(new ChatId(userId), from, TimeSpan.FromSeconds(20), http.RequestAborted);

            return Results.Json(new { epoch = hub.Epoch, events = events.Select(e => new { seq = e.Seq, data = e.Json }) });
        });

        live.MapPost("/simulate", (SimulateRequest request, HttpContext http, LiveVoiceService service) =>
        {
            if (Authorize(http.Request, botToken, allowedUserIds, devKey) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var text = (request.Text ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > 3000)
            {
                return Results.BadRequest("Нужен текст до 3000 символов.");
            }

            var charsPerSecond = Math.Clamp(request.CharsPerSecond ?? 120, 10, 2000);
            var thinkMs = Math.Clamp(request.ThinkMs ?? 1500, 0, 30000);

            var toolStage = string.IsNullOrWhiteSpace(request.ToolStage) ? null : request.ToolStage.Trim();
            _ = Task.Run(() => SimulateAsync(service.Start(new ChatId(userId)), text, charsPerSecond, thinkMs, toolStage));
            return Results.Accepted();
        });
    }

    /// <summary>Подаёт текст в озвучку так же, как это делает агент: сначала «думает», потом дописывает ответ по кускам.</summary>
    private static async Task SimulateAsync(LiveVoiceRun run, string text, int charsPerSecond, int thinkMs, string? toolStage)
    {
        const int tickMs = 50;

        run.OnStage("Думаю");
        if (toolStage is not null)
        {
            await Task.Delay(thinkMs / 2);
            run.Restart();
            run.OnStage(toolStage);
            await Task.Delay(thinkMs - thinkMs / 2);
        }
        else
        {
            await Task.Delay(thinkMs);
        }

        run.Restart();

        var step = Math.Max(1, charsPerSecond * tickMs / 1000);
        for (var index = 0; index < text.Length; index += step)
        {
            var delta = text.AsSpan(index, Math.Min(step, text.Length - index));
            if (delta.IndexOfAny(SentenceEnds) >= 0)
            {
                run.OnAnswerGrowing(text[..(index + delta.Length)]);
            }

            await Task.Delay(tickMs);
        }

        run.Deliver(text);
    }

    private static long? Authorize(HttpRequest request, string botToken, IReadOnlySet<long> allowedUserIds, string? devKey)
    {
        if (!string.IsNullOrEmpty(devKey) && request.Headers["X-Dev-Key"] == devKey)
        {
            return allowedUserIds.First();
        }

        var userId = TelegramInitData.ValidateUserId(request.Headers["X-Init-Data"], botToken, InitDataMaxAge);
        return userId is { } id && allowedUserIds.Contains(id) ? id : null;
    }

    private sealed record SimulateRequest(string? Text, int? CharsPerSecond, int? ThinkMs, string? ToolStage);
}
