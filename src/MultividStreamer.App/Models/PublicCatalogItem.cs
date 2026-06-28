namespace MultividStreamer.App.Models;

public sealed class PublicCatalogItem
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

    // True when the headset can't decode this format natively and the streamer will
    // transcode it live. The headset uses this to switch to time-based (?t=) seeking
    // and to fetch the duration from /media/{id}/info.
    public bool NeedsTranscode { get; init; }
}
