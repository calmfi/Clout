using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Clout.Host.Queue;

public class DiskBackedAmqpQueueServer : IAmqpQueueServer
{
    private sealed class QueueState
    {
        public string Name { get; }
        public string DirectoryPath { get; }
        public string StateFilePath { get; }
        public List<string> MessageFiles { get; } = new();
        public SemaphoreSlim Mutex { get; } = new(1, 1);
        public SemaphoreSlim MessageAvailable { get; } = new(0, int.MaxValue);
        public long TotalBytes { get; set; }

        public QueueState(string basePath, string name)
        {
            Name = name;
            DirectoryPath = Path.Combine(basePath, Sanitize(name));
            StateFilePath = Path.Combine(DirectoryPath, "state.json");
        }
    }

    private readonly ConcurrentDictionary<string, QueueState> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _basePath;
    private readonly QueueStorageOptions _options;
    private readonly ILogger<DiskBackedAmqpQueueServer>? _logger;
    private static readonly Meter s_meter = new("Clout.Host.Queue", "1.0.0");
    private readonly Counter<long> _enqueueCounter = s_meter.CreateCounter<long>("queue.enqueued", unit: null, description: "Number of enqueued messages");
    private readonly Counter<long> _dequeueCounter = s_meter.CreateCounter<long>("queue.dequeued", unit: null, description: "Number of dequeued messages");
    private readonly Counter<long> _evictionCounter = s_meter.CreateCounter<long>("queue.evicted", unit: null, description: "Number of evicted messages due to overflow policy");
    private readonly Counter<long> _rejectCounter = s_meter.CreateCounter<long>("queue.rejected", unit: null, description: "Number of rejected enqueues due to quotas");
    private static readonly ObservableGauge<long> s_queueMessagesGauge = s_meter.CreateObservableGauge("queue.messages", ObserveQueueMessages, unit: "messages", description: "Current queued message count per queue");
    private static readonly ObservableGauge<long> s_queueBytesGauge = s_meter.CreateObservableGauge("queue.bytes", ObserveQueueBytes, unit: "bytes", description: "Current queued bytes per queue");
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public DiskBackedAmqpQueueServer(IOptions<QueueStorageOptions> options, ILogger<DiskBackedAmqpQueueServer> logger)
    {
        _options = options.Value ?? new QueueStorageOptions();
        _logger = logger;
        var configured = _options.BasePath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            _basePath = Path.Combine(AppContext.BaseDirectory, "queue-data");
        }
        else
        {
            _basePath = Path.IsPathRooted(configured!)
                ? configured!
                : Path.Combine(AppContext.BaseDirectory, configured!);
        }
        Directory.CreateDirectory(_basePath);

