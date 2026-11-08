using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultividStreamer.App.Services.Api;

public sealed class ApiSettings
{
    public int Port { get; init; } = 47831;

    public required string Token { get; init; }

    // Stable identity of THIS streamer install, minted once and persisted. The
    // headset binds a trusted-device token to this id and re-checks it (via
    // /server/info) before auto-connecting, so it never ships its token to a
    // different machine that merely advertises the same name/address on the LAN.
    public string ServerId { get; init; } = string.Empty;

    public string BaseUrl => $"http://127.0.0.1:{Port}";
}

public sealed class ApiSettingsStore
{
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true
    };

    public string StorePath { get; }

    public ApiSettingsStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        StorePath = Path.Combine(appData, "Multivid Streamer", "api-settings.json");
    }

    public ApiSettings LoadOrCreate()
    {
        ApiSettings? settings = Load();
        if (settings != null)
        {
            // Migrate settings files that predate ServerId: mint one and persist.
            if (string.IsNullOrWhiteSpace(settings.ServerId))
            {
                settings = new ApiSettings
                {
                    Port = settings.Port,
                    Token = settings.Token,
                    ServerId = CreateServerId()
                };
                Save(settings);
            }

            return settings;
        }

        settings = new ApiSettings
        {
            Token = CreateToken(),
            ServerId = CreateServerId()
        };

        Save(settings);
        return settings;
    }

    private ApiSettings? Load()
    {
        if (!File.Exists(StorePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(StorePath);
            ApiSettings? settings = JsonSerializer.Deserialize<ApiSettings>(json, jsonOptions);
            return string.IsNullOrWhiteSpace(settings?.Token) ? null : settings;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Save(ApiSettings settings)
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, jsonOptions);
        File.WriteAllText(StorePath, json);
    }

    private static string CreateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CreateServerId()
    {
        return Guid.NewGuid().ToString("N");
    }
}

public sealed class TrustedDevice
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public required string TokenHash { get; init; }

    public DateTime AddedUtc { get; init; }

    public DateTime? LastSeenUtc { get; set; }

    [JsonIgnore]
    public string AddedLocalText => AddedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string LastSeenLocalText => LastSeenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Jamais";
}

public sealed class TrustedDeviceStore
{
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object syncRoot = new();

    public string StorePath { get; }

