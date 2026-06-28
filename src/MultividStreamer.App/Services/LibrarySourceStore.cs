using System.IO;
using System.Text.Json;
using MultividStreamer.App.Models;

namespace MultividStreamer.App.Services;

public sealed class LibrarySourceStore
{
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true
    };

    public string StorePath { get; }

    public LibrarySourceStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        StorePath = Path.Combine(appData, "Multivid Streamer", "library-sources.json");
    }

    public List<LibrarySource> Load()
    {
        if (!File.Exists(StorePath))
        {
            return new List<LibrarySource>();
        }

        try
        {
            string json = File.ReadAllText(StorePath);
            List<LibrarySource>? sources = JsonSerializer.Deserialize<List<LibrarySource>>(json, jsonOptions);
            if (sources == null)
            {
                return new List<LibrarySource>();
            }

            // Option A: never auto-remove a source whose disk is currently offline
            // (external drive unplugged, sleeping NAS, etc.). Keep it so it reappears
            // by itself when the disk comes back; the UI greys out unavailable ones
            // (LibrarySource.IsAvailable). Only de-duplicate by path.
            List<LibrarySource> loadedSources = sources
                .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (loadedSources.Count != sources.Count)
            {
                Save(loadedSources);
            }

            return loadedSources;
        }
        catch (Exception)
        {
            return new List<LibrarySource>();
        }
    }

    public void Save(IEnumerable<LibrarySource> sources)
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(sources, jsonOptions);
        File.WriteAllText(StorePath, json);
    }
}
