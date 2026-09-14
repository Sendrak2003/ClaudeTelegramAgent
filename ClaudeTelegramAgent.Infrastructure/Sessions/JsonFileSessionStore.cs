using System.Text.Json;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Infrastructure.Sessions;

public sealed class JsonFileSessionStore : ISessionStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _sessions;

    public JsonFileSessionStore(string filePath)
    {
        _filePath = filePath;
        _sessions = Load(filePath);
    }

    public string? Get(ChatId chatId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(chatId.ToString(), out var sessionId) ? sessionId : null;
        }
    }

    public void Set(ChatId chatId, string sessionId)
    {
        lock (_lock)
        {
            _sessions[chatId.ToString()] = sessionId;
            Save();
        }
    }

    public void Clear(ChatId chatId)
    {
        lock (_lock)
        {
            if (_sessions.Remove(chatId.ToString()))
            {
                Save();
            }
        }
    }

    private static Dictionary<string, string> Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, string>();
        }

        var json = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}
