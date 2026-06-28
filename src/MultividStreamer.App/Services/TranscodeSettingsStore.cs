using System.IO;
using System.Text.Json;
using MultividStreamer.App.Models;

namespace MultividStreamer.App.Services;

/// <summary>
/// Loads/saves the list of extensions to transcode (transcode-formats.json in AppData).
/// On first run it seeds the file with the defaults so the user can find and edit it.
/// To add a format later, just edit the JSON — no code change, no recompile.
/// </summary>
public sealed class TranscodeSettingsStore
{
    // Seeded on first run. wmv/flv are the known formats Quest/AVPro can't decode.
    private static readonly string[] DefaultExtensions = { ".wmv", ".flv" };

    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true
    };

    public string StorePath { get; }

    public TranscodeSettingsStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        StorePath = Path.Combine(appData, "Multivid Streamer", "transcode-formats.json");
    }

    public HashSet<string> Load()
    {
        if (!File.Exists(StorePath))
        {
            Save(DefaultExtensions);
            return Normalize(DefaultExtensions);
        }

        try
        {
            string json = File.ReadAllText(StorePath);
            TranscodeSettings? settings = JsonSerializer.Deserialize<TranscodeSettings>(json, jsonOptions);
            if (settings?.TranscodeExtensions == null || settings.TranscodeExtensions.Count == 0)
            {
                return Normalize(DefaultExtensions);
            }

            return Normalize(settings.TranscodeExtensions);
        }
        catch (Exception)
        {
            // Corrupt/unreadable config must never break startup — fall back to defaults.
            return Normalize(DefaultExtensions);
        }
    }

    public void Save(IEnumerable<string> extensions)
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        TranscodeSettings settings = new()
        {
            TranscodeExtensions = Normalize(extensions)
                .OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        string json = JsonSerializer.Serialize(settings, jsonOptions);
        File.WriteAllText(StorePath, json);
    }

    // Accept "wmv", ".WMV", " .wmv " etc. → normalize to a lowercase, dot-prefixed,
    // de-duplicated set so matching against Path.GetExtension is reliable.
    private static HashSet<string> Normalize(IEnumerable<string> extensions)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string extension = raw.Trim().ToLowerInvariant();
            if (!extension.StartsWith('.'))
            {
                extension = "." + extension;
            }

            result.Add(extension);
        }

        return result;
    }
}
