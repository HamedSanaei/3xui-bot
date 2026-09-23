# اطلس‌پی — راهنمای فنی اتصال به درگاه پرداخت

ویژه‌ی برنامه‌نویسان فروشگاه‌های همکار — اتصال ربات یا سایت به مینی‌اپ اطلس‌پی

**Base URL:** `https://api.atlaspay.space/api/v1`

---

## ۱. معرفی

اطلس‌پی یه درگاه پرداخت کارت‌به‌کارت با تایید خودکار پیامکی‌ه. مشتری شما ریال واریز می‌کنه و معادلش به‌صورت TRX (ترون) به کیف‌پول شما اضافه می‌شه. کل تجربه‌ی پرداخت مشتری از طریق یه مینی‌اپ داخل تلگرام انجام می‌شه؛ کاری که این سند توضیح می‌ده، اتصال بک‌اند شما (ربات یا سایت) به API اطلس‌پیه تا بتونید سفارش بسازید و از نتیجه‌ش باخبر بشید.

> **ℹ️ نکته:** این سند تنها مرجعی است که برای اتصال کامل لازم دارید — شامل توضیح جریان کار، مرجع کامل Endpoint ها و یک نمونه‌ی پیاده‌سازی کامل. نیازی به فایل یا راهنمای دیگری نیست.

### ۱.۱ مفاهیم پایه

| کلید | مقدار |
|---|---|
| Base URL | `https://api.atlaspay.space/api/v1` |
| احراز هویت | هدر `X-API-Key` در همه‌ی درخواست‌ها |
| فرمت پاسخ | JSON |
| واحد مبلغ سفارش | تومان (عدد صحیح) |
| واحد موجودی/کیف‌پول | TRX |
| پروتکل | فقط HTTPS — درخواست HTTP ساده پذیرفته نمی‌شود |

> **⚠️ نکته‌ی مهم:** کلید API اختصاصی شماست. آن را فقط در کد سمت سرور نگه دارید — هرگز در کد سمت کاربر (مرورگر/اپ موبایل) یا مخزن عمومی گیت‌هاب قرار ندهید.

---

## ۲. جریان یک تراکنش

مراحل از لحظه‌ای که مشتری درخواست شارژ می‌ده تا لحظه‌ای که TRX به حساب شما می‌رسه:

1. مشتری توی سایت یا ربات شما درخواست شارژ می‌ده.
2. شما با `POST /orders` سفارش رو به ما معرفی می‌کنید (مبلغ به تومان + آیدی مشتری در صورت وجود)؛ در پاسخ یه لینک (`customerStartLink`) می‌گیرید.
3. مشتری روی لینک کلیک می‌کنه؛ مینی‌اپ اطلس‌پی داخل تلگرام باز می‌شه و مبلغ دقیق + شماره‌کارت مقصد رو نشون می‌ده.
4. مشتری دقیقاً همون مبلغ رو واریز و رسیدش رو آپلود می‌کنه (در بانک‌های پشتیبانی‌شده، تایید کاملاً خودکار و بدون نیاز به رسیده).
5. سفارش تایید می‌شه — خودکار طی چند ثانیه، یا دستی توسط ادمین در صورت نیاز به بررسی.
6. شما با استعلام دوره‌ای (`GET /orders/{id}`) یا در لحظه‌ی بازگشت مشتری (`POST /orders/{id}/verify`) از تایید باخبر می‌شید و سرویس رو تحویل می‌دید.

> **⚠️ نکته‌ی مهم:** مبلغ نهایی سفارش (`totalAmountToman`) با مبلغ درخواستی شما (`baseAmountToman`) یکی نیست — یه رقم آخر یکتا برای تشخیص دقیق پیامک بانکی بهش اضافه می‌شه. همیشه دقیقاً `totalAmountToman` رو به مشتری نشون بدید، نه `baseAmountToman`.

---

### ۲.۱ ارسال پیام تایید سفارش به مشتری

**کجای کد باید این پیام رو بفرستید**

همون‌جایی که تابع `createOrder()` رو صدا می‌زنید و جواب رو می‌گیرید — دقیقاً بلافاصله بعدش. متن پیام رو با دکمه‌ی پرداخت یکجا (یه پیام واحد) بفرستید.

```javascript
const order = await client.createOrder({
  merchantOrderRef: 'ORDER-1402',
  baseAmountToman: 250000,
  customerTelegramId: 123456789,
});

await ctx.reply(MESSAGE_TEXT_WITH_VALUES_FILLED_IN, {
  reply_markup: {
    inline_keyboard: [[{ text: '💳 پرداخت', url: order.customerStartLink }]],
  },
});
```

**پاسخی که از `createOrder()` می‌گیرید**

```json
{
  "orderId": 66,
  "trackingCode": "5c23c12c9fa0c8b3",
  "totalAmountToman": 250000,
  "cardNumberMasked": "6037****3165",
  "paymentDeadlineAt": "2026-08-05T21:58:56.329Z",
  "customerStartLink": "https://t.me/atlaspaybot/pay?startapp=order_66_5c23c12c9fa0c8b3d93367517c77a5af5b4f4c90511e172b"
}
```

> **⚠️ نکته‌ی مهم:** برای نمایش شماره‌ی پیگیری به مشتری، از `trackingCode` استفاده کنید، نه `orderId`. `trackingCode` یه کد تصادفی و امنه؛ `orderId` یه شماره‌ی ترتیبیه که اگه به مشتری نشون داده بشه، حجم واقعی سفارش‌های سیستم رو لو می‌ده.

