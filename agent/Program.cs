using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

var key =
    Environment.GetEnvironmentVariable("VAULTSYNC_DEVICE_KEY")
    ?? throw new Exception("VAULTSYNC_DEVICE_KEY ortam değişkeni gerekli.");
using var http = new HttpClient
{
    BaseAddress = new Uri(
        Environment.GetEnvironmentVariable("VAULTSYNC_API") ?? "http://127.0.0.1:9251"
    ),
    Timeout = TimeSpan.FromMinutes(3),
};
if (
    http.BaseAddress!.Scheme != "https"
    && !(http.BaseAddress.Scheme == "http" && http.BaseAddress.IsLoopback)
)
    throw new Exception("Uzak API bağlantısında HTTPS gerekli.");
http.DefaultRequestHeaders.Add("X-Vault-Key", key);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
async Task<T> Get<T>(string path)
{
    using var r = await http.GetAsync(path);
    await Check(r);
    return (await r.Content.ReadFromJsonAsync<T>())!;
}
async Task Check(HttpResponseMessage r)
{
    if (!r.IsSuccessStatusCode)
        throw new HttpRequestException(
            $"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}",
            null,
            r.StatusCode
        );
}
if (args.Length >= 3 && args[0] == "restore")
{
    var snapshot = await Get<RestoreManifest>("/api/snapshots/" + Uri.EscapeDataString(args[1]));
    var target = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(target);
    foreach (var f in snapshot.Files)
    {
        var path = Path.GetFullPath(
            Path.Combine(target, f.Path.Replace('/', Path.DirectorySeparatorChar))
        );
        if (
            !path.StartsWith(
                target + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new Exception("Unsafe restore path");
        // Reject existing symlink/reparse ancestors, including the destination root.
        for (
            var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
            dir != null;
            dir = dir.Parent
        )
        {
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new Exception("Restore path contains a link.");
            if (dir.FullName == target)
                break;
        }
        using var response = await http.GetAsync(
            $"/api/snapshots/{snapshot.Id}/file?path={Uri.EscapeDataString(f.Path)}"
        );
        await Check(response);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (Hash(bytes) != f.Hash || bytes.LongLength != f.Bytes)
            throw new Exception("Restore integrity check failed");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new Exception("Restore target is a link");
            if (Hash(await File.ReadAllBytesAsync(path)) == f.Hash)
                continue;
            path += ".conflict-" + Guid.NewGuid().ToString("N")[..8];
        }
        await using var output = new FileStream(path, FileMode.CreateNew);
        await output.WriteAsync(bytes);
        Console.WriteLine("RESTORED " + f.Path);
    }
    return;
}
if (args.Length == 0)
    throw new Exception("Kullanım: VaultSync.Agent <klasör> [--once] | restore <snapshot> <hedef>");
var root = Path.GetFullPath(args[0]);
if (!Directory.Exists(root))
    throw new Exception("Kaynak klasör bulunamadı.");
if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
    throw new Exception("Kaynak sembolik bağlantı olamaz.");
var stateRoot = Path.GetFullPath(
    Environment.GetEnvironmentVariable("VAULTSYNC_STATE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VaultSync",
            Hash(System.Text.Encoding.UTF8.GetBytes(root + key + http.BaseAddress))[..16]
        )
);
if (
    stateRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || stateRoot == root
)
    throw new Exception("State klasörü kaynak klasörün dışında olmalı.");
Directory.CreateDirectory(stateRoot);
var pending = Path.Combine(stateRoot, "pending.json");
var baseline = Path.Combine(stateRoot, "last-digest");
async Task Upload(UploadManifest manifest)
{
    var ids = manifest.Files.SelectMany(f => f.Chunks).Distinct().ToArray();
    foreach (var batch in ids.Chunk(512))
    {
        using var missingResponse = await http.PostAsJsonAsync("/api/chunks/missing", batch);
        await Check(missingResponse);
        var missing = (await missingResponse.Content.ReadFromJsonAsync<string[]>())!;
        foreach (var id in missing)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(stateRoot, id));
            if (Hash(bytes) != id)
                throw new Exception("Local outbox integrity error");
            using var body = new ByteArrayContent(bytes);
            using var response = await http.PutAsync("/api/chunks/" + id, body);
            await Check(response);
        }
    }
    using var commit = await http.PostAsJsonAsync("/api/snapshots", manifest);
    await Check(commit);
    var saved = await commit.Content.ReadFromJsonAsync<JsonElement>();
    await File.WriteAllTextAsync(
        Path.Combine(stateRoot, "device.json"),
        JsonSerializer.Serialize(
            new Device(
                saved.GetProperty("deviceId").GetString()!,
                "",
                saved.GetProperty("id").GetString()
            ),
            json
        )
    );
    Console.WriteLine(
        $"COMMITTED {manifest.Files.Count} files / {manifest.Files.Sum(f => f.Bytes)} bytes"
    );
    await File.WriteAllTextAsync(
        baseline,
        Hash(JsonSerializer.SerializeToUtf8Bytes(manifest.Files, json))
    );
    File.Delete(pending);
    foreach (var id in ids)
    {
        var path = Path.Combine(stateRoot, id);
        if (File.Exists(path))
            File.Delete(path);
    }
}
async Task Scan()
{
    if (File.Exists(pending))
    {
        var old = JsonSerializer.Deserialize<UploadManifest>(
            await File.ReadAllTextAsync(pending),
            json
        )!;
        try
        {
            await Upload(old);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            File.Move(pending, pending + ".conflict-" + DateTime.UtcNow.Ticks);
            Console.WriteLine(
                "CONFLICT: pending manifest retained; scanning current source against latest head."
            );
        }
        return;
    }
    Device device;
    var cachedDevice = Path.Combine(stateRoot, "device.json");
    try
    {
        device = await Get<Device>("/api/device");
        await File.WriteAllTextAsync(cachedDevice, JsonSerializer.Serialize(device, json));
    }
    catch (HttpRequestException e) when (e.StatusCode == null && File.Exists(cachedDevice))
    {
        device = JsonSerializer.Deserialize<Device>(
            await File.ReadAllTextAsync(cachedDevice),
            json
        )!;
    }
    var files = new List<Entry>();
    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };
    long total = 0;
    foreach (var path in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc;
        if (length > 64 * 1024 * 1024)
            throw new Exception("File exceeds 64 MiB: " + path);
        total += length;
        if (total > 256L * 1024 * 1024 || files.Count >= 2000)
            throw new Exception("Snapshot limit exceeded");
        var chunks = new List<string>();
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        var buffer = new byte[1048576];
        int count;
        while ((count = await input.ReadAsync(buffer)) > 0)
        {
            var bytes = buffer[..count];
            whole.AppendData(bytes);
            var id = Hash(bytes);
            chunks.Add(id);
            var cached = Path.Combine(stateRoot, id);
            if (!File.Exists(cached))
                await File.WriteAllBytesAsync(cached, bytes);
        }
        info.Refresh();
        if (info.Length != length || info.LastWriteTimeUtc != modified)
            throw new IOException("File changed during scan; will retry.");
        files.Add(
            new(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                length,
                Convert.ToHexString(whole.GetHashAndReset()).ToLowerInvariant(),
                chunks
            )
        );
    }
    var digest = Hash(JsonSerializer.SerializeToUtf8Bytes(files, json));
    if (File.Exists(baseline) && await File.ReadAllTextAsync(baseline) == digest)
    {
        Console.WriteLine("UNCHANGED");
        return;
    }
    var manifest = new UploadManifest(Guid.NewGuid().ToString("N"), device.Head, files);
    var temp = pending + ".tmp";
    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, json));
    File.Move(temp, pending, true);
    await Upload(manifest);
}
if (args.Contains("--once"))
{
    await Scan();
    return;
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};
var dirty = 1;
using var watcher = new FileSystemWatcher(root)
{
    IncludeSubdirectories = true,
    EnableRaisingEvents = true,
};
FileSystemEventHandler changed = (_, _) => Interlocked.Exchange(ref dirty, 1);
watcher.Changed += changed;
watcher.Created += changed;
watcher.Deleted += changed;
watcher.Renamed += (_, _) => Interlocked.Exchange(ref dirty, 1);
watcher.Error += (_, _) => Interlocked.Exchange(ref dirty, 1);
Console.WriteLine("WATCHING " + root);
var last = DateTime.MinValue;
while (!shutdown.IsCancellationRequested)
{
    if (Interlocked.Exchange(ref dirty, 0) == 1 || DateTime.UtcNow - last > TimeSpan.FromMinutes(1))
    {
        try
        {
            await Scan();
            last = DateTime.UtcNow;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            Interlocked.Exchange(ref dirty, 1);
        }
    }
    try
    {
        await Task.Delay(5000, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}
static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

record Entry(string Path, long Bytes, string Hash, List<string> Chunks);

record UploadManifest(string Key, string? Parent, List<Entry> Files);

record Device(string Id, string Name, string? Head);

record RestoreManifest(string Id, List<Entry> Files);
