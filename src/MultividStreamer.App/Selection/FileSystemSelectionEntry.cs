namespace MultividStreamer.App.Selection;

public sealed class FileSystemSelectionEntry
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public required string TypeLabel { get; init; }

    public string SizeLabel { get; init; } = string.Empty;

    public string ModifiedLabel { get; init; } = string.Empty;
}
