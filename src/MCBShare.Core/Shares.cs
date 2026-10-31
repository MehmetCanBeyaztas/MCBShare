namespace MCBShare.Core;

public enum ShareState { WaitingForPhone, Downloading, Downloaded, Expired, Cancelled }

public sealed record ShareFile(string Path, string Name, long Size);

public sealed record ShareSnapshot(
    string Code,
    IReadOnlyList<ShareFile> Files,
    ShareState State,
    DateTimeOffset ExpiresAt,
    long BytesSent,
    long TotalBytes)
{
    public double Progress => TotalBytes == 0 ? 1 : (double)BytesSent / TotalBytes;
}

/// <summary>PC'den telefona paylaşım kodlarını ve ömürlerini yönetir.</summary>
public sealed class ShareService
{
    public static readonly TimeSpan WaitLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DownloadLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Sunucu son baytı okuduğunda telefon onu henüz almamış olabilir; kopan indirme bu sürede tamamlanabilir.</summary>
    public static readonly TimeSpan CompletedLifetime = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly FileLog _log;
    private readonly Dictionary<string, Share> _shares = [];
    private readonly Lock _lock = new();

    public ShareService(TimeProvider time, FileLog log)
    {
        _time = time;
        _log = log;
    }

    public event Action<ShareSnapshot>? ShareChanged;

    public ShareSnapshot Create(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists)
            .Select(p => new FileInfo(p))
            .Select(i => new ShareFile(i.FullName, i.Name, i.Length))
            .ToList();
        if (files.Count == 0) throw new ArgumentException("No files to share", nameof(paths));

        var share = new Share(Secrets.NewCode(8), files, _time.GetUtcNow() + WaitLifetime);
        lock (_lock)
        {
            _shares[share.Code] = share;
            share.Timer = _time.CreateTimer(_ => End(share.Code, ShareState.Expired), null, WaitLifetime, Timeout.InfiniteTimeSpan);
        }
        _log.Info($"Share created: {files.Count} file(s)");
        var snapshot = share.Snapshot();
        ShareChanged?.Invoke(snapshot);
        return snapshot;
    }

    public void Cancel(string code) => End(code, ShareState.Cancelled);

    public ShareSnapshot? Get(string code)
    {
        lock (_lock) return _shares.TryGetValue(code, out var share) ? share.Snapshot() : null;
    }

    /// <summary>İndirme isteğini işaretler. İlk indirme 5 dakikalık süreyi başlatır.</summary>
    public ShareFile? BeginDownload(string code, int index)
    {
        ShareSnapshot? changed = null;
        ShareFile? file;
        lock (_lock)
        {
            if (!_shares.TryGetValue(code, out var share) || index < 0 || index >= share.Files.Count)
                return null;
            file = share.Files[index];
            if (share.State == ShareState.WaitingForPhone)
            {
                share.State = ShareState.Downloading;
                share.ExpiresAt = _time.GetUtcNow() + DownloadLifetime;
                share.Timer?.Change(DownloadLifetime, Timeout.InfiniteTimeSpan);
                changed = share.Snapshot();
            }
        }
        if (changed is not null) ShareChanged?.Invoke(changed);
        return file;
    }

    /// <summary>
    /// Okunan bayt aralığını bildirir. Yalnızca baştan kesintisiz okunan kısım sayılır: dosyanın sonunu
    /// yoklayan bir Range isteği indirmeyi bitmiş göstermemeli. Devam eden indirme kod ölse de kesilmez.
    /// </summary>
    public void ReportProgress(string code, int index, long start, long end)
    {
        ShareSnapshot? changed = null;
        lock (_lock)
        {
            if (!_shares.TryGetValue(code, out var share)) return;
            if (start > share.Sent[index] || end <= share.Sent[index]) return;
            share.Sent[index] = end;
            var sent = share.Sent.Sum();
            if (sent >= share.Total && share.State != ShareState.Downloaded)
            {
                share.State = ShareState.Downloaded;
                share.ExpiresAt = _time.GetUtcNow() + CompletedLifetime;
                share.Timer?.Change(CompletedLifetime, Timeout.InfiniteTimeSpan);
                _log.Info("Share downloaded");
            }
            var now = _time.GetTimestamp();
            if (share.State == ShareState.Downloaded || _time.GetElapsedTime(share.LastReport, now) >= TimeSpan.FromMilliseconds(250))
            {
                share.LastReport = now;
                changed = share.Snapshot();
            }
        }
        if (changed is not null) ShareChanged?.Invoke(changed);
    }

    private void End(string code, ShareState state)
    {
        ShareSnapshot snapshot;
        lock (_lock)
        {
            if (!_shares.Remove(code, out var share)) return;
            share.Timer?.Dispose();
            if (state != ShareState.Expired || share.State != ShareState.Downloaded) share.State = state;
            state = share.State;
            snapshot = share.Snapshot();
        }
        _log.Info($"Share ended: {state}");
        ShareChanged?.Invoke(snapshot);
    }

    private sealed class Share(string code, List<ShareFile> files, DateTimeOffset expiresAt)
    {
        public string Code { get; } = code;
        public List<ShareFile> Files { get; } = files;
        public ShareState State { get; set; } = ShareState.WaitingForPhone;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public long[] Sent { get; } = new long[files.Count];
        public long Total { get; } = files.Sum(f => f.Size);
        public long LastReport { get; set; }
        public ITimer? Timer { get; set; }

        public ShareSnapshot Snapshot() => new(Code, Files, State, ExpiresAt, Sent.Sum(), Total);
    }
}

