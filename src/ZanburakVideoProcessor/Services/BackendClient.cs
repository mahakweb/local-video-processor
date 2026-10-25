using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZanburakVideoProcessor.Models;

namespace ZanburakVideoProcessor.Services;

public sealed class BackendClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly string? _apiHint;
    /// <summary>Serializes progress pings so they can never arrive out of order.</summary>
    private readonly SemaphoreSlim _progressGate = new(1, 1);

    public BackendClient(string? apiHint = null)
    {
        _apiHint = apiHint;
    }

    public async Task<TaskPayload> ClaimAsync(string publicId, CancellationToken ct)
    {
        var tried = new List<string>();
        Exception? last = null;
        foreach (var api in CandidateApis())
        {
            var url = $"{api}/local-processor/tasks/{publicId}/claim";
            tried.Add(url);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var res = await _http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    last = new InvalidOperationException(ExtractMessage(body, $"دریافت اطلاعات عملیات ناموفق بود ({(int)res.StatusCode})."));
                    continue;
                }

                var payload = JsonSerializer.Deserialize<TaskPayload>(body, JsonOptions)
                    ?? throw new InvalidOperationException("پاسخ Backend نامعتبر است.");
                if (string.IsNullOrWhiteSpace(payload.ApiBaseUrl))
                {
                    payload.ApiBaseUrl = api;
                }
                if (string.IsNullOrWhiteSpace(payload.SourceUrl))
                {
                    payload.SourceUrl = api + "/local-processor/source";
                }
                if (string.IsNullOrWhiteSpace(payload.BundleUrl))
                {
                    payload.BundleUrl = api + "/local-processor/bundle";
                }
                if (string.IsNullOrWhiteSpace(payload.BundleCompleteUrl))
                {
                    payload.BundleCompleteUrl = api + "/local-processor/bundle/complete";
                }

                return payload;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        var detail = last?.Message ?? "ارتباط با Backend برقرار نشد.";
        throw new InvalidOperationException(
            detail + " آدرس‌های تلاش‌شده: " + string.Join(" | ", tried));
    }

    public async Task ReportProgressAsync(TaskPayload task, string phase, int? encode = null, int? upload = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task.ProgressUrl))
        {
            return;
        }

        var data = new Dictionary<string, object?> { ["phase"] = phase };
        if (encode.HasValue) data["encode_progress"] = encode.Value;
        if (upload.HasValue) data["upload_progress"] = upload.Value;
        // Wait (bounded) for the previous ping so an older phase can never be
        // applied after a newer one and make the bar jump backwards.
        var acquired = false;
        try
        {
            acquired = await _progressGate.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, task.ProgressUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", task.AccessToken);
            using var res = await _http.SendAsync(req, ct);
            _ = res.StatusCode;
        }
        catch
        {
            // progress is best-effort
        }
        finally
        {
            if (acquired) _progressGate.Release();
        }
    }

    /// <summary>
    /// Reports the terminal "completed" phase and returns true only when the backend
    /// accepted it. Callers must not show a success state when this is false.
    /// </summary>
    public async Task<bool> ReportCompletedAsync(TaskPayload task, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task.ProgressUrl))
        {
            return false;
        }

        var data = new Dictionary<string, object?>
        {
            ["phase"] = "completed",
            ["encode_progress"] = 100,
            ["upload_progress"] = 100,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, task.ProgressUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", task.AccessToken);
        var acquired = false;
        try
        {
            acquired = await _progressGate.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            return res.IsSuccessStatusCode && body.Contains("completed", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (acquired) _progressGate.Release();
        }
    }

    public async Task FailAsync(TaskPayload task, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task.FailUrl))
        {
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, task.FailUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error_message = message }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", task.AccessToken);
        try
        {
            using var res = await _http.SendAsync(req, ct);
            _ = res.StatusCode;
        }
        catch
        {
            // ignore
        }
    }

    public async Task DownloadWatermarkAsync(TaskPayload task, string destPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.WatermarkImageUrl))
        {
            return;
        }

        using var res = await _http.GetAsync(task.WatermarkImageUrl, ct);
        res.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await res.Content.CopyToAsync(fs, ct);
    }

    public async Task DownloadSourceAsync(
        TaskPayload task,
        string destPath,
        IProgress<(long received, long? total)> progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(task.SourceUrl))
        {
            throw new InvalidOperationException("آدرس دانلود ویدیوی منبع از Backend نیامده است.");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, task.SourceUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", task.AccessToken);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var bodyOnError = res.IsSuccessStatusCode ? null : await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractMessage(bodyOnError ?? "", "دانلود ویدیوی آپلودشده از سرور ناموفق بود."));
        }

        var total = res.Content.Headers.ContentLength;
        await using var input = await res.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destPath);
        var buffer = new byte[1024 * 256];
        long received = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress.Report((received, total));
        }

        if (received < 1)
        {
            throw new InvalidOperationException("فایل منبع خالی دریافت شد.");
        }
    }

    public async Task UploadBundleAsync(
        TaskPayload task,
        string zipPath,
        IProgress<(long sent, long total)> progress,
        CancellationToken ct)
    {
        const int chunkSize = 8 * 1024 * 1024;
        var info = new FileInfo(zipPath);
        var totalChunks = (int)Math.Ceiling(info.Length / (double)chunkSize);
        long sent = 0;

        await using var stream = File.OpenRead(zipPath);
        var buffer = new byte[chunkSize];
        for (var i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), ct);
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(buffer, 0, read), "file", $"chunk-{i}.bin");
            content.Add(new StringContent(i.ToString()), "chunkIndex");
            content.Add(new StringContent(totalChunks.ToString()), "totalChunks");
            using var req = new HttpRequestMessage(HttpMethod.Post, task.BundleUrl) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", task.AccessToken);
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtractMessage(body, "آپلود خروجی ناموفق بود."));
            }

            sent += read;
            progress.Report((sent, info.Length));
        }

        using var complete = new MultipartFormDataContent();
        complete.Add(new StringContent(totalChunks.ToString()), "totalChunks");
        using var doneReq = new HttpRequestMessage(HttpMethod.Post, task.BundleCompleteUrl) { Content = complete };
        doneReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", task.AccessToken);
        using var doneRes = await _http.SendAsync(doneReq, ct);
        var doneBody = await doneRes.Content.ReadAsStringAsync(ct);
        if (!doneRes.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractMessage(doneBody, "ثبت تکمیل آپلود ناموفق بود."));
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _progressGate.Dispose();
    }

    private IEnumerable<string> CandidateApis()
    {
        var list = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var api = value.Trim().TrimEnd('/');
            if (!api.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            {
                api += "/api";
            }

            if (!list.Exists(x => string.Equals(x, api, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(api);
            }
        }

        Add(_apiHint);
        Add(Environment.GetEnvironmentVariable("ZANBURAK_API_BASE_URL"));
        Add(ReadAppSettings());
        Add("http://localhost:8000/api");
        Add("https://api.zanburak.ir/api");
        return list;
    }

    private static string? ReadAppSettings()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("ApiBaseUrl", out var url))
            {
                return url.GetString();
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    private static string ExtractMessage(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
            {
                return msg.GetString() ?? fallback;
            }
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                return err.GetString() ?? fallback;
            }
        }
        catch
        {
            // ignore
        }

        return fallback;
    }
}
