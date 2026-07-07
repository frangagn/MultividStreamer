using System.Diagnostics;
using System.IO;

namespace MultividStreamer.App.Services;

/// <summary>
/// Picks the ffmpeg H.264 encoder to use for live transcode, so the SAME build runs
/// optimally on any machine: NVIDIA NVENC, Intel QSV/Arc, AMD AMF, or CPU x264 as a
/// universal fallback.
///
/// "auto" detection runs a tiny real test-encode for each hardware candidate and keeps
/// the first that actually succeeds — presence in `ffmpeg -encoders` isn't enough
/// (needs the GPU + a working driver), and the test mirrors the real pipeline shape
/// (software frames → hardware encoder), so it catches setups where the simple
/// invocation would fail. The result is cached; detection runs once, off the hot path.
/// </summary>
public static class TranscodeEncoder
{
    public sealed record Profile(string Key, string DisplayName, string Codec, string VideoArgs);

    // Preference order: hardware first (quality/CPU win), CPU x264 last (always works).
    //
    // -g 60 (short GOP, ~2s) everywhere: the fMP4 muxer (frag_keyframe) can only flush
    // a fragment when the NEXT keyframe arrives, so startup latency ≈ one full GOP of
    // encode time. The encoder defaults (~250 frames) made transcoded streams take
    // 8-10s to show their first frame. x264 also needs -sc_threshold 0 so scene-cut
    // keyframes don't make fragments irregular.
    private static readonly Profile[] Profiles =
    {
        new("nvenc", "NVIDIA NVENC", "h264_nvenc", "-c:v h264_nvenc -preset p5 -rc vbr -cq 19 -g 60 -pix_fmt yuv420p"),
        new("qsv",   "Intel QSV/Arc", "h264_qsv",  "-c:v h264_qsv -global_quality 20 -preset veryslow -g 60"),
        new("amf",   "AMD AMF",      "h264_amf",   "-c:v h264_amf -quality quality -rc cqp -qp_i 20 -qp_p 20 -g 60 -pix_fmt yuv420p"),
        new("cpu",   "CPU x264",     "libx264",    "-c:v libx264 -preset veryfast -crf 18 -g 60 -sc_threshold 0 -pix_fmt yuv420p"),
    };

    private static readonly Profile CpuFallback = Profiles[^1];

    private static volatile Profile? current;

    // The encoder in effect. Returns the CPU fallback until detection finishes (safe
    // default), then the resolved profile.
    public static Profile Current => current ?? CpuFallback;

    /// <summary>
    /// Kicks off encoder resolution (off the UI/hot path) and caches it. Call once at
    /// startup. <paramref name="preference"/>: "auto" or a forced key (nvenc/qsv/amf/cpu).
    /// </summary>
    public static void Initialize(string? preference)
    {
        string pref = string.IsNullOrWhiteSpace(preference) ? "auto" : preference.Trim().ToLowerInvariant();
        Task.Run(() => current = Resolve(pref));
    }

    private static Profile Resolve(string preference)
    {
        string? ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath == null)
        {
            return CpuFallback;
        }

        // Forced choice from config (if it names a known profile).
        if (preference != "auto")
        {
            Profile? forced = Profiles.FirstOrDefault(p => string.Equals(p.Key, preference, StringComparison.OrdinalIgnoreCase));
            if (forced != null)
            {
                return forced;
            }
        }

        // Auto: first hardware encoder that actually works, else CPU.
        foreach (Profile profile in Profiles)
        {
            if (string.Equals(profile.Key, "cpu", StringComparison.Ordinal))
            {
                break;
            }

            if (EncoderWorks(ffmpegPath, profile.Codec))
            {
                return profile;
            }
        }

        return CpuFallback;
    }

    private static bool EncoderWorks(string ffmpegPath, string codec)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error "
                    + "-f lavfi -i color=c=black:s=128x128:r=5 -frames:v 3 "
                    + $"-c:v {codec} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? ResolveFfmpegPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        return File.Exists(path) ? path : null;
    }
}
