namespace MultividStreamer.App.Models;

/// <summary>
/// User-editable list of file extensions the headset can't play natively and that the
/// streamer transcodes live to H.264/AAC. Persisted as JSON in AppData
/// (transcode-formats.json) so a new format can be supported by editing the file — no
/// recompile. Adding an extension here also makes that format show up as a video in the
/// catalogue (see <see cref="Services.SupportedMediaTypes"/>).
/// </summary>
public sealed class TranscodeSettings
{
    public List<string> TranscodeExtensions { get; set; } = new();
}
