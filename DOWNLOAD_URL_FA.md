# تنظیم URL دانلود اپ

لینک ساختگی در کد گذاشته نشده است. تا وقتی مقدار خالی باشد، سایت می‌گوید لینک هنوز تنظیم نشده.

## Backend

در `zanburak-backend/.env`:

```
LOCAL_PROCESSOR_DOWNLOAD_URL=https://dl.zanburak.ir/apps/zanburak-video-processor.zip
LOCAL_PROCESSOR_APP_VERSION=1.0.0
LOCAL_PROCESSOR_PROTOCOL=zanburak-video
```

پیش‌فرض در `config/upload.php` از همین env خوانده می‌شود.

## Frontend Nuxt

در محیط Nuxt:

```
NUXT_PUBLIC_LOCAL_PROCESSOR_DOWNLOAD_URL=https://dl.zanburak.ir/apps/zanburak-video-processor.zip
NUXT_PUBLIC_LOCAL_PROCESSOR_APP_VERSION=1.0.0
NUXT_PUBLIC_LOCAL_PROCESSOR_PROTOCOL=zanburak-video
```

تعریف در `nuxt.config.ts` → `runtimeConfig.public`.

URL باید به فایل واقعی نصب/زیپ روی هاست خودتان اشاره کند. پس از آپلود خروجی publish، این مقدار را وارد کنید.