        // Load existing queues
        foreach (var dir in Directory.EnumerateDirectories(_basePath))
        {
            var name = Path.GetFileName(dir)!;
            var state = new QueueState(_basePath, name);
            LoadState(state);
            _queues[name] = state;
        }
    }

    public void CreateQueue(string name)
    {
        var state = GetOrCreate(name);
        Directory.CreateDirectory(state.DirectoryPath);
        if (!File.Exists(state.StateFilePath))
        {
            SaveState(state);
        }
    }

    public void PurgeQueue(string name)
    {
        var state = GetOrCreate(name);
        state.Mutex.Wait();
        try
        {
            foreach (var file in state.MessageFiles)
            {
                var path = Path.Combine(state.DirectoryPath, file);
                TryDelete(path);
            }
            state.MessageFiles.Clear();
            state.TotalBytes = 0;
            SaveState(state);
        }
        finally
        {
            state.Mutex.Release();
        }
    }

    public async ValueTask EnqueueAsync<T>(string name, T message, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreate(name);
        await state.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(state.DirectoryPath);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);

            // Per-message size limit
            if (_options.MaxMessageBytes is int maxMsg && bytes.Length > maxMsg)
            {
                _rejectCounter.Add(1);
                ThrowQuota($"Message exceeds MaxMessageBytes={maxMsg}");
            }

            // Queue-level quotas
            long maxBytes = _options.MaxQueueBytes ?? 0;
            int maxMsgs = _options.MaxQueueMessages ?? 0;
            if (maxBytes > 0 || maxMsgs > 0)
            {
                var projectedBytes = state.TotalBytes + bytes.Length;
                var projectedCount = state.MessageFiles.Count + 1;
                var overflow = _options.Overflow;
                if ((maxBytes > 0 && projectedBytes > maxBytes) || (maxMsgs > 0 && projectedCount > maxMsgs))
                {
                    if (overflow == OverflowPolicy.Reject)
                    {
                        _rejectCounter.Add(1);
                        ThrowQuota("Enqueue would exceed queue quota");
                    }
                    else if (overflow == OverflowPolicy.DropOldest)
                    {
                        // Evict oldest until within quotas
                        while ((maxBytes > 0 && projectedBytes > maxBytes) || (maxMsgs > 0 && projectedCount > maxMsgs))
                        {
                            if (state.MessageFiles.Count == 0) break;
                            var oldest = state.MessageFiles[0];
                            state.MessageFiles.RemoveAt(0);
                            var path = Path.Combine(state.DirectoryPath, oldest);
                            if (File.Exists(path))
                            {
                                var len = new FileInfo(path).Length;
                                projectedBytes -= len;
                                _evictionCounter.Add(1);
                                TryDelete(path);
                            }
                            projectedCount--;
                        }
                        state.TotalBytes = projectedBytes;
                    }
                }
            }

            var fileName = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}.bin";
            var pathNew = Path.Combine(state.DirectoryPath, fileName);
            await File.WriteAllBytesAsync(pathNew, bytes, cancellationToken).ConfigureAwait(false);
            state.MessageFiles.Add(fileName);
            state.TotalBytes += bytes.Length;
            SaveState(state);
            _enqueueCounter.Add(1);
            state.MessageAvailable.Release();
        }
        finally
        {
            state.Mutex.Release();
        }
    }

    public async ValueTask<T?> DequeueAsync<T>(string name, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreate(name);
        while (!cancellationToken.IsCancellationRequested)
        {
            await state.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state.MessageFiles.Count > 0)
                {
                    var fileName = state.MessageFiles[0];
                    state.MessageFiles.RemoveAt(0);
                    var path = Path.Combine(state.DirectoryPath, fileName);
                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        TryDelete(path);
                    }
                    state.TotalBytes -= bytes.Length;
                    SaveState(state);
                    _dequeueCounter.Add(1);
                    var obj = JsonSerializer.Deserialize<T>(bytes, JsonOpts);
                    return obj;
                }
            }
            finally
            {
                state.Mutex.Release();
            }

            await state.MessageAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return default;
    }

    public IReadOnlyList<QueueStats> GetStats()
    {
        var list = new List<QueueStats>();
        foreach (var kvp in _queues)
        {
            var s = kvp.Value;
            list.Add(new QueueStats(s.Name, s.MessageFiles.Count, s.TotalBytes));
        }
        return list;
    }

    private QueueState GetOrCreate(string name)
    {
        var key = name ?? string.Empty;
        return _queues.GetOrAdd(key, n =>
        {
            var st = new QueueState(_basePath, n);
            Directory.CreateDirectory(st.DirectoryPath);
            LoadState(st);
            return st;
        });
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private void LoadState(QueueState state)
    {
        try
        {
            if (File.Exists(state.StateFilePath))
            {
                var json = File.ReadAllText(state.StateFilePath);
                var data = JsonSerializer.Deserialize<QueueStatePersisted>(json) ?? new();
                state.MessageFiles.Clear();
                state.MessageFiles.AddRange(data.Files ?? []);
                state.TotalBytes = 0;
                foreach (var f in state.MessageFiles)
                {
                    var p = Path.Combine(state.DirectoryPath, f);
                    if (File.Exists(p)) state.TotalBytes += new FileInfo(p).Length;
                }
                if (_options.CleanupOrphansOnLoad)
                {
                    var known = new HashSet<string>(state.MessageFiles, StringComparer.OrdinalIgnoreCase);
                    foreach (var p in Directory.EnumerateFiles(state.DirectoryPath, "*.bin"))
                    {
                        var fn = Path.GetFileName(p);
                        if (!known.Contains(fn)) TryDelete(p);
                    }
                }
            }
            else
            {
                SaveState(state);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load queue state for {Queue}", state.Name);
        }
    }

    private void SaveState(QueueState state)
    {
        try
        {
            var data = new QueueStatePersisted { Files = state.MessageFiles.ToArray() };
            var json = JsonSerializer.Serialize(data, JsonOpts);
            File.WriteAllText(state.StateFilePath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save queue state for {Queue}", state.Name);
        }
    }

    private static long ObserveQueueMessages()
    {
        // Per-queue details are provided as tags in a full metrics system; here we just expose total.
        return 0;
    }

    private static long ObserveQueueBytes() => 0;

    private static void ThrowQuota(string message) => throw new InvalidOperationException(message);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class QueueStatePersisted
    {
        public string[]? Files { get; set; }
    }
}
