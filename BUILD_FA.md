# راهنمای Build و انتشار

## پیش‌نیاز

- .NET SDK 8 (Desktop / Windows)
- باینری‌های `tools/ffmpeg/ffmpeg.exe` و `ffprobe.exe`

اگر SDK روی ماشین Build نیست:

```powershell
winget install Microsoft.DotNet.SDK.8
```

یا SDK portable را از سایت مایکروسافت دانلود کنید.

## ساخت نسخه قابل اجرا

از پوشه `local-video-processor`:

```powershell
dotnet publish src\ZanburakVideoProcessor\ZanburakVideoProcessor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish
```

خروجی در `publish\ZanburakVideoProcessor.exe` است. پوشه `tools\ffmpeg` باید کنار exe کپی شود (csproj در صورت وجود فایل‌ها آن‌ها را کپی می‌کند).

## ثبت پروتکل بعد از Build

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\register-protocol.ps1 -ExePath .\publish\ZanburakVideoProcessor.exe
```

اپ در اولین اجرا هم پروتکل را برای کاربر جاری در Registry ثبت می‌کند.

## تنظیم آدرس Backend

فایل `appsettings.json` کنار exe:

```json
{
  "ApiBaseUrl": "https://api.zanburak.ir/api"
}
```

برای توسعه محلی مثلاً `http://localhost:8000/api`. متغیر محیطی `ZANBURAK_API_BASE_URL` هم خوانده می‌شود.

## انتشار برای مدیران

1. پوشه publish + tools/ffmpeg + LICENSE را زیپ کنید.
2. فایل را روی یک URL واقعی بگذارید.
3. همان URL را در `LOCAL_PROCESSOR_DOWNLOAD_URL` و `NUXT_PUBLIC_LOCAL_PROCESSOR_DOWNLOAD_URL` قرار دهید.
