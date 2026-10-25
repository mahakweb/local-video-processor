using System.IO;
using System.Windows;
using ZanburakVideoProcessor.Models;
using ZanburakVideoProcessor.Services;

namespace ZanburakVideoProcessor;

public partial class MainWindow : Window
{
    private readonly string? _taskId;
    private readonly string? _apiHint;
    private readonly FfmpegLocator _ffmpeg = new();
    private CancellationTokenSource? _cts;
    private TaskPayload? _task;
    private bool _busy;

    public MainWindow(string? taskId, string? apiHint = null)
    {
        InitializeComponent();
        _taskId = taskId;
        _apiHint = apiHint;
        Loaded += async (_, _) => await StartAsync();
    }

    private async void OnRetry(object sender, RoutedEventArgs e) => await StartAsync();

    private async Task StartAsync()
    {
        if (_busy) return;
        RetryButton.Visibility = Visibility.Collapsed;
        FinishedCard.Visibility = Visibility.Collapsed;
        SetPercent(OverallBar, OverallPercent, 0, false);
        SetPercent(DownloadBar, DownloadPercent, 0, false);
        SetPercent(EncodeBar, EncodePercent, 0, false);
        SetPercent(UploadBar, UploadPercent, 0, false);

        SetStatus("آماده‌سازی", "در حال بررسی FFmpeg همراه اپ…");
        try
        {
            _ffmpeg.EnsureReady();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(_taskId))
        {
            SetStatus("آماده", "این اپ باید از سایت زنبورک باز شود.");
            MessageText.Text = "از صفحه مدیریت دوره، «پردازش روی سیستم شما» را بزنید. انتخاب فایل در این اپ لازم نیست؛ همان ویدیوی آپلودشده روی سرور پردازش می‌شود.";
            return;
        }

        SetStatus("دریافت اطلاعات عملیات", "در حال ارتباط با سرور اصلی…");
        try
        {
            using var client = new BackendClient(_apiHint);
            _task = await client.ClaimAsync(_taskId, CancellationToken.None);
            ApplyTaskMeta(_task);
            await client.ReportProgressAsync(_task, "claimed", 0, 0);
            await RunPipelineAsync();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void ApplyTaskMeta(TaskPayload task)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(task.Title) ? "پردازش ویدیو" : task.Title;
        var fileName = string.IsNullOrWhiteSpace(task.Raw.Filename) ? "ویدیوی آپلودشده" : task.Raw.Filename;
        var duration = task.Raw.Duration > 0 ? $" — مدت {FormatDuration(task.Raw.Duration)}" : "";
        SourceText.Text = fileName + duration;

        var parts = new List<string>();
        if (task.StreamQualities.Count > 0)
        {
            parts.Add("استریم: " + string.Join("، ", task.StreamQualities.Select(q => q + "p")));
        }
        if (task.DownloadQualities.Count > 0)
        {
            parts.Add("دانلود: " + string.Join("، ", task.DownloadQualities.Select(q => q + "p")));
        }
        if (task.Outputs.Count > 0)
        {
            parts.Add("خروجی: " + string.Join("، ", task.Outputs.Select(LocalizeOutput)));
        }
        parts.Add(DescribeWatermark(task.Watermark));
        MetaText.Text = string.Join("  ·  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private async Task RunPipelineAsync()
    {
        if (_task == null) return;
        _busy = true;
        RetryButton.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var workRoot = Path.Combine(Path.GetTempPath(), "zanburak-local", _task.PublicId);
        Directory.CreateDirectory(workRoot);
        var outDir = Path.Combine(workRoot, "out");
        Directory.CreateDirectory(outDir);
        var ext = Path.GetExtension(_task.Raw.Filename);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
        var inputPath = Path.Combine(workRoot, "source" + ext);

        using var client = new BackendClient(_apiHint);
        var encoder = new FfmpegEncoder(_ffmpeg);
        try
        {
            SetStatus("دانلود ویدیو", "در حال دریافت ویدیوی آپلودشده از سرور…");
            await client.ReportProgressAsync(_task, "downloading", 0, 0, ct);
            var downloadProgress = new Progress<(long received, long? total)>(tuple =>
            {
                if (tuple.total is > 0)
                {
                    SetPercent(DownloadBar, DownloadPercent, tuple.received * 100.0 / tuple.total.Value, false);
                }
                else
                {
                    DownloadBar.IsIndeterminate = true;
                    DownloadPercent.Text = FormatBytes(tuple.received);
                }
            });
            await client.DownloadSourceAsync(_task, inputPath, downloadProgress, ct);
            SetPercent(DownloadBar, DownloadPercent, 100, false);
            await client.ReportProgressAsync(_task, "encoding", 1, 0, ct);

            string? wmImage = null;
            if (_task.Watermark.Enabled && _task.Watermark.Type == "image" && !string.IsNullOrWhiteSpace(_task.WatermarkImageUrl))
            {
                wmImage = Path.Combine(workRoot, "watermark.bin");
                await client.DownloadWatermarkAsync(_task, wmImage, ct);
            }

            SetStatus("پردازش ویدیو", "FFmpeg در حال تبدیل ویدیو است…");
            await encoder.DetectEncoderAsync(ct);
            var probe = encoder.Probe(inputPath);
            EncodeBar.IsIndeterminate = probe.Duration <= 0;
            var lastPing = DateTime.MinValue;
            var encodeProgress = new Progress<double>(p =>
            {
                var encodePercent = Math.Clamp(p * 100, 0, 100);
                SetPercent(EncodeBar, EncodePercent, encodePercent, false);
                // Mirror the server's overall bar (encode 80%, bundle upload 20%)
                // so the app and the site never show different numbers.
                SetPercent(OverallBar, OverallPercent, encodePercent * 0.8, false);
                if (DateTime.UtcNow - lastPing < TimeSpan.FromSeconds(2)) return;
                lastPing = DateTime.UtcNow;
                _ = client.ReportProgressAsync(_task, "encoding", (int)encodePercent, 0);
            });
            await encoder.EncodeAsync(inputPath, outDir, _task, probe, wmImage, encodeProgress, ct);
            SetPercent(EncodeBar, EncodePercent, 100, false);
            SetPercent(OverallBar, OverallPercent, 80, false);
            await client.ReportProgressAsync(_task, "preparing_files", 100, 0, ct);

            SetStatus("آماده‌سازی فایل‌ها", "در حال بسته‌بندی خروجی…");
            var zip = OutputBundler.CreateZip(outDir, _task, probe.Duration > 0 ? probe.Duration : _task.Raw.Duration);
            SetStatus("آپلود خروجی", "ارسال فایل‌ها به سرور اصلی…");
            await client.ReportProgressAsync(_task, "uploading", 100, 1, ct);
            var uploadProgress = new Progress<(long sent, long total)>(tuple =>
            {
                if (tuple.total <= 0) return;
                var uploadPercent = tuple.sent * 100.0 / tuple.total;
                SetPercent(UploadBar, UploadPercent, uploadPercent, false);
                SetPercent(OverallBar, OverallPercent, 80 + (uploadPercent * 0.2), false);
            });
            // UploadBundleAsync also runs the finalize step on the backend, which
            // publishes the outputs and marks the video as processed.
            await client.UploadBundleAsync(_task, zip, uploadProgress, ct);
            SetPercent(UploadBar, UploadPercent, 100, false);
            SetPercent(OverallBar, OverallPercent, 100, false);
            await client.ReportCompletedAsync(_task, ct);

            SetStatus("پایان پردازش", "پردازش و آپلود با موفقیت انجام شد.");
            MessageText.Text =
                $"خروجی‌ها روی سرور ثبت شدند. ویدیو «{GetTitleOrFallback(_task)}» در سایت به وضعیت «پردازش‌شده» رفت و می‌توانید صفحه جلسه را باز کنید. " +
                "این پنجره را می‌توانید ببندید.";
            FinishedText.Visibility = Visibility.Visible;
            FinishedCard.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            await client.FailAsync(_task, ex.Message, CancellationToken.None);
            Fail(ex.Message);
        }
        finally
        {
            _busy = false;
            try
            {
                if (Directory.Exists(workRoot))
                {
                    Directory.Delete(workRoot, true);
                }
            }
            catch
            {
                // temp cleanup is best-effort
            }
        }
    }

    private void SetStatus(string status, string message)
    {
        StatusText.Text = status;
        MessageText.Text = message;
    }

    private void Fail(string message)
    {
        SetStatus("خطا", message);
        FinishedCard.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        DownloadBar.IsIndeterminate = false;
        EncodeBar.IsIndeterminate = false;
        UploadBar.IsIndeterminate = false;
    }

    private static void SetPercent(System.Windows.Controls.ProgressBar bar, System.Windows.Controls.TextBlock label, double value, bool indeterminate)
    {
        bar.IsIndeterminate = indeterminate;
        if (!indeterminate)
        {
            bar.Value = Math.Clamp(value, 0, 100);
            label.Text = $"{Math.Round(Math.Clamp(value, 0, 100)):0}٪";
        }
    }

    private static string DescribeWatermark(WatermarkInfo wm)
    {
        if (!wm.Enabled)
        {
            return "واترمارک: خاموش";
        }

        var position = wm.Position switch
        {
            "top-left" => "بالا چپ",
            "top-right" => "بالا راست",
            "center" => "وسط",
            "bottom-left" => "پایین چپ",
            "bottom-right" => "پایین راست",
            _ => wm.Position,
        };
        var opacity = Math.Round(wm.Opacity * 100);

        if (string.Equals(wm.Type, "image", StringComparison.OrdinalIgnoreCase))
        {
            return $"واترمارک: تصویر — {position} · اندازه {Math.Round(wm.ImageScale * 100)}٪ عرض · شفافیت {opacity}٪";
        }

        var text = string.IsNullOrWhiteSpace(wm.Text) ? "متن" : "«" + wm.Text + "»";
        var parts = new List<string> { $"واترمارک: متن {text}" };
        if (!string.IsNullOrWhiteSpace(wm.Font)) parts.Add("فونت " + wm.Font);
        if (!string.IsNullOrWhiteSpace(wm.FontColor)) parts.Add(wm.FontColor);
        parts.Add($"{wm.FontSize}px");
        if (wm.BorderEnabled && wm.BorderWidth > 0) parts.Add($"حاشیه {wm.BorderWidth}px");
        if (wm.BgEnabled) parts.Add($"پس‌زمینه {wm.BgColor} {Math.Round(wm.BgOpacity * 100)}٪");
        parts.Add(position);
        parts.Add($"شفافیت {opacity}٪");
        return string.Join(" · ", parts);
    }

    private static string GetTitleOrFallback(TaskPayload task) =>
        string.IsNullOrWhiteSpace(task.Title) ? "ویدیو" : task.Title;

    private static string LocalizeOutput(string output) => output switch
    {
        "stream" => "استریم",
        "download" => "دانلود",
        "trailer" => "تریلر",
        _ => output,
    };

    private static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.0} MB";
    }
}
