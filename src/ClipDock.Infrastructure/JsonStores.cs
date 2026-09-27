using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipDock.Core.Models;
using ClipDock.Core.Services;

namespace ClipDock.Infrastructure;

public sealed class SettingsStore(AppPaths paths) : ISettingsStore
{
    private readonly string _path = Path.Combine(paths.Data, "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings settings;
        if (!File.Exists(_path))
        {
            settings = new AppSettings();
        }
        else
        {
            await using var stream = File.OpenRead(_path);
            settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions.Default, cancellationToken).ConfigureAwait(false)
                ?? new AppSettings();
        }

        settings.Normalize();
        if (string.IsNullOrWhiteSpace(settings.BridgeToken))
        {
            settings.BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await AtomicJson.WriteAsync(_path, settings, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class HistoryStore(AppPaths paths) : IHistoryStore
{
    private readonly string _path = Path.Combine(paths.Data, "history.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<HistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = entries.Where(item => item.Id != entry.Id).Prepend(entry).Take(500).ToArray();
            await AtomicJson.WriteAsync(_path, updated, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await ReadAsync(cancellationToken).ConfigureAwait(false);
            await AtomicJson.WriteAsync(_path, entries.Where(item => item.Id != id).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<HistoryEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<HistoryEntry>>(stream, JsonOptions.Default, cancellationToken).ConfigureAwait(false) ?? [];
    }
}

public sealed class QueueStore(AppPaths paths) : IQueueStore
{
    private readonly string _path = Path.Combine(paths.Data, "queue.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<DownloadJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<DownloadJob>>(stream, JsonOptions.Default, cancellationToken).ConfigureAwait(false) ?? [];
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IReadOnlyCollection<DownloadJob> jobs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicJson.WriteAsync(_path, jobs, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

internal static class AtomicJson
{
    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions.Default, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, true);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static JsonOptions()
    {
        Default.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }
}
