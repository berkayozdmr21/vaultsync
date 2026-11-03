using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using VaultSync;

var b = WebApplication.CreateBuilder(args);
var data = Path.GetFullPath(b.Configuration["DataPath"] ?? ".runtime/live");
Directory.CreateDirectory(data);
var owner =
    b.Configuration["OwnerKey"]
    ?? (
        b.Environment.IsDevelopment()
            ? "vaultsync-local-dev"
            : throw new InvalidOperationException("OwnerKey is required.")
    );
b.Services.AddDbContext<VaultDb>(o =>
{
    if (b.Configuration["DatabaseProvider"] == "Postgres")
        o.UseNpgsql(b.Configuration.GetConnectionString("Default"));
    else
        o.UseSqlite("Data Source=" + Path.Combine(data, "vault.db"));
});
b.Services.AddSingleton<ObjectVault>();
b.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
b.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy(
        "login",
        c =>
            RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new() { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }
            )
    );
});
var app = b.Build();
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<VaultDb>().Database.EnsureCreatedAsync();
}
var sessions = new ConcurrentDictionary<string, DateTime>();
var gate = new SemaphoreSlim(1, 1);
string Digest(string s) => ObjectVault.Hash(Encoding.UTF8.GetBytes(s));
async Task<Device?> Auth(HttpContext c, VaultDb db, bool admin = false)
{
    var token = c.Request.Headers["X-Vault-Key"].ToString();
    var cookie = c.Request.Cookies["vaultsync-session"];
    if (
        (
            token.Length > 0
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Digest(token)),
                Encoding.UTF8.GetBytes(Digest(owner))
            )
        )
        || (
            cookie != null
            && sessions.TryGetValue(Digest(cookie), out var until)
            && until > DateTime.UtcNow
        )
    )
        return null;
    if (admin)
        throw new ApiFault(401, "Yönetici girişi gerekli.");
    var hash = Digest(token);
    return await db.Devices.SingleOrDefaultAsync(d => !d.Revoked && d.TokenHash == hash)
        ?? throw new ApiFault(401, "Geçersiz cihaz anahtarı.");
}
app.Use(
    async (c, next) =>
    {
        try
        {
            c.Response.Headers["Cache-Control"] = "no-store";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (
                c.Request.Method != "GET"
                && c.Request.Headers.Origin.Count > 0
                && c.Request.Headers.Origin.ToString()
                    != (b.Configuration["PublicOrigin"] ?? "http://127.0.0.1:9250")
            )
                throw new ApiFault(403, "Kaynak adresi kabul edilmedi.");
            await next();
        }
        catch (Exception e)
        {
            if (c.Response.HasStarted)
                throw;
            var code = e switch
            {
                ApiFault f => f.Status,
                JsonException => 400,
                KeyNotFoundException => 404,
                FileNotFoundException => 422,
                DbUpdateConcurrencyException => 409,
                BadHttpRequestException x => x.StatusCode,
                _ => 500,
            };
            c.Response.StatusCode = code;
            if (code == 500)
                app.Logger.LogError(e, "Request failed");
            await c.Response.WriteAsJsonAsync(
                new { error = code == 500 ? "Sunucu hatası." : e.Message }
            );
        }
    }
);
app.UseRateLimiter();
app.MapGet("/health", () => new { status = "ok", schema = 1 });
app.MapPost(
        "/api/login",
        async (HttpContext c) =>
        {
            var input = await c.Request.ReadFromJsonAsync<Login>();
            if (input == null || Digest(input.Key ?? "") != Digest(owner))
                throw new ApiFault(401, "Anahtar doğru değil.");
            foreach (var s in sessions.Where(s => s.Value < DateTime.UtcNow).ToArray())
                sessions.TryRemove(s.Key, out _);
            if (sessions.Count >= 1000)
                throw new ApiFault(429, "Oturum sınırı.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[Digest(token)] = DateTime.UtcNow.AddHours(8);
            c.Response.Cookies.Append(
                "vaultsync-session",
                token,
                new()
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = c.Request.IsHttps,
                    MaxAge = TimeSpan.FromHours(8),
                }
            );
            return Results.Ok(new { role = "owner" });
        }
    )
    .RequireRateLimiting("login");
