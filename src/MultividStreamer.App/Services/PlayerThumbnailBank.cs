using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace MultividStreamer.App.Services;

/// <summary>
/// Lecture SEULE de la banque de vignettes SQLite du lecteur PC « Multivid VR »
/// (FGSoft). Ce lecteur a déjà extrait ~55 000 vignettes (base thumbnail_cache_v2,
/// clé = nom de fichier en MAJUSCULES). Le streamer peut donc resservir une vignette
/// déjà faite, instantanément et sans ffmpeg, pour tout fichier dont le nom correspond.
///
/// Jamais d'écriture : on ouvre en lecture seule pour ne pas gêner le lecteur s'il
/// tourne. En cas d'absence/erreur, renvoie null → le streamer retombe sur ffmpeg.
/// </summary>
public static class PlayerThumbnailBank
{
    private const string TableName = "thumbnail_cache_v2";

    private static string? resolvedDbPath;
    private static bool dbPathResolved;

    private static string? DbPath()
    {
        if (dbPathResolved)
        {
            return resolvedDbPath;
        }

        dbPathResolved = true;
        try
        {
            // persistentDataPath d'une app Unity Windows = %USERPROFILE%\AppData\LocalLow\<company>\<product>
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string candidate = Path.Combine(profile, "AppData", "LocalLow", "FGSoft", "Multivid VR", "thumbnails.db");
            resolvedDbPath = File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            resolvedDbPath = null;
        }

        return resolvedDbPath;
    }

    /// <summary>Indique si la banque du lecteur est présente sur cette machine.</summary>
    public static bool Available => DbPath() != null;

    /// <summary>
    /// Renvoie les octets PNG d'une vignette déjà en banque pour ce nom de fichier,
    /// ou null si absente / banque indisponible.
    /// </summary>
    public static byte[]? TryGetByFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string? path = DbPath();
        if (path == null)
        {
            return null;
        }

        try
        {
            // Ouverture en lecture seule ; WAL du lecteur autorise des lecteurs concurrents.
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using SqliteConnection connection = new(connectionString);
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"SELECT thumbnail_data FROM {TableName} " +
                "WHERE filepath = $name COLLATE NOCASE " +
                "ORDER BY max_dimension DESC LIMIT 1";
            command.Parameters.AddWithValue("$name", fileName);

            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
            {
                using Stream blob = reader.GetStream(0);
                using MemoryStream memory = new();
                blob.CopyTo(memory);
                byte[] data = memory.ToArray();
                return data.Length > 0 ? data : null;
            }
        }
        catch
        {
            // Banque verrouillée, schéma inattendu, etc. → repli ffmpeg.
        }

        return null;
    }
}
