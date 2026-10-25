using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZanburakVideoProcessor.Models;

namespace ZanburakVideoProcessor.Services;

public sealed class ProbeResult
{
    public int Duration { get; init; }
    public int Height { get; init; }
    public int? BitrateKbps { get; init; }
}

public sealed class FfmpegEncoder
{
    private readonly FfmpegLocator _locator;
    private string _encoder = "libx264";

    public FfmpegEncoder(FfmpegLocator locator)
    {
        _locator = locator;
    }

    public ProbeResult Probe(string input)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _locator.FfprobePath,
            ArgumentList =
            {
                "-v", "quiet",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                input,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("اجرای FFprobe ممکن نشد.");
        var json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durEl)
            && double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur))
        {
            duration = (int)Math.Round(dur);
        }

        var height = 720;
        int? bitrate = null;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    if (stream.TryGetProperty("height", out var h)) height = h.GetInt32();
                    if (stream.TryGetProperty("bit_rate", out var br) && int.TryParse(br.GetString(), out var b))
                    {
                        bitrate = (int)Math.Round(b / 1000.0);
                    }
                    break;
                }
            }
        }

        return new ProbeResult { Duration = Math.Max(0, duration), Height = Math.Max(144, height), BitrateKbps = bitrate };
    }

    public async Task DetectEncoderAsync(CancellationToken ct)
    {
        try
        {
            var args = new List<string>
            {
                "-hide_banner", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2",
                "-c:v", "h264_nvenc", "-preset", "p4", "-f", "null", "-",
            };
            var code = await RunAsync(_locator.FfmpegPath, args, null, TimeSpan.FromSeconds(20), ct);
            _encoder = code == 0 ? "h264_nvenc" : "libx264";
        }
        catch
        {
            _encoder = "libx264";
        }
    }

    public async Task EncodeAsync(
        string input,
        string workDir,
        TaskPayload task,
        ProbeResult probe,
        string? watermarkImage,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var outputs = task.Outputs.Count > 0 ? task.Outputs : new List<string> { "stream" };
        var sourceHeight = probe.Height;
        // Encode exactly the rungs the admin picked for each output. The backend
        // already resolved fallbacks; inventing defaults here would produce files
        // the site was never told to publish.
        var streamQs = NormalizeQualities(sourceHeight, task.StreamQualities);
        var downloadQs = NormalizeQualities(sourceHeight, task.DownloadQualities);
        var duration = probe.Duration > 0 ? probe.Duration : task.Raw.Duration;

        if (outputs.Contains("stream"))
        {
            if (streamQs.Count == 0)
            {
                throw new InvalidOperationException("برای استریم هیچ کیفیتی انتخاب نشده است.");
            }
            await EncodeHlsAsync(input, Path.Combine(workDir, "stream"), streamQs, probe, task, watermarkImage, encrypted: true, duration, progress, 0.05, 0.70, ct);
        }

        if (outputs.Contains("trailer"))
        {
            var trailerQs = streamQs.Count > 0 ? streamQs : downloadQs;
            if (trailerQs.Count == 0)
            {
                throw new InvalidOperationException("برای تریلر هیچ کیفیتی انتخاب نشده است.");
            }
            await EncodeHlsAsync(input, Path.Combine(workDir, "trailer"), trailerQs, probe, task, watermarkImage, encrypted: false, duration, progress, 0.05, 0.70, ct);
        }

        if (outputs.Contains("download"))
        {
            if (downloadQs.Count == 0)
            {
                throw new InvalidOperationException("برای دانلود هیچ کیفیتی انتخاب نشده است.");
            }
            await EncodeDownloadsAsync(input, Path.Combine(workDir, "download"), downloadQs, probe, task, watermarkImage, duration, progress, 0.70, 0.95, ct);
        }
    }

    private async Task EncodeHlsAsync(
        string input,
        string outDir,
        List<int> qualities,
        ProbeResult probe,
        TaskPayload task,
        string? watermarkImage,
        bool encrypted,
        int duration,
        IProgress<double>? progress,
        double startFrac,
        double endFrac,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var keysDir = Path.Combine(outDir, "keys");
        Directory.CreateDirectory(keysDir);

        string? keyInfo = null;
        string? keyName = null;
        if (encrypted)
        {
            keyName = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".key";
            var keyBytes = RandomNumberGenerator.GetBytes(16);
            var keyPath = Path.Combine(keysDir, keyName);
            await File.WriteAllBytesAsync(keyPath, keyBytes, ct);
            var iv = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            keyInfo = Path.Combine(outDir, "hls_encryption.keyinfo");
            var absKey = keyPath.Replace('\\', '/');
            await File.WriteAllTextAsync(keyInfo, absKey + "\n" + absKey + "\n" + iv + "\n", ct);
        }

        var segment = Math.Max(2, task.HlsSegmentLength);
        var master = new StringBuilder();
        master.AppendLine("#EXTM3U");
        var count = Math.Max(1, qualities.Count);
        for (var i = 0; i < qualities.Count; i++)
        {
            var q = qualities[i];
            var (w, h) = Dim(task, q);
            var vBit = CapBitrate(Bitrate(task, q), probe.BitrateKbps);
            var aBit = AudioBitrate(task, q);
            var playlist = $"playlist_{i}_{vBit}.m3u8";
            var segments = $"playlist_{i}_{vBit}_%05d.ts";
            var args = new List<string>
            {
                "-hide_banner", "-y", "-i", input,
                "-vf", BuildFilter(w, h, task.Watermark, watermarkImage),
                "-c:v", _encoder,
            };
            args.AddRange(EncoderParams(task, q, vBit));
            args.AddRange(new[]
            {
                "-b:v", vBit + "k",
                "-c:a", "aac",
                "-b:a", aBit + "k",
                "-hls_playlist_type", "vod",
                "-hls_time", segment.ToString(CultureInfo.InvariantCulture),
                "-hls_segment_filename", Path.Combine(outDir, segments),
            });
            if (keyInfo != null)
            {
                args.AddRange(new[] { "-hls_key_info_file", keyInfo, "-hls_flags", "independent_segments+periodic_rekey" });
            }
            else
            {
                args.AddRange(new[] { "-hls_flags", "independent_segments" });
            }

            args.Add(Path.Combine(outDir, playlist));
            var mapped = startFrac + ((i + 1.0) / count) * (endFrac - startFrac);
            await RunFfmpegWithProgress(args, duration, p => progress?.Report(startFrac + (p / 100.0) * ((mapped - startFrac))), ct);

            RewriteKeyUris(Path.Combine(outDir, playlist), keyName);
            master.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={vBit * 1000},RESOLUTION={w}x{h},CODECS=\"avc1.42c01f,mp4a.40.2\"");
            master.AppendLine(playlist);
        }

        master.AppendLine("#EXT-X-ENDLIST");
        await File.WriteAllTextAsync(Path.Combine(outDir, "playlist.m3u8"), master.ToString(), ct);
        if (keyInfo != null && File.Exists(keyInfo))
        {
            File.Delete(keyInfo);
        }
    }

    private async Task EncodeDownloadsAsync(
        string input,
        string outDir,
        List<int> qualities,
        ProbeResult probe,
        TaskPayload task,
        string? watermarkImage,
        int duration,
        IProgress<double>? progress,
        double startFrac,
        double endFrac,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var count = Math.Max(1, qualities.Count);
        for (var i = 0; i < qualities.Count; i++)
        {
            var q = qualities[i];
            var (w, h) = Dim(task, q);
            var vBit = CapBitrate(Bitrate(task, q), probe.BitrateKbps);
            var aBit = AudioBitrate(task, q);
            var dir = Path.Combine(outDir, $"{q}p");
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, $"video-{q}p.mp4");
            var args = new List<string>
            {
                "-hide_banner", "-y", "-i", input,
                "-vf", BuildFilter(w, h, task.Watermark, watermarkImage),
                "-c:v", _encoder,
            };
            args.AddRange(EncoderParams(task, q, vBit));
            args.AddRange(new[]
            {
                "-b:v", vBit + "k",
                "-c:a", "aac",
                "-b:a", aBit + "k",
                "-movflags", "+faststart",
                output,
            });
            var span = (endFrac - startFrac) / count;
            await RunFfmpegWithProgress(args, duration, p => progress?.Report(startFrac + (i * span) + (p / 100.0) * span), ct);
            if (!File.Exists(output))
            {
                throw new InvalidOperationException($"خروجی دانلود {q}p ساخته نشد.");
            }
        }
    }

    private async Task RunFfmpegWithProgress(List<string> args, int duration, Action<double> onPercent, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _locator.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("اجرای FFmpeg ممکن نشد.");
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            err.AppendLine(e.Data);
            var m = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (m.Success && duration > 0)
            {
                var sec = int.Parse(m.Groups[1].Value) * 3600
                    + int.Parse(m.Groups[2].Value) * 60
                    + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                onPercent(Math.Min(99, sec * 100.0 / duration));
            }
        };
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("پردازش FFmpeg ناموفق بود.\n" + Trim(err.ToString(), 800));
        }
    }

    private static async Task<int> RunAsync(string file, List<string> args, string? cwd, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("اجرای فرایند ممکن نشد.");
        var completed = await Task.Run(() => proc.WaitForExit((int)timeout.TotalMilliseconds), ct);
        if (!completed)
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            return -1;
        }
        return proc.ExitCode;
    }

    private IEnumerable<string> EncoderParams(TaskPayload task, int quality, int videoBitrate)
    {
        var maxrate = (int)Math.Round(videoBitrate * 1.35);
        var bufsize = (int)Math.Round(videoBitrate * 2.2);
        var cq = Crf(task, quality);
        if (_encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                "-preset", "p4", "-rc", "vbr", "-cq", cq.ToString(),
                "-maxrate", maxrate + "k", "-bufsize", bufsize + "k",
                "-profile:v", "high", "-spatial-aq", "1", "-temporal-aq", "1",
                "-g", "48", "-pix_fmt", "yuv420p",
            };
        }

        return new[]
        {
            "-preset", "veryfast", "-crf", cq.ToString(),
            "-maxrate", maxrate + "k", "-bufsize", bufsize + "k",
            "-profile:v", "high", "-level", "4.1", "-threads", "0",
            "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
        };
    }

    private static string BuildFilter(int width, int height, WatermarkInfo wm, string? imagePath)
    {
        width -= width % 2;
        height -= height % 2;
        if (wm is not { Enabled: true })
        {
            return $"scale={width}:{height}";
        }

        var margin = Math.Max(18, (int)Math.Round(Math.Min(width, height) * 0.035));
        if (wm.Type == "image" && !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
        {
            var path = imagePath.Replace('\\', '/').Replace(":", "\\:").Replace("'", "\\'");
            var (ox, oy) = OverlayXy(wm.Position, margin);
            var wmW = Math.Max(24, (int)Math.Round(width * wm.ImageScale));
            wmW -= wmW % 2;
            return $"[in]scale={width}:{height}[bg];movie='{path}',scale={wmW}:-1,format=rgba,colorchannelmixer=aa={wm.Opacity:0.00}[wm];[bg][wm]overlay={ox}:{oy}[out]";
        }

        var font = ResolveFont(wm.Font).Replace('\\', '/').Replace(":", "\\:");
        var fontSize = Math.Max(14, (int)Math.Round(wm.FontSize * (height / 720.0)));
        var (x, y) = DrawTextXy(wm.Position, margin);
        var text = EscapeDrawText(wm.Text);
        var parts = new List<string>
        {
            $"fontfile='{font}'",
            $"text='{text}'",
            "fontsize=" + fontSize,
            $"fontcolor={FfmpegColor(wm.FontColor, wm.Opacity)}",
            "x=" + x,
            "y=" + y,
        };
        if (wm.BorderEnabled && wm.BorderWidth > 0)
        {
            parts.Add("borderw=" + wm.BorderWidth);
            parts.Add("bordercolor=" + FfmpegColor(wm.BorderColor, Math.Min(1, wm.Opacity + 0.1)));
        }
        else
        {
            parts.Add("borderw=0");
        }

        if (wm.BgEnabled)
        {
            var pad = Math.Max(0, (int)Math.Round(wm.BgPadding * (height / 720.0)));
            parts.Add("box=1");
            parts.Add("boxborderw=" + pad);
            parts.Add("boxcolor=" + FfmpegColor(wm.BgColor, wm.BgOpacity * wm.Opacity));
        }

        return $"scale={width}:{height},drawtext={string.Join(':', parts)}";
    }

    private static string ResolveFont(string key)
    {
        var map = new Dictionary<string, string>
        {
            ["tahoma"] = @"C:\Windows\Fonts\tahoma.ttf",
            ["tahoma-bold"] = @"C:\Windows\Fonts\tahomabd.ttf",
            ["arial"] = @"C:\Windows\Fonts\arial.ttf",
            ["arial-bold"] = @"C:\Windows\Fonts\arialbd.ttf",
            ["segoe"] = @"C:\Windows\Fonts\segoeui.ttf",
            ["segoe-bold"] = @"C:\Windows\Fonts\segoeuib.ttf",
        };
        foreach (var path in new[] { map.GetValueOrDefault(key), @"C:\Windows\Fonts\tahoma.ttf", @"C:\Windows\Fonts\arial.ttf" })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        }

        return @"C:\Windows\Fonts\arial.ttf";
    }

    private static (string x, string y) DrawTextXy(string position, int margin)
    {
        var m = Math.Max(8, margin).ToString();
        return position switch
        {
            "top-left" => (m, m),
            "top-right" => ($"w-tw-{m}", m),
            "center" => ("(w-tw)/2", "(h-th)/2"),
            "bottom-left" => (m, $"h-th-{m}"),
            _ => ($"w-tw-{m}", $"h-th-{m}"),
        };
    }

    private static (string x, string y) OverlayXy(string position, int margin)
    {
        var m = Math.Max(8, margin).ToString();
        return position switch
        {
            "top-left" => (m, m),
            "top-right" => ($"W-w-{m}", m),
            "center" => ("(W-w)/2", "(H-h)/2"),
            "bottom-left" => (m, $"H-h-{m}"),
            _ => ($"W-w-{m}", $"H-h-{m}"),
        };
    }

    private static string EscapeDrawText(string text) =>
        text.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'").Replace("%", "\\%");

    private static string FfmpegColor(string hex, double alpha)
    {
        hex = hex.Trim().TrimStart('#');
        if (!Regex.IsMatch(hex, "^[0-9a-fA-F]{6}$")) hex = "FFFFFF";
        return $"0x{hex.ToUpperInvariant()}@{Math.Clamp(alpha, 0, 1):0.00}";
    }

    private static void RewriteKeyUris(string playlistPath, string? keyName)
    {
        if (!File.Exists(playlistPath) || string.IsNullOrWhiteSpace(keyName)) return;
        var text = File.ReadAllText(playlistPath);
        text = Regex.Replace(text, @"URI=""[^""]+""", $"URI=\"{keyName}\"");
        File.WriteAllText(playlistPath, text);
    }

    private static List<int> NormalizeQualities(int sourceHeight, List<int> requested)
    {
        var ladder = new[] { 1080, 720, 480, 360, 240, 144 };
        return requested
            .Where(q => ladder.Contains(q) && q <= sourceHeight)
            .Distinct()
            .OrderByDescending(q => q)
            .ToList();
    }

    private static (int w, int h) Dim(TaskPayload task, int q)
    {
        if (task.Ffmpeg.Dimensions.TryGetValue(q.ToString(), out var arr) && arr.Length >= 2) return (arr[0], arr[1]);
        return q switch
        {
            144 => (256, 144),
            240 => (426, 240),
            360 => (640, 360),
            480 => (854, 480),
            1080 => (1920, 1080),
            _ => (1280, 720),
        };
    }

    private static int Bitrate(TaskPayload task, int q)
    {
        if (task.Ffmpeg.VideoBitrateKbps.TryGetValue(q.ToString(), out var v)) return v;
        return q switch { 144 => 200, 240 => 350, 360 => 600, 480 => 1000, 1080 => 3500, _ => 1800 };
    }

    private static int AudioBitrate(TaskPayload task, int q)
    {
        if (task.Ffmpeg.AudioBitrateKbps.TryGetValue(q.ToString(), out var v)) return v;
        return q is 1080 or 720 ? 128 : q == 480 ? 112 : 96;
    }

    private static int Crf(TaskPayload task, int q)
    {
        if (task.Ffmpeg.Crf.TryGetValue(q.ToString(), out var v)) return v;
        return q switch { 1080 => 20, 720 => 21, 480 => 23, _ => 24 };
    }

    private static int CapBitrate(int target, int? source)
    {
        if (source is > 0 && target > source.Value) return (int)Math.Floor(source.Value * 0.95);
        return target;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[^max..];
}
