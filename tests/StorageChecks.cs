using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VaultSync;

var root = Path.GetFullPath(
    Path.Combine(".runtime", "storage-checks", Guid.NewGuid().ToString("N"))
);
Directory.CreateDirectory(root);
var services = new ServiceCollection();
services.AddDbContext<VaultDb>(o =>
    o.UseSqlite("Data Source=" + Path.Combine(root, "metadata.db"))
);
using var provider = services.BuildServiceProvider();
using (var scope = provider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VaultDb>();
    await db.Database.EnsureCreatedAsync();
}
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(
        new Dictionary<string, string?> { { "VaultPath", Path.Combine(root, "objects") } }
    )
    .Build();
var scopes = provider.GetRequiredService<IServiceScopeFactory>();
var vault = new ObjectVault(config, scopes);
var bytes = System.Text.Encoding.UTF8.GetBytes("key-loss regression fixture");
var hash = ObjectVault.Hash(bytes);
await vault.Put(hash, bytes);
var roundtrip = await vault.Get(hash);
if (!bytes.SequenceEqual(roundtrip))
    throw new Exception("Initial encrypted roundtrip failed");
using (var scope = provider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VaultDb>();
    db.Chunks.Add(new() { Id = hash, Bytes = bytes.Length });
    await db.SaveChangesAsync();
}
var keyPath = Path.Combine(root, "objects", "master.key");
var original = await File.ReadAllBytesAsync(keyPath);
File.Delete(keyPath);
try
{
    new ObjectVault(config, scopes);
    throw new Exception("A new key was silently generated for existing data");
}
catch (InvalidOperationException e) when (e.Message.Contains("master key is missing"))
{
    if (File.Exists(keyPath))
        throw new Exception("Missing key was replaced");
    Console.WriteLine("PASS: existing data requires its original encryption key");
}
finally
{
    await File.WriteAllBytesAsync(keyPath, original);
}
var reopened = new ObjectVault(config, scopes);
var restored = await reopened.Get(hash);
if (!bytes.SequenceEqual(restored))
    throw new Exception("Restored key cannot read existing data");
Console.WriteLine("PASS: restoring the original key restores access");
