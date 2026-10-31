using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using Directory = System.IO.Directory;

namespace MCBShare.Core;

public sealed record UploadRequest(
    string Name,
    string? Id = null,
    long Offset = 0,
    long? Size = null,
    long? ModifiedUnixMs = null,
    int? BatchIndex = null,
    int? BatchTotal = null);

public sealed record UploadResult(bool Complete, long Offset, string? Name = null, bool Duplicate = false);

public sealed class ApiException(int status, string error, string message, long? offset = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Error { get; } = error;
    public long? Offset { get; } = offset;
}

public enum TransferState { Running, Completed, Duplicate, Failed }

public sealed record TransferUpdate(
    string TransferId,
    string FileName,
    string DeviceName,
    long Received,
    long? Total,
    TransferState State,
    int? BatchIndex,
    int? BatchTotal,
    string? FinalPath = null);

/// <summary>Alınan dosyayı son hâline getiren isteğe bağlı adım (ör. HEIC → JPG). Yeni yolu döner.</summary>
public interface IReceivedFileProcessor
{
    Task<string> ProcessAsync(string path, CancellationToken ct);
}

public sealed partial class UploadService
{
    private const int BufferSize = 256 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<AppSettings> _settings;
    private readonly ShareHistory _history;
    private readonly FileLog _log;
    private readonly TimeProvider _time;
    private readonly Func<string, long> _freeSpace;
    private readonly IReceivedFileProcessor? _processor;
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly SemaphoreSlim _finalizeLock = new(1, 1);

    public UploadService(
        Func<AppSettings> settings,
        ShareHistory history,
        FileLog log,
        TimeProvider time,
        Func<string, long>? freeSpace = null,
        IReceivedFileProcessor? processor = null)
    {
        _settings = settings;
        _history = history;
        _log = log;
        _time = time;
        _freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
        _processor = processor;
    }

    public event Action<TransferUpdate>? TransferChanged;

    private string PartsFolder => Path.Combine(_settings().ReceiveFolder, ".parts");

    public long GetOffset(Device device, string id)
    {
        ValidateId(id);
        var path = PartPath(device, id);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public async Task<UploadResult> ReceiveAsync(Device device, UploadRequest request, Stream body, long? contentLength, CancellationToken ct)
    {
        var name = FileNames.Sanitize(request.Name);
        if (request.Id is not null)
        {
            ValidateId(request.Id);
            if (request.Size is null or < 0)
                throw new ApiException(400, "bad_request", "size is required with id");
        }
        var resumable = request.Id is not null;
        var id = request.Id ?? Secrets.NewCode(16);
        var total = request.Size ?? (resumable ? null : contentLength);

        Directory.CreateDirectory(PartsFolder);
        var partPath = PartPath(device, id);
        if (!_active.TryAdd(partPath, 0))
            throw new ApiException(409, "upload_busy", "This upload is already running");

        try
        {
            var current = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (request.Offset != current)
                throw new ApiException(409, "offset_mismatch", "Offset does not match", current);
            if (total is { } t && t - current > _freeSpace(PartsFolder))
                throw new ApiException(507, "disk_full", "Not enough disk space");

            var received = await WriteBodyAsync(partPath, body, current, total, id, name, device, request, resumable, ct);
            if (resumable && received < total)
            {
                Report(id, name, device, received, total, TransferState.Running, request);
                return new UploadResult(false, received);
            }

            return await FinishAsync(device, request, id, name, partPath, received);
        }
        catch (OperationCanceledException)
        {
            if (!resumable) TryDelete(partPath);
            Report(id, name, device, 0, total, TransferState.Failed, request);
            _log.Info($"Upload interrupted: {name}");
            throw;
        }
        catch (IOException ex) when (ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027))
        {
            if (!resumable) TryDelete(partPath);
            Report(id, name, device, 0, total, TransferState.Failed, request);
            _log.Error($"Disk full while receiving {name}");
            throw new ApiException(507, "disk_full", "Not enough disk space");
        }
        catch (Exception ex) when (ex is IOException or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            // Telefon bağlantıyı kopardığında gövde okuması bu istisnalardan birini verir.
            if (!resumable) TryDelete(partPath);
            Report(id, name, device, 0, total, TransferState.Failed, request);
            _log.Error($"Upload failed: {name}", ex);
            throw;
        }
        finally
        {
            _active.TryRemove(partPath, out _);
        }
    }

