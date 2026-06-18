# Adminbot

`Adminbot` یک ربات تلگرامی مدیریت فروش، شارژ کیف پول، ساخت و تمدید اکانت‌های `3x-ui` و مدیریت کاربران است. این پروژه با `ASP.NET Core` و `Entity Framework Core` ساخته شده و برای سناریوهای فروش سرویس، پرداخت آنلاین و مدیریت پنل‌های `XUI` طراحی شده است.

## قابلیت‌ها

- ساخت و تمدید اکانت‌های نسخه جدید پنل `3x-ui`
- پشتیبانی از سرویس‌های حجمی و پلن‌های نامحدود با مصرف منصفانه
- ساخت اکانت نامحدود با شروع اعتبار از اولین اتصال
- ذخیره متادیتای کامل سفارش داخل `comment` اکانت پنل
- شارژ کیف پول کاربران از طریق پرداخت آنلاین
- پرداخت ارز دیجیتال با `NOWPayments`
- پرداخت ریالی با `HooshPay`
- دریافت و اعتبارسنجی `IPN` برای پرداخت‌ها
- جلوگیری از شارژ دوباره یک پرداخت تاییدشده
- بررسی دستی وضعیت پرداخت از داخل ربات
- مدیریت موجودی کیف پول کاربران
- مسیرهای مدیریتی برای ادمین‌ها و همکاران
- لاگ فعالیت کاربران و لاگ پرداخت‌ها
- پشتیبانی از ساخت انبوه اکانت
- پشتیبانی از QR Code برای لینک اشتراک

## پرداخت‌ها

### `HooshPay`

پرداخت ریالی `HooshPay` در مسیر شارژ کیف پول فعال است. هنگام ساخت فاکتور، کارمزد درگاه با مقدار زیر از کاربر دریافت می‌شود:

```json
{
  "fee_mode": "buyer"
}
```

مسیر دریافت وب‌هوک:

```text
https://payment.tofanservice.ir/hooshpay-ipn
```

کلیدهای لازم در فایل `Data/configuration.json`:

```json
{
  "hooshPayApiKey": "",
  "hooshPayIpnSecretKey": "",
  "hooshPayBaseUrl": "https://pay.hooshnet.com",
  "hooshPayIpnUrl": "https://payment.tofanservice.ir/hooshpay-ipn",
  "hooshPayReturnUrl": ""
}
```

### `NOWPayments`

پرداخت ارز دیجیتال از طریق `NOWPayments` انجام می‌شود و وضعیت پرداخت از طریق `IPN` یا بررسی دستی داخل ربات قابل پیگیری است.

## دیتابیس‌ها

پروژه از دو دیتابیس جدا استفاده می‌کند:

- `users.db`
  برای وضعیت کاربران ربات، پرداخت‌ها و اطلاعات جریان خرید
- `credentials.db`
  برای موجودی و اطلاعات اصلی کاربران

تغییرات پرداخت `HooshPay` فقط در `users.db` ذخیره می‌شود و دیتابیس `credentials` برای ساختار پرداخت تغییر نمی‌کند.

## تنظیمات مهم

فایل اصلی تنظیمات:

```text
Data/configuration.json
```

تنظیمات مهم:

- `botToken`
- `loggerChannel`
- `backupChannel`
- `xuiV3ApiBaseUrl`
- `xuiV3ApiToken`
- `xuiV3SubLinkBaseUrl`
- `xuiV3ServicePlansPath`
- `nowPaymentApiKey`
- `nowpaymentIpnUrl`
- `hooshPayApiKey`
- `hooshPayIpnSecretKey`
- `hooshPayIpnUrl`

## پلن‌های سرویس

پلن‌های سرویس نسخه ۳ در فایل زیر نگهداری می‌شوند:

```text
Data/xui-v3-service-plans.json
```

در پلن‌های نامحدود:

- حجم قابل انتخاب توسط کاربر نیست
- مقدار مصرف منصفانه از `fairUsageGb` خوانده می‌شود
- تعداد کاربر مجاز از `maxUsers` خوانده می‌شود
- مدت اعتبار از `days` خوانده می‌شود
- اعتبار با مقدار منفی `expiryTime` به پنل ارسال می‌شود تا از اولین اتصال شروع شود

## اجرا

برای build پروژه:

```bash
dotnet build
```

برای اجرای ربات:

```bash
dotnet run
```

## نکات امنیتی

- فایل `Data/configuration.json` شامل secretهاست و نباید در مخزن عمومی منتشر شود.
- کلیدهای `HooshPay`، `NOWPayments` و توکن ربات تلگرام را در محیط امن نگهداری کنید.
- قبل از انتشار عمومی، مسیرهای پرداخت، دامنه‌ها و callbackها را با محیط production خود هماهنگ کنید.
