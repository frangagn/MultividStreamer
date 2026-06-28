namespace MultividStreamer.App.Models;

public sealed class ScanResult
{
    public required List<LibrarySource> ExistingSources { get; init; }

    public required List<CatalogItem> Items { get; init; }

    public required int MissingSourcesRemoved { get; init; }

    public required int DuplicateFilesSkipped { get; init; }

    public int VideoCount => Items.Count(item => item.Kind == "video");

    public int ImageCount => Items.Count(item => item.Kind == "image");

    public int RawImageCount => Items.Count(item => item.Kind == "image" && item.FormatGroup == "raw");

    public int ZipCount => Items.Count(item => item.Kind == "zip");
}