    public TrustedDeviceStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        StorePath = Path.Combine(appData, "Multivid Streamer", "trusted-devices.json");
    }

    public List<TrustedDevice> Load()
    {
        lock (syncRoot)
        {
            return LoadCore();
        }
    }

    public TrustedDevice AddDevice(string name, string token)
    {
        TrustedDevice device = new()
        {
            Id = CreateId(),
            Name = NormalizeDeviceName(name),
            TokenHash = CreateTokenHash(token),
            AddedUtc = DateTime.UtcNow,
            LastSeenUtc = null
        };

        lock (syncRoot)
        {
            List<TrustedDevice> devices = LoadCore();
            devices.Add(device);
            SaveCore(devices);
        }

        return device;
    }

    public bool RemoveDevice(string deviceId)
    {
        lock (syncRoot)
        {
            List<TrustedDevice> devices = LoadCore();
            int removed = devices.RemoveAll(device => string.Equals(device.Id, deviceId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            SaveCore(devices);
            return true;
        }
    }

    // Revoque TOUS les appareils d'un coup. Renvoie le nombre d'appareils retires.
    public int RemoveAllDevices()
    {
        lock (syncRoot)
        {
            List<TrustedDevice> devices = LoadCore();
            int count = devices.Count;
            if (count == 0)
            {
                return 0;
            }

            SaveCore(new List<TrustedDevice>());
            return count;
        }
    }

    public bool TryAuthorizeToken(string token, out TrustedDevice? authorizedDevice)
    {
        authorizedDevice = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string tokenHash = CreateTokenHash(token);
        lock (syncRoot)
        {
            List<TrustedDevice> devices = LoadCore();
            TrustedDevice? device = devices.FirstOrDefault(candidate => FixedTimeEquals(candidate.TokenHash, tokenHash));
            if (device == null)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (device.LastSeenUtc == null || now - device.LastSeenUtc.Value > TimeSpan.FromMinutes(1))
            {
                device.LastSeenUtc = now;
                SaveCore(devices);
            }

            authorizedDevice = device;
            return true;
        }
    }

    public static string CreateDeviceToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return "mvs_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private List<TrustedDevice> LoadCore()
    {
        if (!File.Exists(StorePath))
        {
            return new List<TrustedDevice>();
        }

        try
        {
            string json = File.ReadAllText(StorePath);
            List<TrustedDevice>? devices = JsonSerializer.Deserialize<List<TrustedDevice>>(json, jsonOptions);
            return devices?
                .Where(IsValidDevice)
                .ToList() ?? new List<TrustedDevice>();
        }
        catch (Exception)
        {
            return new List<TrustedDevice>();
        }
    }

    private void SaveCore(List<TrustedDevice> devices)
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(devices, jsonOptions);
        File.WriteAllText(StorePath, json);
    }

    private static bool IsValidDevice(TrustedDevice device)
    {
        return !string.IsNullOrWhiteSpace(device.Id)
            && !string.IsNullOrWhiteSpace(device.Name)
            && !string.IsNullOrWhiteSpace(device.TokenHash)
            && device.AddedUtc != default;
    }

    private static string NormalizeDeviceName(string? name)
    {
        string normalized = string.IsNullOrWhiteSpace(name) ? "Appareil sans nom" : name.Trim();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private static string CreateId()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CreateTokenHash(string token)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

// Mot de passe d'appairage : une alternative STABLE au code qui tourne. L'usager de
// l'ordinateur le definit une fois (saisi cache) ; le casque peut ensuite se connecter
// avec ce mot de passe quand personne n'est devant le PC pour lire le code du moment.
// On ne garde que le hache (SHA-256, sale), jamais le mot de passe en clair. Outil LAN :
// pas de KDF lent, mais sel + hache suffisent a ne pas stocker le secret en clair.
public sealed class PairingPasswordStore
{
    private readonly object syncRoot = new();
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public string StorePath { get; }

    public PairingPasswordStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        StorePath = Path.Combine(appData, "Multivid Streamer", "pairing-password.json");
    }

    private sealed record Record(string Salt, string Hash);

    public bool HasPassword()
    {
        lock (syncRoot)
        {
            return Load() != null;
        }
    }

    // Definit (ou efface si vide/null) le mot de passe d'appairage.
    public void SetPassword(string? plain)
    {
        lock (syncRoot)
        {
            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (string.IsNullOrEmpty(plain))
            {
                if (File.Exists(StorePath))
                {
                    File.Delete(StorePath);
                }
                return;
            }

            byte[] salt = RandomNumberGenerator.GetBytes(16);
            Record record = new(Convert.ToHexString(salt).ToLowerInvariant(), HashWithSalt(plain, salt));
            File.WriteAllText(StorePath, JsonSerializer.Serialize(record, jsonOptions));
        }
    }

    // Vrai si le candidat correspond au mot de passe defini (false si aucun mot de passe).
    public bool Verify(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        lock (syncRoot)
        {
            Record? record = Load();
            if (record == null)
            {
                return false;
            }

            byte[] salt = Convert.FromHexString(record.Salt);
            string candidateHash = HashWithSalt(candidate, salt);
            byte[] a = System.Text.Encoding.UTF8.GetBytes(candidateHash);
            byte[] b = System.Text.Encoding.UTF8.GetBytes(record.Hash);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
    }

    private Record? Load()
    {
        if (!File.Exists(StorePath))
        {
            return null;
        }

        try
        {
            Record? record = JsonSerializer.Deserialize<Record>(File.ReadAllText(StorePath), jsonOptions);
            return record == null || string.IsNullOrWhiteSpace(record.Hash) || string.IsNullOrWhiteSpace(record.Salt)
                ? null
                : record;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string HashWithSalt(string plain, byte[] salt)
    {
        byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plain);
        byte[] combined = new byte[salt.Length + plainBytes.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(plainBytes, 0, combined, salt.Length, plainBytes.Length);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }
}