    private async Task<long> WriteBodyAsync(
        string partPath, Stream body, long start, long? total, string id, string name,
        Device device, UploadRequest request, bool resumable, CancellationToken ct)
    {
        var received = start;
        var lastReport = _time.GetTimestamp();
        var buffer = new byte[BufferSize];
        await using var file = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

        Report(id, name, device, received, total, TransferState.Running, request);
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (resumable && received + read > total)
                throw new ApiException(400, "bad_request", "Body is larger than size");

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (_time.GetElapsedTime(lastReport) >= ProgressInterval)
            {
                lastReport = _time.GetTimestamp();
                Report(id, name, device, received, total, TransferState.Running, request);
            }
        }
        return received;
    }

    private async Task<UploadResult> FinishAsync(Device device, UploadRequest request, string id, string name, string partPath, long size)
    {
        var settings = _settings();
        if (settings.SkipDuplicates && _history.FindDuplicate(partPath, size) is { } existing)
        {
            TryDelete(partPath);
            _log.Info($"Duplicate skipped: {name} from {device.Name}");
            Report(id, existing.Name, device, size, size, TransferState.Duplicate, request, existing.Path);
            return new UploadResult(true, size, existing.Name, Duplicate: true);
        }

        var captured = ReadCaptureDate(partPath);
        var fileDate = captured
            ?? (request.ModifiedUnixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : _time.GetUtcNow());

        var folder = settings.ReceiveFolder;
        if (settings.OrganizeByDate)
            folder = Path.Combine(folder, fileDate.ToLocalTime().ToString("yyyy"), fileDate.ToLocalTime().ToString("MM"));
        Directory.CreateDirectory(folder);

        string finalPath;
        await _finalizeLock.WaitAsync();
        try
        {
            finalPath = FileNames.Unique(folder, name);
            File.Move(partPath, finalPath);
        }
        finally
        {
            _finalizeLock.Release();
        }
        try
        {
            File.SetLastWriteTimeUtc(finalPath, fileDate.UtcDateTime);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _log.Error($"File date could not be set: {name}", ex);
        }

        if (_processor is not null)
        {
            try
            {
                finalPath = await _processor.ProcessAsync(finalPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Error($"Post-processing failed, keeping original: {name}", ex);
            }
        }

        var finalInfo = new FileInfo(finalPath);
        _history.Add(new HistoryItem
        {
            Id = Secrets.NewCode(12),
            Path = finalPath,
            Name = finalInfo.Name,
            Size = finalInfo.Length,
            DeviceName = device.Name,
            ReceivedAt = _time.GetUtcNow(),
        });
        _log.Info($"Received: {finalInfo.Name} ({size} bytes) from {device.Name}");
        Report(id, finalInfo.Name, device, size, size, TransferState.Completed, request, finalPath);
        return new UploadResult(true, size, finalInfo.Name);
    }

    /// <summary>Terk edilmiş yarım yüklemeleri siler.</summary>
    public void CleanStaleParts(TimeSpan olderThan)
    {
        if (!Directory.Exists(PartsFolder)) return;
        var limit = _time.GetUtcNow().UtcDateTime - olderThan;
        foreach (var file in Directory.EnumerateFiles(PartsFolder, "*.part"))
        {
            if (!_active.ContainsKey(file) && File.GetLastWriteTimeUtc(file) < limit)
                TryDelete(file);
        }
    }

    private void Report(string id, string name, Device device, long received, long? total, TransferState state, UploadRequest request, string? path = null) =>
        TransferChanged?.Invoke(new TransferUpdate(id, name, device.Name, received, total, state, request.BatchIndex, request.BatchTotal, path));

    private string PartPath(Device device, string id) =>
        Path.Combine(PartsFolder, Secrets.Hash(device.Id + ":" + id)[..32] + ".part");

    private static void ValidateId(string id)
    {
        if (!IdPattern().IsMatch(id))
            throw new ApiException(400, "bad_request", "Invalid id");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    private DateTimeOffset? ReadCaptureDate(string path)
    {
        // Metadata isteğe bağlıdır: telefondan gelen bozuk bir etiket (ör. XMP) dosyayı reddettirmemeli.
        try
        {
            return FindCaptureDate(ImageMetadataReader.ReadMetadata(path));
        }
        catch (Exception)
        {
            // Bütün etiketler okunamadıysa JPEG'de yalnızca EXIF yeterlidir.
        }
        try
        {
            return FindCaptureDate(JpegMetadataReader.ReadMetadata(path, [new ExifReader()]));
        }
        catch (Exception)
        {
            // Çoğu dosya türünde EXIF yoktur; bu bir hata değil.
            return null;
        }
    }

    private DateTimeOffset? FindCaptureDate(IEnumerable<MetadataExtractor.Directory> directories)
    {
        var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (exif is null || !exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var taken))
            return null;

        // Kameradan gelen saçma tarihler (ör. 0001 veya gelecek) dosya tarihi olamaz.
        if (taken.Year < 1980 || taken > _time.GetUtcNow().UtcDateTime.AddDays(2))
            return null;
        return new DateTimeOffset(DateTime.SpecifyKind(taken, DateTimeKind.Local));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex IdPattern();
}

public static class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Telefondan gelen adı güvenli bir Windows dosya adına çevirir; yol bileşenlerini atar.</summary>
    public static string Sanitize(string? name)
    {
        var last = (name ?? "").Replace('\\', '/').Split('/').Last();
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(last.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (clean.Length == 0 || clean == ".parts") clean = "dosya";

        var stem = Path.GetFileNameWithoutExtension(clean);
        if (Reserved.Contains(stem)) clean = "_" + clean;

        if (clean.Length > 200)
        {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 20) ext = "";
            clean = clean[..(200 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return clean;
    }

    /// <summary>Çakışma varsa "foto (1).jpg" gibi numaralandırır.</summary>
    public static string Unique(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }
}
