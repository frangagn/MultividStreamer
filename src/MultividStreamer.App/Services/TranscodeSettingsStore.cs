using System.IO;
using System.Text.Json;
using MultividStreamer.App.Models;

namespace MultividStreamer.App.Services;

/// <summary>
/// Loads/saves the transcode config (transcode-formats.json in AppData): which file
/// extensions to transcode, and which ffmpeg encoder to use. On first run it seeds the
/// file with the defaults so the user can find and edit it. To add a format or force an
/// encoder later, just edit the JSON — no code change, no recompile.
/// </summary>
public sealed class TranscodeSettingsStore
{
    // Seeded on first run. These are the formats where serving the raw file to the
    // headset fails or seeks badly, so the streamer transcodes them to seekable fMP4:
    //   wmv/flv/avi -> AVPro/ExoPlayer can't decode the raw codec at all;
    //   ts/mts/m2ts -> MPEG-TS/AVCHD have no seek index, so native byte-range seek on
    //                  the raw stream is slow/unreliable (the "seek sometimes fails" bug).
    // mkv is deliberately NOT here: ExoPlayer decodes and seeks mkv natively, and live
    // transcoding large H.264/H.265 mkv would be a regression. Use the per-file "force
    // transcode" action for the rare mkv that misbehaves.
    private static readonly string[] DefaultExtensions = { ".wmv", ".flv", ".avi", ".ts", ".mts", ".m2ts" };
    private const string DefaultEncoder = "auto";

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

    public TranscodeSettings Load()
    {
        if (!File.Exists(StorePath))
        {
            TranscodeSettings seeded = new()
            {
                TranscodeExtensions = DefaultExtensions.ToList(),
                Encoder = DefaultEncoder
            };
            Save(seeded);
            return seeded;
        }

        try
        {
            string json = File.ReadAllText(StorePath);
            TranscodeSettings? settings = JsonSerializer.Deserialize<TranscodeSettings>(json, jsonOptions);
            if (settings == null)
            {
                return Defaults();
            }

            if (settings.TranscodeExtensions == null || settings.TranscodeExtensions.Count == 0)
            {
                settings.TranscodeExtensions = DefaultExtensions.ToList();
            }

            if (string.IsNullOrWhiteSpace(settings.Encoder))
            {
                settings.Encoder = DefaultEncoder;
            }

            return settings;
        }
        catch (Exception)
        {
            // Corrupt/unreadable config must never break startup — fall back to defaults.
            return Defaults();
        }
    }

    public void Save(TranscodeSettings settings)
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, jsonOptions);
        File.WriteAllText(StorePath, json);
    }

    // Accept "wmv", ".WMV", " .wmv " etc. → normalize to a lowercase, dot-prefixed,
    // de-duplicated set so matching against Path.GetExtension is reliable.
    public static HashSet<string> NormalizeExtensions(IEnumerable<string> extensions)
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

    private static TranscodeSettings Defaults()
    {
        return new TranscodeSettings
        {
            TranscodeExtensions = DefaultExtensions.ToList(),
            Encoder = DefaultEncoder
        };
    }
}