/// <summary>İndirilen dosyayı okurken ulaşılan konumu bildirir.</summary>
internal sealed class ProgressStream(Stream inner, Action<long, long> onRead) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var start = inner.Position;
        var read = inner.Read(buffer, offset, count);
        onRead(start, start + read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var start = inner.Position;
        var read = await inner.ReadAsync(buffer, ct);
        onRead(start, start + read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>İki yönlü pano metni.</summary>
public sealed class ClipboardExchange(TimeProvider time, FileLog log)
{
    public const int MaxBytes = 1024 * 1024;
    public static readonly TimeSpan TextLifetime = TimeSpan.FromMinutes(15);

    private readonly Lock _lock = new();
    private string? _targetId;
    private bool _delivered;
    private string _text = "";
    private DateTimeOffset? _at;

    public event Action<string, string>? TextReceived;

    /// <summary>Hedef cihaz gönderilen metni ilk kez aldığında cihaz adıyla tetiklenir.</summary>
    public event Action<string>? TextDelivered;

    /// <summary>Metni yalnızca verilen cihaza açar. Önceki metnin yerini alır.</summary>
    public void SendToPhone(string deviceId, string text)
    {
        lock (_lock)
        {
            _targetId = deviceId;
            _text = text;
            _at = time.GetUtcNow();
            _delivered = false;
        }
    }

    public (string Text, DateTimeOffset? At) Latest()
    {
        lock (_lock)
        {
            if (_at is { } at && time.GetUtcNow() - at >= TextLifetime)
            {
                _text = "";
                _at = null;
            }
            return (_text, _at);
        }
    }

    internal (string Text, DateTimeOffset? At) FetchForDevice(string deviceId, string deviceName)
    {
        var latest = Latest();
        if (latest.At is null) return latest;
        bool first;
        lock (_lock)
        {
            if (deviceId != _targetId) return ("", null);
            first = !_delivered;
            _delivered = true;
        }
        if (first)
        {
            log.Info($"Clipboard text delivered to {deviceName}");
            TextDelivered?.Invoke(deviceName);
        }
        return latest;
    }

    internal void Receive(string deviceName, string text)
    {
        log.Info($"Clipboard text received from {deviceName}");
        TextReceived?.Invoke(deviceName, text);
    }
}