**متن پیام**

دو تا جای خالی داره که باید با مقادیر واقعیِ همون `order` پر بشه:

| جای‌خالی | مقدار |
|---|---|
| `{{totalAmountToman}}` | `order.totalAmountToman` |
| `{{trackingCode}}` | `order.trackingCode` |

```
⚠️ پیش از پرداخت، لطفاً موارد زیر را با دقت مطالعه فرمایید:

🔹 مبلغ را دقیقاً مطابق عدد ذکرشده در پایین واریز نمایید. واریز مبلغ رند یا
متفاوت با مبلغ ذکرشده، ممکن است موجب تأخیر در تأیید شود.
🔹 پرداخت باید در بازه‌ی زمانی اعلام‌شده انجام شود؛ در غیر این صورت سفارش
منقضی خواهد شد.
🔹 مسئولیت واریز به شماره‌کارت یا مبلغ نادرست، بر عهده‌ی پرداخت‌کننده است.

💰 مبلغ قابل پرداخت: {{totalAmountToman}} تومان
این مبلغ شامل کلیه‌ی کارمزدها است و نیازی به محاسبه‌ی جداگانه نیست.

⏱ مهلت پرداخت: ۲۰ دقیقه از همین لحظه

🌐 برای ادامه، از دکمه‌ی زیر وارد صفحه‌ی پرداخت شوید. شماره‌کارت مقصد واریز
همانجا نمایش داده خواهد شد.

✅ پس از واریز، رسید پرداخت را از همان صفحه بارگذاری نمایید و منتظر تأیید
بمانید. در صورت بروز هرگونه مشکل، مراتب را به‌همراه تصویر رسید به پشتیبانی
اطلاع دهید.

🔖 شماره‌ی پیگیری سفارش: {{trackingCode}}
```

**تجربه‌ی مشتری موقع کلیک روی دکمه**

با کلیک روی دکمه‌ی پرداخت، صفحه‌ی پرداخت مستقیم داخل همون چتِ ربات شما برای مشتری باز می‌شه — بدون نیاز به خروج از چت شما یا هیچ مرحله‌ی اضافه‌ای. کل فرآیند (دیدن مبلغ، شماره‌کارت، آپلود رسید، دریافت تأیید) در همون صفحه انجام می‌شه.

---

## ۳. مرجع کامل API

### ۳.۱ ساخت سفارش

**POST** `/orders`

برای شروع یه تراکنش جدید این Endpoint رو صدا بزنید. یه لینک برمی‌گردونه که با کلیک روش، مینی‌اپ پرداخت داخل تلگرام برای مشتری باز می‌شه.

| پارامتر | نوع | توضیح | الزامی |
|---|---|---|---|
| `merchantOrderRef` | string | شناسه‌ی یکتای سفارش در سیستم خودتون | الزامی |
| `baseAmountToman` | integer | مبلغ پایه‌ی سفارش به تومان | الزامی |
| `customerTelegramId` | integer | آیدی عددی تلگرام مشتری — اگر بدید، سفارش مستقیم به همون مشتری متصل می‌شه | اختیاری |

نمونه‌ی درخواست:

```bash
curl -X POST https://api.atlaspay.space/api/v1/orders \
  -H "X-API-Key: your_api_key" \
  -H "Content-Type: application/json" \
  -d '{
    "merchantOrderRef": "ORDER-1402",
    "baseAmountToman": 250000,
    "customerTelegramId": 123456789
  }'
```

نمونه‌ی پاسخ:

```json
{
  "orderId": 58,
  "totalAmountToman": 259739,
  "cardNumberMasked": "6037********3165",
  "paymentDeadlineAt": "2026-08-02T21:58:56.329Z",
  "customerStartLink": "https://t.me/atlaspaybot?start=order_58_31f2..."
}
```

> مهلت پرداخت (`paymentDeadlineAt`) همیشه ۲۰ دقیقه بعد از ساخت سفارشه. اگه مشتری تا اون موقع واریز نکنه، سفارش خودکار «منقضی» می‌شه.

---

### ۳.۲ استعلام وضعیت سفارش

**GET** `/orders/{id}`

وضعیت فعلی یه سفارش خاص رو برمی‌گردونه. برای پولینگ دوره‌ای (مثلاً هر ۱۵ تا ۳۰ ثانیه) روی سفارش‌های در انتظار استفاده کنید.

```bash
curl https://api.atlaspay.space/api/v1/orders/58 \
  -H "X-API-Key: your_api_key"
```

نمونه‌ی پاسخ:

```json
{
  "success": true,
  "id": 58,
  "trackingCode": "5c23c12c9fa0c8b3",
  "merchantOrderRef": "ORDER-1402",
  "status": "awaiting_payment",
  "totalAmountToman": 259739,
  "actualReceivedAmountToman": null,
  "requiresManualDelivery": false,
  "createdAt": "2026-08-02T21:38:56.335Z"
}
```

