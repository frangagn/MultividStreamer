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

    // Extra video extensions that the headset can't play natively and that the streamer
    // transcodes live (loaded from transcode-formats.json at startup via
    // SetTranscodeExtensions). Treated as normal videos for cataloguing/listing so they
    // appear in the browser, and flagged by NeedsTranscode so the stream endpoint knows
    // to pipe them through ffmpeg. Adding an extension to the config is all that's needed.
    private static HashSet<string> transcodeVideoExtensions = new(StringComparer.OrdinalIgnoreCase);

    public static void SetTranscodeExtensions(IEnumerable<string> extensions)
    {
        transcodeVideoExtensions = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
    }

    public static bool NeedsTranscode(string path)
    {
        return transcodeVideoExtensions.Contains(Path.GetExtension(path));
    }

    private static bool IsVideoExtension(string extension)
    {
        return VideoExtensions.Contains(extension) || transcodeVideoExtensions.Contains(extension);
    }

    public static bool IsSupportedFile(string path)
    {
        string extension = Path.GetExtension(path);
        return IsVideoExtension(extension)
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

        if (IsVideoExtension(extension))
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
