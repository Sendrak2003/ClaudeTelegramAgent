using System.Text.Json;
using ClaudeTelegramAgent.Application.Abstractions;
using ClaudeTelegramAgent.Application.Contracts;
using ClaudeTelegramAgent.Domain;

namespace ClaudeTelegramAgent.Infrastructure.Sessions;

public sealed class JsonFileSessionStore : ISessionStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, ChatState> _chats;

    public JsonFileSessionStore(string filePath)
    {
        _filePath = filePath;
        _chats = Load(filePath);
    }

    public string? GetActiveSessionId(ChatId chatId)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(chatId.ToString());
            return state.Slots[state.ActiveSlot].SessionId;
        }
    }

    public void SetActiveSessionId(ChatId chatId, string sessionId)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(chatId.ToString());
            state.Slots[state.ActiveSlot].SessionId = sessionId;
            Save();
        }
    }

    public void ClearActiveSession(ChatId chatId)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(chatId.ToString());
            state.Slots[state.ActiveSlot].SessionId = null;
            Save();
        }
    }

    public int CreateChat(ChatId chatId)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(chatId.ToString());
            var newSlot = state.NextSlot++;
            state.Slots[newSlot] = new SlotState { CreatedAtUtc = DateTimeOffset.UtcNow };
            state.ActiveSlot = newSlot;
            Save();
            return newSlot;
        }
    }

    public bool SwitchChat(ChatId chatId, int slotNumber)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(chatId.ToString());
            if (!state.Slots.ContainsKey(slotNumber))
            {
                return false;
            }

            state.ActiveSlot = slotNumber;
            Save();
            return true;
        }
    }

    public IReadOnlyList<ChatSlotInfo> ListChats(ChatId chatId)
    {
        lock (_lock)
        {
            var state = GetOrCreateState(chatId.ToString());
            return state.Slots
                .OrderBy(kv => kv.Key)
                .Select(kv => new ChatSlotInfo(kv.Key, kv.Key == state.ActiveSlot, kv.Value.SessionId is not null, kv.Value.CreatedAtUtc))
                .ToList();
        }
    }

    private ChatState GetOrCreateState(string key)
    {
        if (!_chats.TryGetValue(key, out var state))
        {
            state = new ChatState();
            state.Slots[1] = new SlotState { CreatedAtUtc = DateTimeOffset.UtcNow };
            _chats[key] = state;
        }

        if (!state.Slots.ContainsKey(state.ActiveSlot))
        {
            state.Slots[state.ActiveSlot] = new SlotState { CreatedAtUtc = DateTimeOffset.UtcNow };
        }

        return state;
    }

    private static Dictionary<string, ChatState> Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, ChatState>();
        }

        var json = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, ChatState>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ChatState>>(json) ?? new Dictionary<string, ChatState>();
        }
        catch (JsonException)
        {
            // Старый формат файла (одна сессия на чат, без слотов) — начинаем с чистого листа.
            return new Dictionary<string, ChatState>();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_chats, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private sealed class ChatState
    {
        public int ActiveSlot { get; set; } = 1;

        public int NextSlot { get; set; } = 2;

        public Dictionary<int, SlotState> Slots { get; set; } = new();
    }

    private sealed class SlotState
    {
        public string? SessionId { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}
