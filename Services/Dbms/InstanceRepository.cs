using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DbServeApp.Services.Dbms;

public sealed class StorageOptions
{
    /// <summary>Empty = %LocalAppData%\DbServeApp\instances.json</summary>
    public string InstancesFile { get; set; } = "";
}

public interface IInstanceRepository
{
    Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default);
    Task<DbInstance?> GetAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(DbInstance instance, CancellationToken ct = default);
    Task UpdateAsync(DbInstance instance, CancellationToken ct = default);
    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);
}

public sealed class JsonInstanceRepository : IInstanceRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly ILogger<JsonInstanceRepository> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<DbInstance>? _cache;

    public JsonInstanceRepository(IOptions<StorageOptions> options, ILogger<JsonInstanceRepository> log)
    {
        _log = log;
        var configured = options.Value.InstancesFile;
        _path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbServeApp", "instances.json")
            : Path.GetFullPath(configured);
    }

    public async Task<IReadOnlyList<DbInstance>> GetAllAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return (await LoadAsync(ct)).ToList(); }
        finally { _lock.Release(); }
    }

    public async Task<DbInstance?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return (await LoadAsync(ct)).FirstOrDefault(i => i.Id == id); }
        finally { _lock.Release(); }
    }

    public async Task AddAsync(DbInstance instance, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = (await LoadAsync(ct)).ToList();
            list.Add(instance);
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateAsync(DbInstance instance, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = (await LoadAsync(ct)).ToList();
            var idx = list.FindIndex(i => i.Id == instance.Id);
            if (idx < 0) return;
            list[idx] = instance;
            await SaveAsync(list, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var list = (await LoadAsync(ct)).ToList();
            if (list.RemoveAll(i => i.Id == id) == 0) return false;
            await SaveAsync(list, ct);
            return true;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<DbInstance>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path)) return _cache = [];
        try
        {
            await using var fs = File.OpenRead(_path);
            return _cache = await JsonSerializer.DeserializeAsync<List<DbInstance>>(fs, Json, ct) ?? [];
        }
        catch (JsonException ex)
        {
            // Keep the unreadable file for manual recovery instead of overwriting it.
            var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_path, backup);
            _log.LogError(ex, "Instance file was unreadable and was moved to {Backup}", backup);
            return _cache = [];
        }
    }

    private async Task SaveAsync(List<DbInstance> list, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, list, Json, ct);
        File.Move(tmp, _path, overwrite: true);
        // Only replace the cache once the file is written, so a failed save leaves memory and disk consistent.
        _cache = list;
    }
}
