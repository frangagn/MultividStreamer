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

    // Which ffmpeg encoder to use. "auto" (default) probes the machine and picks the
    // best working hardware encoder (NVIDIA NVENC > Intel QSV/Arc > AMD AMF), falling
    // back to CPU x264. Force one with: "nvenc" | "qsv" | "amf" | "cpu". Lets the same
    // build run optimally on different machines (RTX desktop, Arc laptop, …).
    public string Encoder { get; set; } = "auto";
}
