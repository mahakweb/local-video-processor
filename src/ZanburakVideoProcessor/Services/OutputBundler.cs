using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ZanburakVideoProcessor.Models;

namespace ZanburakVideoProcessor.Services;

public static class OutputBundler
{
    public static string CreateZip(string workDir, TaskPayload task, int duration)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["video_id"] = task.VideoId,
            ["outputs"] = task.Outputs,
            ["storage_disk"] = task.StorageDisk,
            ["remote_paths"] = task.RemotePaths,
            ["download_qualities"] = task.DownloadQualities,
            ["stream_qualities"] = task.StreamQualities,
            ["duration"] = duration,
        };
        File.WriteAllText(Path.Combine(workDir, "manifest.json"), JsonSerializer.Serialize(manifest));

        var zipPath = Path.Combine(Path.GetDirectoryName(workDir) ?? workDir, "bundle.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(workDir, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        return zipPath;
    }
}
