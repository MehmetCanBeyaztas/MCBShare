using System.Security.Cryptography;

namespace MCBShare.Core;

public sealed class HistoryItem
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string DeviceName { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class HistoryFile
{
    public List<HistoryItem> Items { get; set; } = [];
}

/// <summary>Alınan dosyaların kaydı. Panel en yeni birkaçını gösterir; tamamı tekrar kontrolünde kullanılır.</summary>
public sealed class ShareHistory
{
    public const int PanelCount = 20;

    private readonly JsonStore<HistoryFile> _store;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<HistoryItem> _items;

    public ShareHistory(JsonStore<HistoryFile> store, TimeProvider time)
    {
        _store = store;
        _time = time;
        _items = store.Load().Items;
    }

    public event Action? Changed;

    /// <summary>En yeni üstte. Silinmiş dosyalar listelenmez.</summary>
    public IReadOnlyList<HistoryItem> Recent()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(i => i.ReceivedAt)
                .Where(i => File.Exists(i.Path))
                .Take(PanelCount)
                .ToList();
        }
    }

    public void Add(HistoryItem item)
    {
        lock (_lock)
        {
            _items.Add(item);
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string id, bool deleteFile)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null) return;
            if (deleteFile && File.Exists(item.Path)) File.Delete(item.Path);
            _items.Remove(item);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Aynı içerikli kayıtlı dosyayı bulur. Hash yalnızca boyutu tutan kayıt varsa hesaplanır.</summary>
    public HistoryItem? FindDuplicate(string candidatePath, long size)
    {
        List<HistoryItem> sameSize;
        lock (_lock) sameSize = _items.Where(i => i.Size == size && File.Exists(i.Path)).ToList();
        if (sameSize.Count == 0) return null;

        var hash = HashFile(candidatePath);
        foreach (var item in sameSize)
        {
            item.Sha256 ??= HashFile(item.Path);
            if (item.Sha256 == hash) return item;
        }
        lock (_lock) Save();
        return null;
    }

    /// <summary>Saklama süresini aşan kayıtları listeden çıkarır; alınan dosyalara dokunmaz.</summary>
    public void Prune(int retentionDays)
    {
        if (retentionDays <= 0) return;
        var limit = _time.GetUtcNow().AddDays(-retentionDays);
        int removed;
        lock (_lock)
        {
            removed = _items.RemoveAll(i => i.ReceivedAt < limit);
            if (removed > 0) Save();
        }
        if (removed > 0) Changed?.Invoke();
    }

    private void Save() => _store.Save(new HistoryFile { Items = _items });

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
