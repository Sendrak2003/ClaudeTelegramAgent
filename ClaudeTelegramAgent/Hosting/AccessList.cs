using System.Text.Json;
using ClaudeTelegramAgent.Infrastructure.Claude;
using ClaudeTelegramAgent.Options;

namespace ClaudeTelegramAgent.Hosting;

/// <summary>
/// Кто может пользоваться ботом. Владельцы заданы в Agent__AllowedUserIds; остальных владелец разрешает или блокирует кнопками,
/// список хранится в файле .agent-data/access.json рядом с остальными данными агента и переживает перезапуски.
/// </summary>
public sealed class AccessList
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly HashSet<long> _owners;
    private readonly HashSet<long> _allowed = [];
    private readonly HashSet<long> _blocked = [];

    public AccessList(AccessControlOptions options, ClaudeCliOptions claude, ILogger<AccessList> logger)
    {
        _owners = [.. options.AllowedUserIds];
        _path = Path.Combine(claude.WorkDir, ".agent-data", "access.json");

        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
                _allowed.UnionWith(data?.Allowed ?? []);
                _blocked.UnionWith(data?.Blocked ?? []);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read access list {Path}, starting empty", _path);
        }
    }

    public IReadOnlyCollection<long> Owners => _owners;

    public bool IsOwner(long userId) => _owners.Contains(userId);

    public bool IsAllowed(long userId)
    {
        lock (_gate)
        {
            return _owners.Contains(userId) || (_allowed.Contains(userId) && !_blocked.Contains(userId));
        }
    }

    public bool IsBlocked(long userId)
    {
        lock (_gate)
        {
            return _blocked.Contains(userId) && !_owners.Contains(userId);
        }
    }

    public void Allow(long userId)
    {
        lock (_gate)
        {
            _blocked.Remove(userId);
            _allowed.Add(userId);
            Save();
        }
    }

    public void Block(long userId)
    {
        lock (_gate)
        {
            if (_owners.Contains(userId))
            {
                return;
            }

            _allowed.Remove(userId);
            _blocked.Add(userId);
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(_allowed.ToList(), _blocked.ToList())));
    }

    private sealed record Stored(List<long> Allowed, List<long> Blocked);
}
