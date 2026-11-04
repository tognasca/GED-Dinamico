using System.Security.Cryptography;

namespace Ged.Api.Services;

public interface IStorageProvider
{
    Task<(string Key, string Hash, long Size)> PutAsync(Guid tenantId, Stream source, CancellationToken ct);
    Task<Stream> OpenReadAsync(Guid tenantId, string key, CancellationToken ct);
}

public sealed class LocalStorageProvider(IConfiguration cfg) : IStorageProvider
{
    private readonly string _root = Path.GetFullPath(cfg["Storage:Root"] ?? "./data/files");

    public async Task<(string Key, string Hash, long Size)> PutAsync(Guid tenantId, Stream source, CancellationToken ct)
    {
        var key = $"{Guid.NewGuid():N}.blob";
        var dir = Path.Combine(_root, tenantId.ToString("N"));
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, key);
        try
        {
            await using (var target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await source.CopyToAsync(target, ct);
            await using var read = File.OpenRead(full);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(read, ct));
            return (key, hash, read.Length);
        }
        catch { File.Delete(full); throw; }
    }

    public Task<Stream> OpenReadAsync(Guid tenantId, string key, CancellationToken ct)
    {
        // Key must be a server-generated UUID with the fixed .blob extension.
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-f0-9]{32}\\.blob$"))
            throw new UnauthorizedAccessException("Invalid storage key");
        var full = Path.Combine(_root, tenantId.ToString("N"), key);
        return Task.FromResult<Stream>(File.OpenRead(full));
    }
}
