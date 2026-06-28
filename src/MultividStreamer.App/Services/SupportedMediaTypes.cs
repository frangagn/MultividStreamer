using System.IO;

namespace MultividStreamer.App.Services;

public static class SupportedMediaTypes
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".mov",
        ".avi",
        ".webm",
        ".m4v",
        ".ts",
        ".mts",
        ".m2ts",
        ".wmv"
    };

    private static readonly HashSet<string> StandardImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".gif"
    };

    private static readonly HashSet<string> RawImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nef",
        ".cr2",
        ".cr3"
    };

    public static bool IsSupportedFile(string path)
    {
        string extension = Path.GetExtension(path);
        return VideoExtensions.Contains(extension)
            || StandardImageExtensions.Contains(extension)
            || RawImageExtensions.Contains(extension)
            || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedZipImage(string path)
    {
        string extension = Path.GetExtension(path);
        return StandardImageExtensions.Contains(extension)
            || RawImageExtensions.Contains(extension);
    }

    public static bool TryGetCatalogType(string path, out string kind, out string? formatGroup)
    {
        string extension = Path.GetExtension(path);

        if (VideoExtensions.Contains(extension))
        {
            kind = "video";
            formatGroup = null;
            return true;
        }

        if (StandardImageExtensions.Contains(extension))
        {
            kind = "image";
            formatGroup = "standard";
            return true;
        }

        if (RawImageExtensions.Contains(extension))
        {
            kind = "image";
            formatGroup = "raw";
            return true;
        }

        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            kind = "zip";
            formatGroup = "archive";
            return true;
        }

        kind = string.Empty;
        formatGroup = null;
        return false;
    }
}
