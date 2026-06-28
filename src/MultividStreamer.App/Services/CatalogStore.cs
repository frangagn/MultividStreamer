using System.IO;
using System.Text.Json;
using MultividStreamer.App.Models;

namespace MultividStreamer.App.Services;

public sealed class CatalogStore
{
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true
    };

    // In-memory cache so we don't re-read and re-parse the (potentially tens of
    // MB) catalog.json on every request. The streamer calls Load() on every
    // /stream request via FindCatalogItem; with a large library that JSON parse
    // stalls each request for hundreds of ms, starving the headset's buffer and
    // causing playback stutter even when bandwidth is plentiful. The cache is
    // keyed on the file's last-write time and length, so an external change to
    // catalog.json still triggers a reload.
    private readonly object cacheLock = new();
    private List<CatalogItem>? cachedItems;
    private DateTime cachedWriteUtc;
    private long cachedLength;

    public string StorePath { get; }

    public CatalogStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        StorePath = Path.Combine(appData, "Multivid Streamer", "catalog.json");
    }

    public List<CatalogItem> Load()
    {
        if (!File.Exists(StorePath))
        {
            return new List<CatalogItem>();
        }

        try
        {
            FileInfo info = new(StorePath);

            lock (cacheLock)
            {
                if (cachedItems != null &&
                    info.LastWriteTimeUtc == cachedWriteUtc &&
                    info.Length == cachedLength)
                {
                    return cachedItems;
                }
            }

            string json = File.ReadAllText(StorePath);
            List<CatalogItem> items = JsonSerializer.Deserialize<List<CatalogItem>>(json, jsonOptions) ?? new List<CatalogItem>();

            lock (cacheLock)
            {
                cachedItems = items;
                cachedWriteUtc = info.LastWriteTimeUtc;
                cachedLength = info.Length;
            }

            return items;
        }
        catch (Exception)
        {
            return new List<CatalogItem>();
        }
    }

    public void Save(IEnumerable<CatalogItem> items)
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<CatalogItem> snapshot = items as List<CatalogItem> ?? new List<CatalogItem>(items);
        string json = JsonSerializer.Serialize(snapshot, jsonOptions);
        File.WriteAllText(StorePath, json);

        // Refresh the cache from the data we just wrote so the next request
        // serves from memory without re-reading the file.
        try
        {
            FileInfo info = new(StorePath);
            lock (cacheLock)
            {
                cachedItems = snapshot;
                cachedWriteUtc = info.LastWriteTimeUtc;
                cachedLength = info.Length;
            }
        }
        catch (Exception)
        {
            lock (cacheLock)
            {
                cachedItems = null;
            }
        }
    }
}
