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
