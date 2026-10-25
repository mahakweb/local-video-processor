# باینری‌های FFmpeg همراه اپ

این پوشه باید شامل فایل‌های زیر باشد تا کاربر نهایی FFmpeg جداگانه نصب نکند:

- `ffmpeg.exe`
- `ffprobe.exe`
- `LICENSE` (متن مجوز GPLv3 بیلد Gyan)
- `README.txt` بیلد اصلی در صورت وجود

## منبع نسخه‌ای که در محیط فعلی بررسی شد

- مسیر سیستم توسعه‌دهنده (فقط برای کپی، نه وابستگی اجرا): `C:\ffmpeg\ffmpeg-8.0-essentials_build\bin\`
- نسخه: FFmpeg 8.0 essentials_build از gyan.dev
- مجوز: GPLv3 (بیلد استاتیک با `--enable-gpl`)
- سورس متناظر: https://ffmpeg.org و https://www.gyan.dev/ffmpeg/builds/

اپ FFmpeg را به‌صورت فرایند جدا اجرا می‌کند و به PATH ویندوز یا `C:\ffmpeg\bin` وابسته نیست.

اگر این پوشه خالی است، قبل از انتشار این دو دستور را اجرا کنید (کپی، نه جابه‌جایی):

```powershell
Copy-Item "C:\ffmpeg\ffmpeg-8.0-essentials_build\bin\ffmpeg.exe" ".\ffmpeg.exe"
Copy-Item "C:\ffmpeg\ffmpeg-8.0-essentials_build\bin\ffprobe.exe" ".\ffprobe.exe"
Copy-Item "C:\ffmpeg\ffmpeg-8.0-essentials_build\LICENSE" ".\LICENSE"
```
