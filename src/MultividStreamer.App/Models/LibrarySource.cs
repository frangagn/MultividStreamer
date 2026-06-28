namespace MultividStreamer.App.Models;

public sealed class LibrarySource
{
    public required string Id { get; init; }

    public required LibrarySourceKind Kind { get; init; }

    public required string Path { get; init; }

    public required string Name { get; init; }

    public DateTime AddedUtc { get; init; }

    public string KindLabel => Kind == LibrarySourceKind.Directory ? "Dossier" : "Fichier";

    // Whether the source's disk/path is reachable right now. Evaluated on demand so the
    // UI can grey out offline sources (Option A: they are kept, never auto-removed, and
    // light up again when the disk returns). Re-evaluate via SourcesList.Items.Refresh().
    public bool IsAvailable => Kind == LibrarySourceKind.Directory
        ? System.IO.Directory.Exists(Path)
        : System.IO.File.Exists(Path);
}