app.MapPost(
    "/api/logout",
    (HttpContext c) =>
    {
        var t = c.Request.Cookies["vaultsync-session"];
        if (t != null)
            sessions.TryRemove(Digest(t), out _);
        c.Response.Cookies.Delete("vaultsync-session");
        return Results.NoContent();
    }
);
app.MapGet(
    "/api/overview",
    async (HttpContext c, VaultDb db) =>
    {
        await Auth(c, db, true);
        var devices = await db
            .Devices.Select(d => new
            {
                d.Id,
                d.Name,
                d.Head,
                d.Revoked,
            })
            .ToListAsync();
        var logical = await db.Snapshots.SumAsync(s => (long?)s.Bytes) ?? 0;
        var physical = await db.Chunks.SumAsync(s => (long?)s.Bytes) ?? 0;
        return Results.Ok(
            new
            {
                devices,
                snapshots = await db.Snapshots.CountAsync(),
                logicalBytes = logical,
                physicalBytes = physical,
                chunks = await db.Chunks.CountAsync(),
                audit = await db.Audits.OrderByDescending(a => a.Id).Take(30).ToListAsync(),
            }
        );
    }
);
app.MapPost(
    "/api/devices",
    async (DeviceInput input, HttpContext c, VaultDb db) =>
    {
        await Auth(c, db, true);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80)
            throw new ApiFault(400, "Cihaz adı 1–80 karakter olmalı.");
        if (await db.Devices.CountAsync() >= 50)
            throw new ApiFault(409, "50 cihaz sınırı.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var d = new Device { Name = input.Name.Trim(), TokenHash = Digest(token) };
        db.Devices.Add(d);
        db.Audits.Add(new() { Action = "device.created", Detail = d.Name });
        await db.SaveChangesAsync();
        return Results.Ok(
            new
            {
                d.Id,
                d.Name,
                key = token,
            }
        );
    }
);
app.MapDelete(
    "/api/devices/{id}",
    async (string id, HttpContext c, VaultDb db) =>
    {
        await Auth(c, db, true);
        var d = await db.Devices.FindAsync(id) ?? throw new KeyNotFoundException();
        d.Revoked = true;
        db.Audits.Add(new() { Action = "device.revoked", Detail = d.Name });
        await db.SaveChangesAsync();
        return Results.NoContent();
    }
);
app.MapGet(
    "/api/device",
    async (HttpContext c, VaultDb db) =>
    {
        var d = await Auth(c, db) ?? throw new ApiFault(400, "Cihaz anahtarı kullanın.");
        return Results.Ok(
            new
            {
                d.Id,
                d.Name,
                d.Head,
            }
        );
    }
);
app.MapPost(
    "/api/chunks/missing",
    async (List<string> ids, HttpContext c, VaultDb db) =>
    {
        await Auth(c, db);
        if (ids.Count > 512)
            throw new ApiFault(400, "En fazla 512 parça.");
        foreach (var id in ids)
            ObjectVault.Validate(id);
        var found = await db.Chunks.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync();
        return ids.Except(found).ToArray();
    }
);
app.MapPut(
    "/api/chunks/{id}",
    async (string id, HttpContext c, VaultDb db, ObjectVault vault) =>
    {
        await Auth(c, db);
        ObjectVault.Validate(id);
        using var ms = new MemoryStream();
        await c.Request.Body.CopyToAsync(ms);
        if (ms.Length is < 1 or > 1048576)
            throw new ApiFault(400, "Parça boyutu 1 byte–1 MiB olmalı.");
        var bytes = ms.ToArray();
        if (ObjectVault.Hash(bytes) != id)
            throw new ApiFault(400, "Hash eşleşmiyor.");
        await gate.WaitAsync(c.RequestAborted);
        try
        {
            if (!await db.Chunks.AnyAsync(x => x.Id == id))
            {
                if (
                    (await db.Chunks.SumAsync(x => (long?)x.Bytes) ?? 0) + bytes.Length
                    > 1024L * 1024 * 1024
                )
                    throw new ApiFault(409, "1 GiB kasa kotası doldu.");
                await vault.Put(id, bytes);
                db.Chunks.Add(new() { Id = id, Bytes = bytes.Length });
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        }
        finally
        {
            gate.Release();
        }
    }
);
void ValidateManifest(Commit input)
{
    if (
        string.IsNullOrWhiteSpace(input.Key)
        || input.Key.Length > 100
        || input.Files == null
        || input.Files.Count > 2000
    )
        throw new ApiFault(400, "Manifest sınırı aşıldı.");
    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long total = 0;
    foreach (var f in input.Files)
    {
        if (
            f == null
            || f.Path == null
            || f.Path.Length is < 1 or > 240
            || f.Path.Contains('\\')
            || f.Path.Contains(':')
            || f.Path.StartsWith('/')
            || f.Path.Split('/').Any(p => p is "" or "." or ".." || p.Any(char.IsControl))
            || !paths.Add(f.Path)
            || f.Bytes < 0
            || f.Bytes > 64 * 1024 * 1024
            || f.Chunks == null
            || f.Chunks.Count > 64
        )
            throw new ApiFault(400, "Geçersiz dosya yolu veya boyutu.");
        ObjectVault.Validate(f.Hash);
        foreach (var id in f.Chunks)
            ObjectVault.Validate(id);
        total += f.Bytes;
    }
    if (total > 256L * 1024 * 1024)
        throw new ApiFault(400, "Snapshot en fazla 256 MiB.");
}
app.MapPost(
    "/api/snapshots",
    async (Commit input, HttpContext c, VaultDb db, ObjectVault vault) =>
    {
        var device = await Auth(c, db) ?? throw new ApiFault(400, "Cihaz anahtarı gerekli.");
        ValidateManifest(input);
        var manifest = JsonSerializer.Serialize(input.Files);
        var digest = Digest(manifest);
        await gate.WaitAsync(c.RequestAborted);
        try
        {
            var prior = await db.Snapshots.SingleOrDefaultAsync(s =>
                s.DeviceId == device.Id && s.Key == input.Key
            );
            if (prior != null)
            {
                if (prior.Digest != digest || prior.Parent != input.Parent)
                    throw new ApiFault(409, "İşlem anahtarı farklı içerikle kullanılmış.");
                return Results.Ok(prior);
            }
            await db.Entry(device).ReloadAsync();
            if (device.Head != input.Parent)
                throw new ApiFault(409, "Kaynak değişti. Güncel head ile yeniden tarayın.");
            foreach (var f in input.Files)
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                foreach (var id in f.Chunks)
                {
                    if (!await db.Chunks.AnyAsync(x => x.Id == id))
                        throw new ApiFault(409, "Eksik parça: " + id);
                    var bytes = await vault.Get(id);
                    length += bytes.Length;
                    sha.AppendData(bytes);
                }
                if (
                    length != f.Bytes
                    || Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != f.Hash
                )
                    throw new ApiFault(422, "Dosya bütünlüğü eşleşmiyor: " + f.Path);
            }
            var snapshot = new Snapshot
            {
                DeviceId = device.Id,
                Key = input.Key,
                Parent = input.Parent,
                Manifest = manifest,
                Digest = digest,
                Files = input.Files.Count,
                Bytes = input.Files.Sum(f => f.Bytes),
            };
            db.Snapshots.Add(snapshot);
            device.Head = snapshot.Id;
            db.Audits.Add(
                new()
                {
                    Action = "snapshot.committed",
                    Detail = $"{device.Name} · {snapshot.Files} dosya",
                }
            );
            await db.SaveChangesAsync();
            return Results.Ok(snapshot);
        }
        finally
        {
            gate.Release();
        }
    }
);
app.MapGet(
    "/api/snapshots",
    async (HttpContext c, VaultDb db) =>
    {
        var d = await Auth(c, db);
        return Results.Ok(
            await db
                .Snapshots.Where(s => d == null || s.DeviceId == d.Id)
                .OrderByDescending(s => s.CreatedTicks)
                .Take(200)
                .Select(s => new
                {
                    s.Id,
                    s.DeviceId,
                    s.Parent,
                    s.Files,
                    s.Bytes,
                    s.CreatedTicks,
                })
                .ToListAsync()
        );
    }
);
async Task<Snapshot> Visible(string id, HttpContext c, VaultDb db)
{
    var d = await Auth(c, db);
    return await db.Snapshots.SingleOrDefaultAsync(s =>
            s.Id == id && (d == null || s.DeviceId == d.Id)
        ) ?? throw new KeyNotFoundException("Sürüm bulunamadı.");
}
app.MapGet(
    "/api/snapshots/{id}",
    async (string id, HttpContext c, VaultDb db) =>
    {
        var s = await Visible(id, c, db);
        return Results.Ok(
            new
            {
                s.Id,
                s.Parent,
                s.DeviceId,
                files = JsonSerializer.Deserialize<List<FileEntry>>(s.Manifest),
            }
        );
    }
);
async Task<byte[]> FileBytes(FileEntry f, ObjectVault v)
{
    using var ms = new MemoryStream();
    foreach (var id in f.Chunks)
        await ms.WriteAsync(await v.Get(id));
    var bytes = ms.ToArray();
    if (bytes.Length != f.Bytes || ObjectVault.Hash(bytes) != f.Hash)
        throw new ApiFault(422, "Dosya bütünlüğü bozuk.");
    return bytes;
}
app.MapGet(
    "/api/snapshots/{id}/file",
    async (string id, string path, HttpContext c, VaultDb db, ObjectVault v) =>
    {
        var s = await Visible(id, c, db);
        var f =
            JsonSerializer
                .Deserialize<List<FileEntry>>(s.Manifest)!
                .SingleOrDefault(f => f.Path == path)
            ?? throw new KeyNotFoundException();
        return Results.File(
            await FileBytes(f, v),
            "application/octet-stream",
            Path.GetFileName(f.Path)
        );
    }
);
app.MapGet(
    "/api/snapshots/{id}/archive",
    async (string id, HttpContext c, VaultDb db, ObjectVault v) =>
    {
        var s = await Visible(id, c, db);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var f in JsonSerializer.Deserialize<List<FileEntry>>(s.Manifest)!)
            {
                var entry = zip.CreateEntry(f.Path, CompressionLevel.Fastest);
                await using var stream = entry.Open();
                await stream.WriteAsync(await FileBytes(f, v));
            }
        }
        return Results.File(ms.ToArray(), "application/zip", "vaultsync-" + id[..8] + ".zip");
    }
);
app.MapGet(
    "/api/retention/preview",
    async (int keep, HttpContext c, VaultDb db) =>
    {
        await Auth(c, db, true);
        if (keep is < 1 or > 100)
            throw new ApiFault(400, "1–100 sürüm seçin.");
        var all = await db.Snapshots.OrderByDescending(x => x.CreatedTicks).ToListAsync();
        var candidates = all.GroupBy(x => x.DeviceId)
            .SelectMany(g => g.Skip(keep))
            .Select(s => new
            {
                s.Id,
                s.DeviceId,
                s.Bytes,
            })
            .ToList();
        return Results.Ok(
            new
            {
                keep,
                candidates,
                mode = "preview-only",
                message = "Veriler silinmedi. Paylaşılan parçalar nedeniyle dosya boyutu fiziksel kazanım değildir.",
            }
        );
    }
);
app.MapGet(
    "/api/snapshots/{id}/diff",
    async (string id, HttpContext c, VaultDb db) =>
    {
        var current = await Visible(id, c, db);
        var previous = current.Parent == null ? null : await db.Snapshots.FindAsync(current.Parent);
        var files = JsonSerializer.Deserialize<List<FileEntry>>(current.Manifest)!;
        var old =
            previous == null
                ? new List<FileEntry>()
                : JsonSerializer.Deserialize<List<FileEntry>>(previous.Manifest)!;
        var before = old.ToDictionary(f => f.Path);
        var after = files.ToDictionary(f => f.Path);
        return Results.Ok(
            new
            {
                added = files.Where(f => !before.ContainsKey(f.Path)).Select(f => f.Path),
                changed = files
                    .Where(f => before.TryGetValue(f.Path, out var prior) && prior.Hash != f.Hash)
                    .Select(f => f.Path),
                removed = old.Where(f => !after.ContainsKey(f.Path)).Select(f => f.Path),
            }
        );
    }
);
app.MapPost(
    "/api/snapshots/{id}/verify",
    async (string id, HttpContext c, VaultDb db, ObjectVault v) =>
    {
        var snapshot = await Visible(id, c, db);
        foreach (var f in JsonSerializer.Deserialize<List<FileEntry>>(snapshot.Manifest)!)
            await FileBytes(f, v);
        db.Audits.Add(new() { Action = "snapshot.verified", Detail = id[..8] });
        await db.SaveChangesAsync();
        return Results.Ok(new { verified = true, files = snapshot.Files });
    }
);
app.Run();

record Login(string Key);

record DeviceInput(string Name);
