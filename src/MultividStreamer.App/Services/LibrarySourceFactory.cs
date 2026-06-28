using System.IO;
using System.Security.Cryptography;
using System.Text;
using MultividStreamer.App.Models;

namespace MultividStreamer.App.Services;

public static class LibrarySourceFactory
{
    public static LibrarySource? TryCreate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }

        if (Directory.Exists(fullPath))
        {
            if (IsBlockedDirectorySource(fullPath))
            {
                return null;
            }

            return Create(fullPath, LibrarySourceKind.Directory);
        }

        if (File.Exists(fullPath) && SupportedMediaTypes.IsSupportedFile(fullPath))
        {
            return Create(fullPath, LibrarySourceKind.File);
        }

        return null;
    }

    public static bool Exists(LibrarySource source)
    {
        return source.Kind == LibrarySourceKind.Directory
            ? Directory.Exists(source.Path)
            : File.Exists(source.Path);
    }

    private static LibrarySource Create(string fullPath, LibrarySourceKind kind)
    {
        string name = kind == LibrarySourceKind.Directory
            ? GetDirectoryName(fullPath)
            : Path.GetFileName(fullPath);

        return new LibrarySource
        {
            Id = CreateStableId(kind, fullPath),
            Kind = kind,
            Path = fullPath,
            Name = name,
            AddedUtc = DateTime.UtcNow
        };
    }

    public static bool IsBlockedDirectorySource(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        string normalizedPath = NormalizeDirectoryPath(fullPath);
        string normalizedRoot = NormalizeDirectoryPath(root ?? string.Empty);
        string normalizedProfile = NormalizeDirectoryPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, normalizedProfile, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDirectoryName(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string CreateStableId(LibrarySourceKind kind, string path)
    {
        string key = $"{kind}|{path}".ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string id = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return $"source_{id}";
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
