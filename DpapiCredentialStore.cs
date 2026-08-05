using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace SSHRtm;

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore
{
    private readonly string _path;
    private Dictionary<string, string> _blobs;

    public DpapiCredentialStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "creds.dat");
        _blobs = Load();
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_blobs));
    }

    public string? TryGet(string name)
    {
        if (!_blobs.TryGetValue(name, out var b64)) return null;
        try
        {
            var enc = Convert.FromBase64String(b64);
            var plain = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    public void Set(string name, string password)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        var enc = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        _blobs[name] = Convert.ToBase64String(enc);
        Save();
    }

    public void Remove(string name)
    {
        if (_blobs.Remove(name)) Save();
    }
}
