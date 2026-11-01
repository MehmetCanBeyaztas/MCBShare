namespace MCBShare.Core;

public sealed class Device
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";

    /// <summary>Kısayol yalnızca PC'ye gönderir; sayfası olmadığı için metin alamaz.</summary>
    public bool IsShortcut { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class DeviceFile
{
    public List<Device> Devices { get; set; } = [];
}

public sealed class DeviceRegistry
{
    private readonly JsonStore<DeviceFile> _store;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<Device> _devices;

    public DeviceRegistry(JsonStore<DeviceFile> store, TimeProvider time)
    {
        _store = store;
        _time = time;
        _devices = store.Load().Devices;
    }

    public event Action? Changed;

    public IReadOnlyList<Device> Devices
    {
        get { lock (_lock) return _devices.ToList(); }
    }

    /// <summary>Yeni cihaz kaydeder. Anahtar yalnızca burada döner, sonra geri alınamaz.</summary>
    public (Device Device, string Key) Register(string name, bool isShortcut = false)
    {
        var key = Secrets.NewKey();
        var cleanName = name.Trim();
        if (cleanName.Length == 0) cleanName = "Telefon";
        if (cleanName.Length > 60) cleanName = cleanName[..60];
        var device = new Device
        {
            Id = Secrets.NewCode(12),
            Name = cleanName,
            KeyHash = Secrets.Hash(key),
            IsShortcut = isShortcut,
            RegisteredAt = _time.GetUtcNow(),
        };
        lock (_lock)
        {
            _devices.Add(device);
            Save();
        }
        Changed?.Invoke();
        return (device, key);
    }

    public Device? Authenticate(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var hash = Secrets.Hash(key);
        lock (_lock)
        {
            var device = _devices.FirstOrDefault(d => d.KeyHash == hash);
            if (device is not null) device.LastSeenAt = _time.GetUtcNow();
            return device;
        }
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _devices.RemoveAll(d => d.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    private void Save() => _store.Save(new DeviceFile { Devices = _devices });
}

/// <summary>Bir IP'den gelen başarısız denemeleri dakika başına sınırlar.</summary>
public sealed class AttemptLimiter
{
    private readonly TimeProvider _time;
    private readonly int _maxPerMinute;
    private readonly Dictionary<string, (int Count, DateTimeOffset Start)> _entries = [];
    private readonly Lock _lock = new();

    public AttemptLimiter(TimeProvider time, int maxPerMinute = 20)
    {
        _time = time;
        _maxPerMinute = maxPerMinute;
    }

    public bool IsBlocked(string ip)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(ip, out var e)
                && _time.GetUtcNow() - e.Start < TimeSpan.FromMinutes(1)
                && e.Count >= _maxPerMinute;
        }
    }

    public void RecordFailure(string ip)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_entries.TryGetValue(ip, out var e) && now - e.Start < TimeSpan.FromMinutes(1))
                _entries[ip] = (e.Count + 1, e.Start);
            else
                _entries[ip] = (1, now);
        }
    }
}
