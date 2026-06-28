using System.IO;
using MultividStreamer.App.Services;

namespace MultividStreamer.App.Selection;

public sealed class FileSystemBrowserService
{
    public IReadOnlyList<FileSystemSelectionEntry> GetEntries(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return DriveInfo.GetDrives()
                .Select(drive => new FileSystemSelectionEntry
                {
                    Name = drive.Name,
                    FullPath = drive.Name,
                    IsDirectory = true,
                    TypeLabel = GetDriveLabel(drive)
                })
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        List<FileSystemSelectionEntry> directories = GetDirectories(currentPath);
        List<FileSystemSelectionEntry> files = GetFiles(currentPath);
        return directories.Concat(files).ToList();
    }

    public string? GetParentPath(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(currentPath);
        string? root = Path.GetPathRoot(fullPath);
        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        DirectoryInfo? parent = Directory.GetParent(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return parent?.FullName;
    }

    private static List<FileSystemSelectionEntry> GetDirectories(string currentPath)
    {
        try
        {
            return Directory.EnumerateDirectories(currentPath)
                .Where(path => !ShouldSkip(path))
                .Select(path =>
                {
                    DirectoryInfo info = new(path);
                    return new FileSystemSelectionEntry
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        IsDirectory = true,
                        TypeLabel = "Dossier",
                        ModifiedLabel = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    };
                })
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return new List<FileSystemSelectionEntry>();
        }
    }

    private static List<FileSystemSelectionEntry> GetFiles(string currentPath)
    {
        try
        {
            return Directory.EnumerateFiles(currentPath)
                .Where(path => !ShouldSkip(path) && SupportedMediaTypes.IsSupportedFile(path))
                .Select(path =>
                {
                    FileInfo info = new(path);
                    return new FileSystemSelectionEntry
                    {
                        Name = info.Name,
                        FullPath = info.FullName,
                        IsDirectory = false,
                        TypeLabel = Path.GetExtension(info.Name).ToLowerInvariant(),
                        SizeLabel = FormatSize(info.Length),
                        ModifiedLabel = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    };
                })
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return new List<FileSystemSelectionEntry>();
        }
    }

    private static bool ShouldSkip(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden)
                || attributes.HasFlag(FileAttributes.System)
                || Path.GetFileName(path).StartsWith("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsRecoverableFileSystemException(exception))
        {
            return true;
        }
    }

    private static bool IsRecoverableFileSystemException(Exception exception)
    {
        return exception is UnauthorizedAccessException
            || exception is IOException
            || exception is System.Security.SecurityException
            || exception is ArgumentException
            || exception is NotSupportedException;
    }

    private static string GetDriveLabel(DriveInfo drive)
    {
        return drive.DriveType switch
        {
            DriveType.Fixed => "Disque",
            DriveType.Removable => "Amovible",
            DriveType.Network => "Reseau",
            DriveType.CDRom => "Optique",
            _ => "Disque"
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }
}
