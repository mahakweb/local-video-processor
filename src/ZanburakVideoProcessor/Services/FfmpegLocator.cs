using System.Diagnostics;
using System.IO;

namespace ZanburakVideoProcessor.Services;

public sealed class FfmpegLocator
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public FfmpegLocator()
    {
        var dir = ResolveToolsDirectory();
        FfmpegPath = Path.Combine(dir, "ffmpeg.exe");
        FfprobePath = Path.Combine(dir, "ffprobe.exe");
    }

    public void EnsureReady()
    {
        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
        {
            throw new InvalidOperationException(
                "باینری FFmpeg همراه اپ پیدا نشد. پوشه tools/ffmpeg باید ffmpeg.exe و ffprobe.exe داشته باشد.");
        }

        ProbeVersion(FfmpegPath);
        ProbeVersion(FfprobePath);
    }

    public string VersionText()
    {
        try
        {
            return ProbeVersion(FfmpegPath);
        }
        catch
        {
            return "نامشخص";
        }
    }

    private static string ProbeVersion(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "-version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("اجرای FFmpeg ممکن نشد.");
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(8000);
        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("FFmpeg قابل اجرا نیست.");
        }

        var line = output.Split('\n').FirstOrDefault() ?? path;
        return line.Trim();
    }

    private static string ResolveToolsDirectory()
    {
        var bases = new[]
        {
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, ".."),
            Path.Combine(AppContext.BaseDirectory, "..", ".."),
            Path.Combine(AppContext.BaseDirectory, "..", "..", ".."),
        };

        foreach (var root in bases)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, "tools", "ffmpeg"));
            if (File.Exists(Path.Combine(candidate, "ffmpeg.exe")))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"));
    }
}
