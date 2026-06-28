using System.IO;
using System.Security.Cryptography;
using System.Text;
using MultividStreamer.App.Models;

namespace MultividStreamer.App.Services;

public sealed class LibraryCatalogScanner
{
    public ScanResult Scan(IEnumerable<LibrarySource> sources)
    {
        List<LibrarySource> existingSources = new();
        List<CatalogItem> items = new();
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        int missingSourcesRemoved = 0;
        int duplicateFilesSkipped = 0;

        foreach (LibrarySource source in sources)
        {
            if (!LibrarySourceFactory.Exists(source))
            {
                missingSourcesRemoved++;
                continue;
            }

            existingSources.Add(source);

            foreach (string filePath in EnumerateSourceFiles(source))
            {
                string fullPath = Path.GetFullPath(filePath);
                if (!seenPaths.Add(fullPath))
                {
                    duplicateFilesSkipped++;
                    continue;
                }

                CatalogItem? item = TryCreateCatalogItem(source, fullPath);
                if (item != null)
                {
                    items.Add(item);
                }
            }
        }

        return new ScanResult
        {
            ExistingSources = existingSources,
            Items = items
                .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MissingSourcesRemoved = missingSourcesRemoved,
            DuplicateFilesSkipped = duplicateFilesSkipped
        };
    }

    private static IEnumerable<string> EnumerateSourceFiles(LibrarySource source)
    {
        if (source.Kind == LibrarySourceKind.File)
        {
            if (SupportedMediaTypes.IsSupportedFile(source.Path))
            {
                yield return source.Path;
            }

            yield break;
        }

        foreach (string filePath in EnumerateAccessibleFiles(source.Path))
        {
            if (SupportedMediaTypes.IsSupportedFile(filePath))
            {
                yield return filePath;
            }
        }
    }

    private static IEnumerable<string> EnumerateAccessibleFiles(string rootPath)
    {
        Stack<string> directoriesToScan = new();
        directoriesToScan.Push(rootPath);

        while (directoriesToScan.Count != 0)
        {
            string currentPath = directoriesToScan.Pop();

            foreach (string filePath in GetAccessibleFiles(currentPath))
            {
                if (!ShouldSkip(filePath))
                {
                    yield return filePath;
                }
            }

            foreach (string directoryPath in GetAccessibleDirectories(currentPath))
            {
                if (!ShouldSkipDirectory(directoryPath))
                {
                    directoriesToScan.Push(directoryPath);
                }
            }
        }
    }

    private static IEnumerable<string> GetAccessibleFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> GetAccessibleDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return Array.Empty<string>();
        }
    }

    private static CatalogItem? TryCreateCatalogItem(LibrarySource source, string fullPath)
    {
        if (!SupportedMediaTypes.TryGetCatalogType(fullPath, out string kind, out string? formatGroup))
        {
            return null;
        }

        try
        {
            FileInfo file = new(fullPath);
            if (!file.Exists)
            {
                return null;
            }

            return new CatalogItem
            {
                Id = CreateStableItemId(fullPath),
                SourceId = source.Id,
                Kind = kind,
                FormatGroup = formatGroup,
                FileName = file.Name,
                Directory = GetRelativeDirectory(source, file),
                Extension = file.Extension.ToLowerInvariant(),
                SizeBytes = file.Length,
                ModifiedUtc = file.LastWriteTimeUtc,
                StreamUrl = $"/stream/{CreateStableItemId(fullPath)}",
                AbsolutePath = file.FullName
            };
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return null;
        }
    }

    private static string GetRelativeDirectory(LibrarySource source, FileInfo file)
    {
        if (source.Kind == LibrarySourceKind.File || string.IsNullOrWhiteSpace(file.DirectoryName))
        {
            return string.Empty;
        }

        string relative = Path.GetRelativePath(source.Path, file.DirectoryName);
        return relative == "." ? string.Empty : relative;
    }

    private static bool ShouldSkipDirectory(string path)
    {
        string directoryName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return directoryName.StartsWith("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || directoryName.StartsWith("found.0", StringComparison.OrdinalIgnoreCase)
            || ShouldSkip(path);
    }

    private static bool ShouldSkip(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden)
                || attributes.HasFlag(FileAttributes.System);
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return true;
        }
    }

    private static string CreateStableItemId(string path)
    {
        string key = Path.GetFullPath(path).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string id = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return $"item_{id}";
    }

    private static bool IsRecoverableFileSystemException(Exception exception)
    {
        return exception is UnauthorizedAccessException
            || exception is IOException
            || exception is System.Security.SecurityException
            || exception is ArgumentException
            || exception is NotSupportedException;
    }
}
