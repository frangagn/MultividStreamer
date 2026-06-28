namespace MultividStreamer.App.Models;

public sealed class CatalogItem
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public required string Kind { get; init; }

    public string? FormatGroup { get; init; }

    public required string FileName { get; init; }

    public required string Directory { get; init; }

    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime ModifiedUtc { get; init; }

    public required string StreamUrl { get; init; }

    public required string AbsolutePath { get; init; }
}
