using System.Security.Cryptography;
using System.Text;
using Minio;
using Minio.DataModel.Args;

namespace VaultSync;

public class ObjectVault
{
    readonly string root;
    readonly byte[] key;
    readonly IMinioClient? s3;
    readonly string bucket;

    public ObjectVault(IConfiguration c)
    {
        root = Path.GetFullPath(c["VaultPath"] ?? ".runtime/live/vault");
        Directory.CreateDirectory(root);
        var keyFile = Path.Combine(root, "master.key");
        if (!File.Exists(keyFile))
        {
            using var f = new FileStream(keyFile, FileMode.CreateNew);
            f.Write(RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        key = File.ReadAllBytes(keyFile);
        if (key.Length != 32)
            throw new InvalidOperationException("Invalid vault master key");
        bucket = c["S3:Bucket"] ?? "vaultsync";
        if (c["S3:Endpoint"] is string endpoint && endpoint.Length > 0)
            s3 = new MinioClient()
                .WithEndpoint(endpoint)
                .WithCredentials(c["S3:AccessKey"], c["S3:SecretKey"])
                .WithSSL(c.GetValue<bool>("S3:Ssl"))
                .Build();
    }

    public static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void Validate(string id)
    {
        if (
            string.IsNullOrEmpty(id)
            || id.Length != 64
            || id.Any(c => !"0123456789abcdef".Contains(c))
        )
            throw new ApiFault(400, "Geçersiz parça kimliği.");
    }

    string PathFor(string id)
    {
        Validate(id);
        return Path.Combine(root, "objects", id[..2], id);
    }

    public async Task Put(string id, byte[] bytes)
    {
        Validate(id);
        if (Hash(bytes) != id)
            throw new ApiFault(400, "Parça hash değeri eşleşmiyor.");
        var envelope = new byte[28 + bytes.Length];
        RandomNumberGenerator.Fill(envelope.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(
            envelope.AsSpan(0, 12),
            bytes,
            envelope.AsSpan(28),
            envelope.AsSpan(12, 16),
            Encoding.ASCII.GetBytes(id)
        );
        if (s3 != null)
        {
            using var stream = new MemoryStream(envelope);
            await s3.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(id)
                    .WithStreamData(stream)
                    .WithObjectSize(envelope.Length)
                    .WithContentType("application/octet-stream")
            );
        }
        else
        {
            var path = PathFor(id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, envelope);
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }

    public async Task<byte[]> Get(string id)
    {
        Validate(id);
        byte[] envelope;
        if (s3 != null)
        {
            using var ms = new MemoryStream();
            await s3.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(id)
                    .WithCallbackStream(s => s.CopyTo(ms))
            );
            envelope = ms.ToArray();
        }
        else
            envelope = await File.ReadAllBytesAsync(PathFor(id));
        if (envelope.Length < 28)
            throw new ApiFault(422, "Bozuk parça.");
        var bytes = new byte[envelope.Length - 28];
        using var aes = new AesGcm(key, 16);
        try
        {
            aes.Decrypt(
                envelope.AsSpan(0, 12),
                envelope.AsSpan(28),
                envelope.AsSpan(12, 16),
                bytes,
                Encoding.ASCII.GetBytes(id)
            );
        }
        catch (CryptographicException)
        {
            throw new ApiFault(422, "Parça bütünlük kontrolü başarısız.");
        }
        if (Hash(bytes) != id)
            throw new ApiFault(422, "Parça bütünlüğü doğrulanamadı.");
        return bytes;
    }
}
