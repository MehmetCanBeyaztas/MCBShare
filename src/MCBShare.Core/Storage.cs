using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCBShare.Core;

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public string ReceiveFolder { get; set; } = "";
    public List<string> TrustedNetworks { get; set; } = [];
    public string PairingToken { get; set; } = "";
    public bool ShowTransferNotifications { get; set; } = true;
    public bool ConvertHeicToJpg { get; set; }
    public bool OrganizeByDate { get; set; }
    public bool SkipDuplicates { get; set; } = true;
    public int RetentionDays { get; set; } = 30;
    public string Language { get; set; } = "";
}

/// <summary>Bir JSON dosyasını atomik olarak okur ve yazar.</summary>
public sealed class JsonStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();

    public JsonStore(string path) => _path = path;

    public T Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) ?? new T();
            }
            catch (JsonException)
            {
                // Bozuk dosya uygulamayı açılışta düşürmemeli; sonraki yazmada incelenebilsin diye kenara alınır.
                File.Move(_path, _path + ".bad", overwrite: true);
            }
            return new T();
        }
    }

    public void Save(T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, _path, overwrite: true);
        }
    }
}

public static class Secrets
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string NewCode(int length) => RandomNumberGenerator.GetString(Alphabet, length);

    public static string NewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool EqualsConstantTime(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Günlük dönen metin logu. Dosya içerikleri ve anahtarlar buraya yazılmaz.</summary>
public sealed class FileLog
{
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly int _keepDays;
    private readonly Lock _lock = new();

    public FileLog(string folder, TimeProvider? time = null, int keepDays = 7)
    {
        _folder = folder;
        _time = time ?? TimeProvider.System;
        _keepDays = keepDays;
        Directory.CreateDirectory(folder);
        DeleteOld();
    }

    public string Folder => _folder;

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private void Write(string level, string message)
    {
        var now = _time.GetLocalNow();
        lock (_lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_folder, $"mcbshare-{now:yyyy-MM-dd}.log"),
                    $"{now:HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Log yazılamadı diye aktarım bozulmamalı.
            }
        }
    }

    private void DeleteOld()
    {
        var limit = _time.GetUtcNow().UtcDateTime.AddDays(-_keepDays);
        foreach (var file in Directory.EnumerateFiles(_folder, "mcbshare-*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < limit)
                File.Delete(file);
        }
    }
}