> **⚠️ فیلد `requiresManualDelivery`:** قبل از تحویل سرویس، همیشه این فیلد رو هم چک کنید — نه فقط `status`. توضیح کامل در بخش [۴.۱](#۴۱-کسری-واریز-و-فیلد-requiresmanualdelivery).

---

### ۳.۳ تایید نهایی (Verify)

**POST** `/orders/{id}/verify`

برای لحظه‌ای که مشتری به سایت شما برمی‌گرده مناسبه — دقیقاً همون داده‌ی استعلام وضعیت رو برمی‌گردونه، به‌علاوه‌ی فیلد `paid` که مستقیم `true` یا `false` هست (نیازی نیست خودتون اسم دقیق وضعیت‌های موفق رو بدونید).

```bash
curl -X POST https://api.atlaspay.space/api/v1/orders/58/verify \
  -H "X-API-Key: your_api_key"
```

نمونه‌ی پاسخ:

```json
{
  "success": true,
  "id": 58,
  "status": "confirmed",
  "paid": true,
  "totalAmountToman": 259739,
  "requiresManualDelivery": false,
  "...": "..."
}
```

> **⚠️ `paid: true` به‌تنهایی کافی نیست.** قبل از تحویل سرویس حتماً `requiresManualDelivery` رو هم چک کنید — بخش [۴.۱](#۴۱-کسری-واریز-و-فیلد-requiresmanualdelivery) رو ببینید.

---

### ۳.۴ لغو سفارش

**POST** `/orders/{id}/cancel`

یه سفارش رو لغو می‌کنه — فقط تا زمانی که هیچ سیگنال پرداختی (نه پیامک بانکی، نه رسید) براش دریافت نشده باشه.

```bash
curl -X POST https://api.atlaspay.space/api/v1/orders/58/cancel \
  -H "X-API-Key: your_api_key"
```

موفق:

```json
{ "success": true, "status": "cancelled" }
```

ناموفق (دیگه قابل‌لغو نیست):

```json
{
  "success": false,
  "message": "این سفارش دیگه در وضعیت قابل‌لغو نیست..."
}
```

---

### ۳.۵ موجودی فعلی

**GET** `/balance`

موجودی فعلی قابل‌برداشت شما (TRX) رو برمی‌گردونه.

```bash
curl https://api.atlaspay.space/api/v1/balance \
  -H "X-API-Key: your_api_key"
```

```json
{ "success": true, "data": { "availableTrx": 12.4587 } }
```

---

### ۳.۶ اطلاعات حساب

**GET** `/account`

اطلاعات پایه‌ی حساب فروشگاهی شما رو برمی‌گردونه.

```bash
curl https://api.atlaspay.space/api/v1/account \
  -H "X-API-Key: your_api_key"
```

```json
{
  "success": true,
  "data": { "id": 1, "name": "فروشگاه شما", "status": "active", "markupPct": null }
}
```

---

### ۳.۷ نمایش مستقیم اطلاعات پرداخت (اختیاری)

> **ℹ️ این یه قابلیت کاملاً اختیاریه** که فقط بعد از درخواست شما و تایید مالک اطلس‌پی برای فروشگاهتون فعال می‌شه — برای فعال‌سازی، از داخل ربات تلگرام اطلس‌پی دکمه‌ی «🆘 پشتیبانی» رو بزنید و درخواست فعال‌سازی «نمایش مستقیم اطلاعات پرداخت» رو بدید. اگه فعال نباشه، همون رفتار همیشگی (فقط `cardNumberMasked`) رو می‌بینید، بدون خطا.

وقتی فعال باشه، به‌جای این‌که مشتری برای دیدن شماره‌کارت مجبور باشه وارد مینی‌اپ اطلس‌پی بشه، شماره‌کارت واقعی و کامل مستقیم تو پاسخ `POST /orders` بهتون داده می‌شه — می‌تونید همون‌جا، تو ربات خودتون، بدون هیچ واسطه‌ای به مشتری نشونش بدید.

> **⚠️ نکته‌ی مهم:** دکمه‌ی لینک مینی‌اپ (`customerStartLink`) رو همچنان نشون بدید — این فیچر جایگزین مینی‌اپ نیست، فقط یه لایه‌ی نمایشی اضافه‌ست. اگه تایید خودکار پیامکی کار نکنه، تنها راه ارسال رسید همون مینی‌اپه.

**فیلدهای اضافه در پاسخ `POST /orders`:**

| فیلد | توضیح |
|---|---|
| `cardNumber` | شماره‌کارت کامل (نه ماسک‌شده) — همون چیزی که مشتری باید بهش واریز کنه |
| `cardHolderName` | نام صاحب کارت |
| `bankName` | نام بانک |

```json
{
  "orderId": 66,
  "trackingCode": "5c23c12c9fa0c8b3",
  "totalAmountToman": 250000,
  "cardNumberMasked": "6037****3165",
  "cardNumber": "6037991812345678",
  "cardHolderName": "Ali Rezaei",
  "bankName": "Bank Melli",
  "paymentDeadlineAt": "2026-08-15T21:58:56.329Z",
  "customerStartLink": "https://t.me/atlaspaybot/pay?startapp=order_66_..."
}
```

**متن نمونه‌ی پیام به مشتری** (می‌تونید مستقیم، با جایگزین‌کردن مقادیر واقعی، تو ربات خودتون بفرستید):

```
💳 اطلاعات پرداخت

مبلغ: {{totalAmountToman}} تومان
شماره‌کارت: {{cardNumber}}
به نام: {{cardHolderName}}
بانک: {{bankName}}

⚠️ لطفاً مبلغ را دقیقاً مطابق عدد بالا واریز کنید.

پس از واریز، حدود ۲ دقیقه صبر کنید. اگر پرداخت به‌صورت خودکار تأیید
نشد، از طریق لینک زیر وارد شوید و تصویر رسید را ارسال کنید:
{{customerStartLink}}
```

**نکات امنیتی:**

- شماره‌کارت واقعی از این لحظه وارد سرور/دیتابیس/لاگ‌های خودِ سیستم شما می‌شه — مسئولیت نگهداری امنش (رمزنگاری در حالت ذخیره‌سازی، محدودکردن دسترسی) با شماست.
- توصیه می‌کنیم این مقدار رو بلافاصله بعد از نمایش به مشتری، از دیتابیس/لاگ خودتون پاک یا کوتاه‌مدت نگه دارید — نیازی به نگهداری طولانی‌مدت نیست.

<details>
<summary>نمونه‌کد امن‌سازی برای Node.js (اختیاری، پیشنهادی) — کلیک برای مشاهده</summary>

رمزنگاری قبل از ذخیره‌سازی (AES-256-GCM):

```javascript
const crypto = require('crypto');

// این کلید رو خودتون یه‌بار بسازید و جای امن (Environment Variable) نگه دارید:
// node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
const ENCRYPTION_KEY = Buffer.from(process.env.CARD_ENCRYPTION_KEY, 'hex');

function encryptCardNumber(cardNumber) {
  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv('aes-256-gcm', ENCRYPTION_KEY, iv);
  const encrypted = Buffer.concat([cipher.update(cardNumber, 'utf8'), cipher.final()]);
  const authTag = cipher.getAuthTag();
  // این رو تو دیتابیس ذخیره کنید (نه خودِ cardNumber خام):
  return `${iv.toString('hex')}:${authTag.toString('hex')}:${encrypted.toString('hex')}`;
}

function decryptCardNumber(stored) {
  const [ivHex, authTagHex, dataHex] = stored.split(':');
  const decipher = crypto.createDecipheriv('aes-256-gcm', ENCRYPTION_KEY, Buffer.from(ivHex, 'hex'));
  decipher.setAuthTag(Buffer.from(authTagHex, 'hex'));
  return Buffer.concat([decipher.update(Buffer.from(dataHex, 'hex')), decipher.final()]).toString('utf8');
}
```

پاک‌سازی خودکار بعد از چند ساعت — یه Cron ساده که هر ۱۰ دقیقه اجرا می‌شه و رکوردهای قدیمی‌تر از ۳ ساعت رو خالی می‌کنه (این عدد هماهنگ با پنجره‌ی اعتراضِ اطلس‌پی — ۲ ساعت — انتخاب شده، تا حتی سفارش‌های معترض‌شده هم هنوز اطلاعاتشون موجود باشه):

```javascript
// هر ۱۰ دقیقه یه‌بار (با node-cron یا مشابه) اجرا بشه
async function purgeOldCardNumbers(db) {
  const PURGE_AFTER_MS = 3 * 60 * 60 * 1000; // ۳ ساعت
  const cutoff = new Date(Date.now() - PURGE_AFTER_MS);
  await db.query(
    'UPDATE orders SET card_number_encrypted = NULL WHERE created_at < $1 AND card_number_encrypted IS NOT NULL',
    [cutoff],
  );
}
```

> این نمونه‌کدها صرفاً پیشنهادی‌ان - اجرای واقعی و درستی‌شون تو زیرساخت خودتون، مسئولیت خودتونه، ولی حداقل نقطه‌ی شروع امنی می‌دن.

</details>

---

### ۳.۸ Webhook - اطلاع‌رسانیِ خودکار (اختیاری، جایگزینِ Polling)

> **ℹ️ این یه قابلیت کاملاً اختیاریه.** بدونِ ثبتِ Webhook، همه‌چیز دقیقاً مثلِ قبل کار می‌کنه - باید خودتون هر از گاهی وضعیتِ سفارش رو با [۳.۲ استعلامِ وضعیت](#۳۲-استعلام-وضعیت-سفارش) چک کنید (Polling). با ثبتِ Webhook، به‌جای این‌که شما مدام بپرسید، همون لحظه‌ای که سفارش تایید یا رد بشه، ما خودمون یه درخواست به سرورِ شما می‌فرستیم.

#### ثبت/تغییر/حذفِ Webhook

```
POST /webhook
Headers: X-API-Key: <کلید شما>
Body: { "webhookUrl": "https://yourdomain.com/webhooks/atlaspay" }
```

برای حذف، `webhookUrl` رو `null` بفرستید. آدرس حتماً باید با `https://` شروع بشه.

**پاسخ (فقط همین یه‌بار Secret رو نشون می‌ده - جایی ذخیره‌ش کنید):**
```json
{
  "success": true,
  "data": {
    "webhookUrl": "https://yourdomain.com/webhooks/atlaspay",
    "webhookSecret": "a1b2c3d4e5f6..."
  }
}
```

برای دیدنِ وضعیتِ فعلی (بدونِ افشای دوباره‌ی Secret):
```
GET /webhook
Headers: X-API-Key: <کلید شما>
```

#### ساختارِ درخواستی که براتون می‌فرستیم

هر بار که سفارشی تایید یا رد بشه، یه `POST` به آدرسِ ثبت‌شده با این بدنه می‌فرستیم:

```json
{
  "event": "order.confirmed",
  "orderId": 66,
  "merchantOrderRef": "your-internal-ref-123",
  "totalAmountToman": 112000,
  "status": "confirmed",
  "timestamp": "2026-09-09T14:22:31.000Z"
}
```

برای `event: "order.rejected"`، یه فیلدِ اضافه هم داره: `"reason": "دلیلِ رد شدن"`.

#### تاییدِ صحتِ درخواست (امضای HMAC)

هدرِ `X-Webhook-Signature` رو هر درخواست هست - امضای HMAC-SHA256 بدنه با `webhookSecret` خودتون. قبل از پردازش، حتماً تاییدش کنید تا مطمئن بشید درخواست واقعاً از طرفِ اطلس‌پیه، نه یه نفرِ دیگه که آدرسِ Webhookتون رو حدس زده:

```javascript
const crypto = require('crypto');

app.post('/webhooks/atlaspay', express.json(), (req, res) => {
  const signature = req.headers['x-webhook-signature'];
  const expected = crypto
    .createHmac('sha256', process.env.ATLASPAY_WEBHOOK_SECRET)
    .update(JSON.stringify(req.body))
    .digest('hex');

  if (signature !== expected) {
    return res.status(401).send('Invalid signature');
  }

  const { event, orderId, merchantOrderRef, status } = req.body;
  // ... منطقِ خودتون (مثلاً تحویلِ خودکارِ محصول)

  res.status(200).send('OK');
});
```

#### نکاتِ مهم

- **بدونِ Retry:** اگه سرورِ شما در دسترس نباشه یا Timeout بده (۵ ثانیه)، فقط یه‌بار تلاش می‌کنیم و دیگه دوباره نمی‌فرستیم. به همین دلیل، Webhook رو به‌عنوانِ یه **میان‌بر برای سرعت** ببینید، نه تنها منبعِ حقیقت - همچنان توصیه می‌کنیم [۳.۲ استعلامِ وضعیت](#۳۲-استعلام-وضعیت-سفارش) رو هم به‌عنوانِ پشتیبان نگه دارید (مثلاً یه چک دوره‌ایِ کم‌فاصله برای سفارش‌هایی که هنوز `awaiting_payment`ان).
- **پاسخِ سریع بدید:** سعی کنید تو Handlerِ Webhook فقط داده رو ثبت کنید و بلافاصله `200` برگردونید؛ منطقِ سنگین (مثلاً تماس با APIهای دیگه) رو به‌صورتِ Async/صف انجام بدید، وگرنه ممکنه قبل از رسیدنِ پاسخ، Timeout بخوریم.
- می‌تونید Webhook رو از **پنلِ مینی‌اپِ خودتون** هم (بدونِ نیاز به کد) تنظیم کنید - دقیقاً همین Endpoint، فقط با احرازِ هویتِ تلگرامی به‌جای کلیدِ API.

---

## ۴. وضعیت‌های سفارش

فیلد `status` در پاسخ همه‌ی Endpoint های بالا یکی از این مقادیره:

| شناسه | توضیح |
|---|---|
| `awaiting_payment` | در انتظار واریز مشتری |
| `admin_review` | رسید رسیده، در حال بررسی |
| `underpaid_review` | مبلغ واریزی کمتر از سفارش بوده، در حال بررسی مغایرت |
| `underpaid_awaiting_remainder` | مشتری قبول کرده باقیمانده رو جدا واریز کنه |
| `confirmed` / `settled` | تایید نهایی شد، TRX واریز شد |
| `rejected` | رد شد (رسید نامعتبر یا مغایرت حل‌نشده) |
| `expired` | مهلت پرداخت (۲۰ دقیقه) بدون واریز گذشت |
| `cancelled` | توسط فروشگاه لغو شد |

### ۴.۱ کسری واریز و فیلد `requiresManualDelivery`

اگه مشتری کمتر از مبلغ سفارش واریز کنه، سیستم خودکار مغایرت رو تشخیص می‌ده و به مشتری دو گزینه نشون می‌ده: قبول همون مبلغ کمتر، یا واریز باقیمانده به‌صورت جدا.

اگه مشتری مبلغ کمتر رو قبول کنه، سفارش هم به `confirmed`/`settled` می‌رسه و هم `paid: true` می‌گیره — **دقیقاً مثل یه پرداخت کامل** — با این تفاوت که مبلغ TRX واریزشده به حساب شما کمتر از مبلغ کامل سفارشه (برابر با همون مبلغی که مشتری واقعاً واریز کرده، نه `totalAmountToman`).

برای همین یه فیلد جدا لازمه:

```json
"requiresManualDelivery": true
```

| مقدار | یعنی چی |
|---|---|
| `false` | پرداخت کامل بوده، خیالتون راحت باشه و سرویس رو خودکار تحویل بدید. |
| `true` | مشتری کمتر از مبلغ کامل واریز کرده و همون مبلغ کمتر پذیرفته شده. مبلغ واقعی واریزشده به حساب شما رو با `actualReceivedAmountToman` چک کنید و **پیش از تحویل خودکار سرویس، خودتون تصمیم بگیرید** — آیا با همین مبلغ سرویس رو ارائه می‌دید یا با مشتری هماهنگ می‌کنید.

> **⚠️ نکته‌ی مهم:** همیشه قبل از تحویل خودکار سرویس/کانفیگ، هم `paid` (یا `status`) و هم `requiresManualDelivery` رو با هم چک کنید. اتکا فقط به `paid`/`status` باعث می‌شه سفارش‌های کسری‌واریز هم مثل پرداخت کامل تحویل داده بشن.

---

## ۵. کدهای خطا

| کد | توضیح |
|---|---|
| `400` | ورودی نامعتبر (مثلاً مبلغ خارج از محدوده یا پارامتر ضروری جا افتاده) |
| `401` | کلید API نامعتبر یا ارسال‌نشده |
| `404` | سفارش پیدا نشد یا متعلق به شما نیست |
| `503` | سرویس موقتاً غیرفعال است |

---

## ۶. نمونه‌ی کامل پیاده‌سازی

> **✅ نکته:** روشی که تو این بخش نشون می‌دیم (Polling — یعنی خودتون هر چند ثانیه یه‌بار وضعیت سفارش رو می‌پرسید) ساده‌ترین و توصیه‌شده‌ترین روش اتصاله. هیچ سرور دائمی، هیچ Endpoint عمومی جدید، و هیچ تنظیم امنیتی اضافه (مثل بررسی امضا) لازم نداره — روی هر نوع هاستی (حتی اشتراکی که فقط Cron داره، نه پروسه‌ی همیشه‌روشن) کار می‌کنه.

سه نمونه‌ی کامل زیر (Node.js، Python، PHP) یه ربات تلگرامی ساده رو نشون می‌دن که دستور `/charge` می‌گیره، سفارش می‌سازه، و به‌صورت دوره‌ای وضعیت سفارش‌های در انتظار رو چک می‌کنه — با در نظر گرفتن `requiresManualDelivery`. منطق برای هر زبان دیگه‌ای (Go، Java، …) هم مشابه‌ست — فقط سه Endpoint بالا (`POST /orders`، `GET /orders/{id}`، و در صورت نیاز `POST /orders/{id}/verify`) رو با هر کتابخونه‌ی HTTP که استفاده می‌کنید صدا بزنید.

### ۶.۱ Node.js

نصب پیش‌نیاز: `npm install telegraf`

```javascript
const ATLASPAY_API_KEY = 'کلید_API_که_اطلس‌پی_بهتون_داده';
const ATLASPAY_BASE_URL = 'https://api.atlaspay.space/api/v1';

// ⚠️ به‌جای این Map، تو پروژه‌ی واقعی از دیتابیس خودتون استفاده کنید
const pendingOrders = new Map(); // orderId -> { telegramUserId }

async function createOrder(telegramUserId, amountToman) {
  const res = await fetch(`${ATLASPAY_BASE_URL}/orders`, {
    method: 'POST',
    headers: {
      'X-API-Key': ATLASPAY_API_KEY,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      merchantOrderRef: `bot-${telegramUserId}-${Date.now()}`,
      baseAmountToman: amountToman,
      customerTelegramId: telegramUserId,
    }),
  });
  if (!res.ok) throw new Error(`ساخت سفارش شکست خورد: ${await res.text()}`);

  const order = await res.json();
  pendingOrders.set(order.orderId, { telegramUserId });
  return order; // شامل customerStartLink برای نمایش به کاربر
}

async function deliverService(telegramUserId, order) {
  // ⚠️ اینجا رو با کار واقعی خودتون جایگزین کنید:
  // شارژ موجودی داخلی، فعال‌کردن اشتراک، ارسال فایل و غیره
}

async function checkPendingOrders(bot) {
  const successStatuses = ['confirmed', 'settled'];
  const deadStatuses = ['rejected', 'expired', 'cancelled'];

  for (const [orderId, info] of pendingOrders.entries()) {
    const res = await fetch(`${ATLASPAY_BASE_URL}/orders/${orderId}`, {
      headers: { 'X-API-Key': ATLASPAY_API_KEY },
    });
    const data = await res.json();

    if (successStatuses.includes(data.status)) {
      if (data.requiresManualDelivery) {
        // ⚠️ کسری‌واریز پذیرفته‌شده - مبلغ TRX واریزی کمتر از حد کامله.
        // خودکار تحویل نمی‌دیم؛ به مدیر خبر می‌دیم تا خودش تصمیم بگیره.
        await bot.telegram.sendMessage(
          ADMIN_CHAT_ID,
          `⚠️ سفارش ${orderId} با کسری واریز تایید شد — نیاز به بررسی دستی.`,
        );
        pendingOrders.delete(orderId);
        continue;
      }
      await deliverService(info.telegramUserId, data);
      await bot.telegram.sendMessage(info.telegramUserId, '✅ Payment confirmed, wallet charged!');
      pendingOrders.delete(orderId);
    } else if (deadStatuses.includes(data.status)) {
      await bot.telegram.sendMessage(info.telegramUserId, '❌ Payment failed or expired.');
      pendingOrders.delete(orderId);
    }
    // در غیر این صورت، سفارش هنوز در جریانه — دفعه‌ی بعد دوباره چک می‌شه
  }
}

// مثال فراخوانی: /charge 50000
bot.command('charge', async (ctx) => {
  const amount = Number(ctx.message.text.split(' ')[1]);
  if (!amount || amount <= 0) return ctx.reply('مثال: /charge 50000');

  const order = await createOrder(ctx.from.id, amount);
  await ctx.reply('برای پرداخت روی دکمه بزنید:', Markup.inlineKeyboard([
    Markup.button.url('💳 پرداخت', order.customerStartLink),
  ]));
});

setInterval(() => checkPendingOrders(bot), 30_000);
```

---

### ۶.۲ Python

نصب پیش‌نیاز: `pip install python-telegram-bot requests`

```python
import time
import requests
from telegram import Update, InlineKeyboardButton, InlineKeyboardMarkup
from telegram.ext import Application, CommandHandler, ContextTypes

ATLASPAY_API_KEY = "کلید_API_که_اطلس‌پی_بهتون_داده"
ATLASPAY_BASE_URL = "https://api.atlaspay.space/api/v1"
BOT_TOKEN = "توکن_رباتِ_خودتون_از_BotFather"
ADMIN_CHAT_ID = 123456789  # آیدی عددی خودتون یا هر ادمینی که باید کسری‌واریزها رو ببینه

# ⚠️ به‌جای این دیکشنری، تو پروژه‌ی واقعی از دیتابیس خودتون استفاده کنید
pending_orders = {}  # order_id -> { "telegram_user_id": ... }


def create_order(telegram_user_id: int, amount_toman: int) -> dict:
    res = requests.post(
        f"{ATLASPAY_BASE_URL}/orders",
        headers={"X-API-Key": ATLASPAY_API_KEY, "Content-Type": "application/json"},
        json={
            "merchantOrderRef": f"bot-{telegram_user_id}-{int(time.time())}",
            "baseAmountToman": amount_toman,
            "customerTelegramId": telegram_user_id,
        },
        timeout=15,
    )
    res.raise_for_status()
    order = res.json()

    # ⚠️ اینجا رو با ذخیره‌سازی واقعی تو دیتابیس خودتون جایگزین کنید
    pending_orders[order["orderId"]] = {"telegram_user_id": telegram_user_id}
    return order  # شامل customerStartLink برای نمایش به کاربر


async def deliver_service(telegram_user_id: int, order: dict):
    # ⚠️ اینجا رو با کار واقعی خودتون جایگزین کنید:
    # شارژ موجودی داخلی، فعال‌کردن اشتراک، ارسال فایل و غیره
    pass


async def check_pending_orders(context: ContextTypes.DEFAULT_TYPE):
    success_statuses = {"confirmed", "settled"}
    dead_statuses = {"rejected", "expired", "cancelled"}

    for order_id in list(pending_orders.keys()):
        info = pending_orders[order_id]
        res = requests.get(f"{ATLASPAY_BASE_URL}/orders/{order_id}", headers={"X-API-Key": ATLASPAY_API_KEY}, timeout=15)
        data = res.json()
        status = data.get("status")

        if status in success_statuses:
            if data.get("requiresManualDelivery"):
                # ⚠️ کسری‌واریز پذیرفته‌شده - مبلغ TRX واریزی کمتر از حد کامله.
                # خودکار تحویل نمی‌دیم؛ به مدیر خبر می‌دیم تا خودش تصمیم بگیره.
                await context.bot.send_message(ADMIN_CHAT_ID, f"⚠️ سفارش {order_id} با کسری واریز تایید شد — نیاز به بررسی دستی.")
                del pending_orders[order_id]
                continue
            await deliver_service(info["telegram_user_id"], data)
            await context.bot.send_message(info["telegram_user_id"], "✅ Payment confirmed, wallet charged!")
            del pending_orders[order_id]
        elif status in dead_statuses:
            await context.bot.send_message(info["telegram_user_id"], "❌ Payment failed or expired.")
            del pending_orders[order_id]
        # در غیر این صورت، سفارش هنوز در جریانه — دفعه‌ی بعد دوباره چک می‌شه


async def charge_command(update: Update, context: ContextTypes.DEFAULT_TYPE):
    # مثال استفاده: /charge 50000
    if not context.args or not context.args[0].isdigit():
        await update.message.reply_text("فرمت درست: /charge [مبلغ به تومان]\nمثال: /charge 50000")
        return

    amount = int(context.args[0])
    order = create_order(update.effective_user.id, amount)
    keyboard = InlineKeyboardMarkup([[InlineKeyboardButton("💳 پرداخت", url=order["customerStartLink"])]])
    await update.message.reply_text(f"برای شارژ {amount:,} تومان روی دکمه بزنید:", reply_markup=keyboard)


def main():
    app = Application.builder().token(BOT_TOKEN).build()
    app.add_handler(CommandHandler("charge", charge_command))
    app.job_queue.run_repeating(check_pending_orders, interval=30, first=10)
    app.run_polling()


if __name__ == "__main__":
    main()
```

---

### ۶.۳ PHP

⚠️ بیشتر هاست‌های PHP اجازه‌ی یه پروسه‌ی «همیشه روشن» رو نمی‌دن (برخلاف Node.js/Python)، برای همین این نسخه با **Cron** کار می‌کنه: هر یک دقیقه یه‌بار خودکار اجرا می‌شه، وضعیت سفارش‌های در انتظار رو از یه فایل JSON می‌خونه و آپدیت می‌کنه.

```php
<?php
// atlaspay-check.php — هر یک دقیقه با Crontab اجرا می‌شه

$ATLASPAY_API_KEY = 'کلید_API_که_اطلس‌پی_بهتون_داده';
$ATLASPAY_BASE_URL = 'https://api.atlaspay.space/api/v1';
$BOT_TOKEN = 'توکن_رباتِ_خودتون_از_BotFather';
$ADMIN_CHAT_ID = 123456789; // آیدی عددی خودتون یا هر ادمینی که باید کسری‌واریزها رو ببینه
$PENDING_FILE = __DIR__ . '/pending_orders.json';

function atlaspayRequest(string $method, string $path, ?array $body = null): array {
    global $ATLASPAY_API_KEY, $ATLASPAY_BASE_URL;
    $ch = curl_init($ATLASPAY_BASE_URL . $path);
    $headers = ['X-API-Key: ' . $ATLASPAY_API_KEY];
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_CUSTOMREQUEST => $method,
    ]);
    if ($body !== null) {
        $headers[] = 'Content-Type: application/json';
        curl_setopt($ch, CURLOPT_POSTFIELDS, json_encode($body));
    }
    curl_setopt($ch, CURLOPT_HTTPHEADER, $headers);
    $res = curl_exec($ch);
    curl_close($ch);
    return json_decode($res, true) ?? [];
}

function sendTelegramMessage(int $telegramUserId, string $text): void {
    global $BOT_TOKEN;
    $ch = curl_init("https://api.telegram.org/bot{$BOT_TOKEN}/sendMessage");
    curl_setopt_array($ch, [
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_POST => true,
        CURLOPT_POSTFIELDS => http_build_query(['chat_id' => $telegramUserId, 'text' => $text]),
    ]);
    curl_exec($ch);
    curl_close($ch);
}

// ⚠️ این تابع رو صدا بزنید هروقت مشتری خواست شارژ کنه (مثلاً وقتی دستور /charge می‌زنه)
function createOrder(int $telegramUserId, int $amountToman): array {
    global $PENDING_FILE;
    $order = atlaspayRequest('POST', '/orders', [
        'merchantOrderRef' => "bot-{$telegramUserId}-" . time(),
        'baseAmountToman' => $amountToman,
        'customerTelegramId' => $telegramUserId,
    ]);

    $pending = file_exists($PENDING_FILE) ? json_decode(file_get_contents($PENDING_FILE), true) : [];
    $pending[$order['orderId']] = ['telegramUserId' => $telegramUserId];
    file_put_contents($PENDING_FILE, json_encode($pending));

    return $order; // شامل customerStartLink برای نمایش به کاربر
}

function deliverService(int $telegramUserId, array $order): void {
    // ⚠️ اینجا رو با کار واقعی خودتون جایگزین کنید:
    // شارژ موجودی داخلی، فعال‌کردن اشتراک، ارسال فایل و غیره
}

// بخش اصلی که Cron هر یک دقیقه صداش می‌زنه
function checkPendingOrders(): void {
    global $PENDING_FILE, $ADMIN_CHAT_ID;
    if (!file_exists($PENDING_FILE)) return;

    $pending = json_decode(file_get_contents($PENDING_FILE), true) ?? [];
    $successStatuses = ['confirmed', 'settled'];
    $deadStatuses = ['rejected', 'expired', 'cancelled'];

    foreach ($pending as $orderId => $info) {
        $data = atlaspayRequest('GET', "/orders/{$orderId}");
        $status = $data['status'] ?? null;

        if (in_array($status, $successStatuses, true)) {
            if (!empty($data['requiresManualDelivery'])) {
                // ⚠️ کسری‌واریز پذیرفته‌شده - مبلغ TRX واریزی کمتر از حد کامله.
                // خودکار تحویل نمی‌دیم؛ به مدیر خبر می‌دیم تا خودش تصمیم بگیره.
                sendTelegramMessage($ADMIN_CHAT_ID, "⚠️ سفارش {$orderId} با کسری واریز تایید شد — نیاز به بررسی دستی.");
                unset($pending[$orderId]);
                continue;
            }
            deliverService($info['telegramUserId'], $data);
            sendTelegramMessage($info['telegramUserId'], '✅ Payment confirmed, wallet charged!');
            unset($pending[$orderId]);
        } elseif (in_array($status, $deadStatuses, true)) {
            sendTelegramMessage($info['telegramUserId'], '❌ Payment failed or expired.');
            unset($pending[$orderId]);
        }
        // در غیر این صورت، سفارش هنوز در جریانه — دفعه‌ی بعد دوباره چک می‌شه
    }

    file_put_contents($PENDING_FILE, json_encode($pending));
}

checkPendingOrders();
```

بعد باید به سرورتون بگید این فایل رو هر یک دقیقه خودش اجرا کنه — یه خط به Crontab سرورتون اضافه کنید:

```bash
* * * * * php /path/to/atlaspay-check.php >> /path/to/atlaspay.log 2>&1
```

(ساخت سفارش وقتی مشتری دستور `/charge` می‌زنه، جدا از این فایله — تابع `createOrder()` رو از همون‌جای کد ربات‌تون که رسید Webhook تلگرام رو مدیریت می‌کنه صدا بزنید.)

---

## ۷. امنیت

- کلید API خودتون رو در جایی امن نگه دارید (نه در کد سمت کاربر، نه در مخزن عمومی گیت‌هاب).
- اگه فکر می‌کنید کلیدتون لو رفته، سریعاً به پشتیبانی اطلاع بدید تا کلید جدید صادر بشه.
- همه‌ی درخواست‌ها باید روی HTTPS انجام بشن؛ درخواست‌های HTTP ساده پذیرفته نمی‌شن.
- اطلاعات کارت‌های ادمین‌ها یا مشتری‌های سایر فروشگاه‌ها هیچ‌وقت در دسترس شما قرار نمی‌گیره.

---

## ۸. پشتیبانی

برای هر سوال یا مشکل فنی، از داخل ربات تلگرام اطلس‌پی دکمه‌ی «🆘 پشتیبانی» رو بزنید یا دستور `/support` رو بفرستید.
