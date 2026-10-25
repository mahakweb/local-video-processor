using System.Text.Json.Serialization;

namespace ZanburakVideoProcessor.Models;

public sealed class TaskPayload
{
    [JsonPropertyName("public_id")] public string PublicId { get; set; } = "";
    [JsonPropertyName("video_id")] public int VideoId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("course_title")] public string CourseTitle { get; set; } = "";
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("api_base_url")] public string ApiBaseUrl { get; set; } = "";
    [JsonPropertyName("source_url")] public string SourceUrl { get; set; } = "";
    [JsonPropertyName("bundle_url")] public string BundleUrl { get; set; } = "";
    [JsonPropertyName("bundle_complete_url")] public string BundleCompleteUrl { get; set; } = "";
    [JsonPropertyName("progress_url")] public string ProgressUrl { get; set; } = "";
    [JsonPropertyName("fail_url")] public string FailUrl { get; set; } = "";
    [JsonPropertyName("storage_disk")] public string StorageDisk { get; set; } = "dl";
    [JsonPropertyName("outputs")] public List<string> Outputs { get; set; } = new();
    [JsonPropertyName("stream_qualities")] public List<int> StreamQualities { get; set; } = new();
    [JsonPropertyName("download_qualities")] public List<int> DownloadQualities { get; set; } = new();
    [JsonPropertyName("remote_paths")] public Dictionary<string, string> RemotePaths { get; set; } = new();
    [JsonPropertyName("raw")] public RawInfo Raw { get; set; } = new();
    [JsonPropertyName("watermark")] public WatermarkInfo Watermark { get; set; } = new();
    [JsonPropertyName("watermark_image_url")] public string? WatermarkImageUrl { get; set; }
    [JsonPropertyName("hls_segment_length")] public int HlsSegmentLength { get; set; } = 8;
    [JsonPropertyName("ffmpeg")] public FfmpegProfile Ffmpeg { get; set; } = new();
}

public sealed class RawInfo
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("extensions")] public List<string> Extensions { get; set; } = new();
}

public sealed class WatermarkInfo
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("position")] public string Position { get; set; } = "bottom-right";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.7;
    [JsonPropertyName("font_size")] public int FontSize { get; set; } = 36;
    [JsonPropertyName("image_scale")] public double ImageScale { get; set; } = 0.16;
    [JsonPropertyName("font")] public string Font { get; set; } = "tahoma";
    [JsonPropertyName("font_color")] public string FontColor { get; set; } = "#FFFFFF";
    [JsonPropertyName("bg_enabled")] public bool BgEnabled { get; set; }
    [JsonPropertyName("bg_color")] public string BgColor { get; set; } = "#000000";
    [JsonPropertyName("bg_opacity")] public double BgOpacity { get; set; } = 0.45;
    [JsonPropertyName("bg_padding")] public int BgPadding { get; set; } = 10;
    [JsonPropertyName("bg_radius")] public int BgRadius { get; set; } = 8;
    [JsonPropertyName("image_radius")] public int ImageRadius { get; set; }
    [JsonPropertyName("border_enabled")] public bool BorderEnabled { get; set; } = true;
    [JsonPropertyName("border_color")] public string BorderColor { get; set; } = "#000000";
    [JsonPropertyName("border_width")] public int BorderWidth { get; set; } = 2;
}

public sealed class FfmpegProfile
{
    [JsonPropertyName("quality_ladder")] public List<int> QualityLadder { get; set; } = new() { 1080, 720, 480, 360, 240, 144 };
    [JsonPropertyName("dimensions")] public Dictionary<string, int[]> Dimensions { get; set; } = new();
    [JsonPropertyName("video_bitrate_kbps")] public Dictionary<string, int> VideoBitrateKbps { get; set; } = new();
    [JsonPropertyName("audio_bitrate_kbps")] public Dictionary<string, int> AudioBitrateKbps { get; set; } = new();
    [JsonPropertyName("crf")] public Dictionary<string, int> Crf { get; set; } = new();
    [JsonPropertyName("x264")] public Dictionary<string, object>? X264 { get; set; }
    [JsonPropertyName("nvenc")] public Dictionary<string, object>? Nvenc { get; set; }
    [JsonPropertyName("encoder_preference")] public List<string> EncoderPreference { get; set; } = new() { "h264_nvenc", "libx264" };
}
