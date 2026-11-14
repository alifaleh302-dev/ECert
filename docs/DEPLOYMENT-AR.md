<div dir="rtl" align="right">

# التقرير التقني — تنصيب نظام ECert على سيرفر خاص

> **الهدف:** دليل تقني كامل ومُختبَر لتنصيب نظام **ECert** وتشغيله على سيرفر خاص
> (VPS / Dedicated) من الصفر، بما يشمل: المتطلبات، قاعدة البيانات، متغيرات البيئة،
> طرق النشر (Docker و Native)، الوكيل العكسي وTLS، الخدمة الدائمة، النسخ الاحتياطي،
> الترقية، واستكشاف الأخطاء.
>
> **الجمهور:** مهندس تشغيل/نظم (DevOps) أو مدير نظام يملك صلاحية root على السيرفر.

---

## جدول المحتويات

1. [نظرة عامة على متطلبات التشغيل](#1-نظرة-عامة-على-متطلبات-التشغيل)
2. [الموارد الموصى بها](#2-الموارد-الموصى-بها)
3. [تحضير السيرفر](#3-تحضير-السيرفر)
4. [تنصيب قاعدة البيانات MySQL/MariaDB](#4-تنصيب-قاعدة-البيانات-mysqlmariadb)
5. [متغيرات البيئة (القيم الحساسة)](#5-متغيرات-البيئة-القيم-الحساسة)
6. [طريقة النشر (أ): Docker — مُوصى بها](#6-طريقة-النشر-أ-docker--مُوصى-بها)
7. [طريقة النشر (ب): Native عبر systemd](#7-طريقة-النشر-ب-native-عبر-systemd)
8. [الوكيل العكسي (Nginx) و TLS](#8-الوكيل-العكسي-nginx-و-tls)
9. [الجدار الناري وفتح المنافذ](#9-الجدار-الناري-وفتح-المنافذ)
10. [التحقق بعد التنصيب (قائمة فحص)](#10-التحقق-بعد-التنصيب-قائمة-فحص)
11. [إعدادات ما بعد أول دخول](#11-إعدادات-ما-بعد-أول-دخول)
12. [النسخ الاحتياطي والاستعادة](#12-النسخ-الاحتياطي-والاستعادة)
13. [الترقية والتحديث](#13-الترقية-والتحديث)
14. [استكشاف الأخطاء وإصلاحها](#14-استكشاف-الأخطاء-وإصلاحها)
15. [قائمة التحقق النهائية](#15-قائمة-التحقق-النهائية)

---

## 1. نظرة عامة على متطلبات التشغيل

| المكوّن | المتطلب | ملاحظات |
|---------|---------|---------|
| نظام التشغيل | Linux (Ubuntu 22.04/24.04 أو Debian 12 مُوصى بهما) | يعمل أيضاً على Windows (IIS) عبر `web.config`. |
| وقت التشغيل | **.NET 10 Runtime (ASP.NET Core)** | للطريقة Native. غير مطلوب لـ Docker. |
| قاعدة البيانات | **MySQL 8** أو **MariaDB 10.6+** | يجب أن تكون بترميز `utf8mb4`. |
| عميل MySQL | `mysql-client` / `mariadb-client` | **إلزامي لتوفير `mysqldump`** لخدمة النسخ الاحتياطي المشفّر. |
| الوكيل العكسي | Nginx (مُوصى به) | يُنهي TLS ويُمرّر الرؤوس. |
| الذاكرة | 1 GB كحد أدنى، 2 GB مُوصى به | ASP.NET Core + MariaDB. |
| المعالج | vCPU واحد كافٍ لمركز واحد | 2 vCPU مُوصى به. |
| القرص | 20 GB SSD+ | يكبر بحسب الشهادات والصور المرفوعة. |

**اعتماديات وقت التشغيل الخاصة بالكود:**
- **`mysqldump`** — تستدعيه خدمة النسخ الاحتياطي (`DatabaseBackupService`) عبر `Process`.
- **خطوط عربية مُضمَّنة** — بطاقات المشاركة تُرسم بـ SkiaSharp + HarfBuzzSharp مع خطوط مُدمجة
  في الحاوية (لا تعتمد على خطوط النظام).
- **حزم SkiaSharp/Linux** مُضمَّنة عبر NuGet (`SkiaSharp.NativeAssets.Linux.NoDependencies`).

---

## 2. الموارد الموصى بها

| حجم المركز | vCPU | RAM | قرص | قاعدة بيانات |
|-----------|:----:|:---:|:---:|--------------|
| صغير (≤ 5,000 شهادة) | 1 | 1 GB | 20 GB | MySQL محلي على نفس السيرفر |
| متوسط (≤ 50,000) | 2 | 2 GB | 50 GB | MySQL محلي أو مُدار |
| كبير | 4+ | 4 GB+ | 100 GB+ | MySQL مُدار + مراقبة |

---

## 3. تحضير السيرفر

```bash
# 1) التحديث وتثبيت الأدوات الأساسية
sudo apt-get update && sudo apt-get upgrade -y
sudo apt-get install -y curl ca-certificates gnupg lsb-release ufw git unzip

# 2) إنشاء مستخدم نظام غير مميّز لتشغيل التطبيق (للطريقة Native)
sudo useradd --system --create-home --shell /usr/sbin/nologin ecert

# 3) ضبط المنطقة الزمنية (مهم لسجلات التدقيق والتقارير)
sudo timedatectl set-timezone Asia/Aden   # أو المنطقة المطلوبة

# 4) إنشاء مجلدات التطبيق
sudo mkdir -p /opt/ecert /var/lib/ecert /etc/ecert
sudo chown -R ecert:ecert /opt/ecert /var/lib/ecert
```

> **ملاحظة:** لا تُشغّل التطبيق بصلاحية root. استخدم مستخدماً غير مميّز (`ecert`).

---

## 4. تنصيب قاعدة البيانات MySQL/MariaDB

### 4.1 التثبيت (مثال MariaDB على Ubuntu)

```bash
sudo apt-get install -y mariadb-server
sudo systemctl enable --now mariadb
sudo mariadb-secure-installation   # اضبط كلمة مرور root ووافِق على الإعدادات الآمنة
```

### 4.2 إنشاء قاعدة البيانات والمستخدم

```bash
sudo mariadb -u root -p <<'SQL'
CREATE DATABASE ecert CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'ecert'@'127.0.0.1' IDENTIFIED BY 'ضع_كلمة_مرور_قوية_هنا';
GRANT ALL PRIVILEGES ON ecert.* TO 'ecert'@'127.0.0.1';
-- للتطوير/الاختبارات (قواعد ecert_test_*)، يمكن منح:
-- GRANT ALL PRIVILEGES ON `ecert\_test\_%`.* TO 'ecert'@'127.0.0.1';
FLUSH PRIVILEGES;
SQL
```

### 4.3 ضبط الأداء والأمان (اختياري لكن مُوصى به)

أنشئ `/etc/mysql/mariadb.conf.d/99-ecert.cnf`:

```ini
[mysqld]
character-set-server = utf8mb4
collation-server     = utf8mb4_unicode_ci
max_connections      = 200
innodb_buffer_pool_size = 512M      # ~50-70% من الذاكرة المتاحة
innodb_flush_log_at_trx_commit = 1  # أمان على حساب أداء طفيف (مناسب للمالية)
```

```bash
sudo systemctl restart mariadb
```

### 4.4 (بديل) قاعدة بيانات مُدارة

يدعم النظام أي MySQL مُدار (Railway, PlanetScale, AWS RDS, DigitalOcean Managed DB).
استخدم صيغة `mysql://` أو ADO.NET مع `SslMode=Required` و`AllowPublicKeyRetrieval=True`.

---

## 5. متغيرات البيئة (القيم الحساسة)

> **قاعدة ذهبية:** لا تكتب أي سر في `appsettings.json` أو في المستودع. مرّر كل القيم الحساسة
> عبر متغيرات البيئة (أو `EnvironmentFile` لـ systemd).

| المتغير | إلزامي | الوصف |
|---------|:------:|-------|
| `ConnectionStrings__MySqlConnection` | ✅ | سلسلة اتصال ADO.NET: `Server=...;Port=3306;Database=ecert;User ID=...;Password=...;SslMode=...;AllowPublicKeyRetrieval=True` |
| `DATABASE_URL` | بديل | صيغة `mysql://user:pass@host:port/db?sslmode=required` (بديل عن السابقة). |
| `CERT_HMAC_SECRET` | ✅ (مُستحسن) | سر توقيع الشهادات/السندات. **ولّده مرة واحدة ولا تغيّره أبداً بعد الإصدار.** |
| `PUBLIC_BASE_URL` | مُستحسن | العنوان العام (`https://ecert.example.com`) لبناء روابط التحقق و QR. |
| `ECERT_BOOTSTRAP_PASSWORD` | اختياري | كلمة المرور الأولية لحسابات البذرة. إن غابت، تُولَّد عشوائياً وتُسجَّل مرة واحدة. |
| `ECERT_RESET_ADMIN_PASSWORD` | اختياري | إعادة تعيين كلمة مرور `admin` مؤقتاً (ألغِه بعد الاستخدام). |
| `TURNSTILE_SITE_KEY` / `TURNSTILE_SECRET_KEY` | اختياري | تفعيل Cloudflare Turnstile على النماذج العامة. |
| `Institution__Name` | اختياري | اسم المركز على السندات (افتراضي «مركز التدريب»). |
| `ASPNETCORE_ENVIRONMENT` | — | `Production` (الافتراضي في الحاوية). |
| `ASPNETCORE_HTTP_PORTS` / `PORT` | — | منفذ الاستماع (`PORT` يُحترم تلقائياً إن وُجد). |

**توليد الأسرار:**

```bash
# سر توقيع الشهادات (64 حرفاً سادس عشرية = 32 بايت)
openssl rand -hex 32
# كلمة مرور قوية للبذرة
openssl rand -base64 18
```

**ملف البيئة (Native):** أنشئ `/etc/ecert/ecert.env` بصلاحية `600`:

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_HTTP_PORTS=5000
ConnectionStrings__MySqlConnection=Server=127.0.0.1;Port=3306;Database=ecert;User ID=ecert;Password=ضع_كلمة_المرور;SslMode=None;AllowPublicKeyRetrieval=True
CERT_HMAC_SECRET=السر_المُولَّد
PUBLIC_BASE_URL=https://ecert.example.com
ECERT_BOOTSTRAP_PASSWORD=كلمة_مرور_البذرة
```

```bash
sudo chown ecert:ecert /etc/ecert/ecert.env
sudo chmod 600 /etc/ecert/ecert.env
```

> **ملاحظة:** `SslMode=None` مقبول فقط عندما تكون قاعدة البيانات على نفس السيرفر (localhost).
> مع قاعدة بيانات بعيدة، استخدم `SslMode=Required`.

---

## 6. طريقة النشر (أ): Docker — مُوصى بها

**المزايا:** بيئة معزولة وقابلة للتكرار، وبها `mysqldump` واعتماديات الرسم مثبّتة مسبقاً.

### 6.1 تثبيت Docker

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER   # ثم أعد تسجيل الدخول
```

### 6.2 بناء الصورة

```bash
cd /opt/ecert
sudo git clone https://github.com/alifaleh302-dev/ECert.git .
sudo docker build -t ecert:latest .
```

### 6.3 ملف `docker-compose.yml` (مع MariaDB وNginx)

```yaml
services:
  db:
    image: mariadb:11
    restart: unless-stopped
    environment:
      MARIADB_ROOT_PASSWORD: ${DB_ROOT_PASSWORD}
      MARIADB_DATABASE: ecert
      MARIADB_USER: ecert
      MARIADB_PASSWORD: ${DB_PASSWORD}
    command: --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci
    volumes:
      - db_data:/var/lib/mysql
    healthcheck:
      test: ["CMD", "healthcheck.sh", "--connect", "--innodb_initialized"]
      interval: 10s
      timeout: 5s
      retries: 10

  app:
    image: ecert:latest
    restart: unless-stopped
    depends_on:
      db:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__MySqlConnection: "Server=db;Port=3306;Database=ecert;User ID=ecert;Password=${DB_PASSWORD};SslMode=None;AllowPublicKeyRetrieval=True"
      CERT_HMAC_SECRET: ${CERT_HMAC_SECRET}
      PUBLIC_BASE_URL: ${PUBLIC_BASE_URL}
      ECERT_BOOTSTRAP_PASSWORD: ${BOOTSTRAP_PASSWORD}
    volumes:
      - uploads:/app/wwwroot/uploads
      - qrcodes:/app/wwwroot/qrcodes
    ports:
      - "127.0.0.1:10000:10000"   # لا تُعرض للخارج؛ Nginx يمرّر إليها
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:10000/health"]
      interval: 30s
      timeout: 5s
      retries: 3

volumes:
  db_data:
  uploads:
  qrcodes:
```

**ملف `.env` بجانبه** (صلاحية `600`):

```ini
DB_ROOT_PASSWORD=...
DB_PASSWORD=...
CERT_HMAC_SECRET=...
PUBLIC_BASE_URL=https://ecert.example.com
BOOTSTRAP_PASSWORD=...
```

### 6.4 التشغيل

```bash
sudo docker compose up -d
sudo docker compose logs -f app
```

> **تنبيهات مهمة حول الحاوية:**
> - **`mysqldump` مثبّت داخل الصورة**، لكن خدمة النسخ الاحتياطي تتصل بقاعدة البيانات عبر
>   سلسلة الاتصال؛ لذا تأكّد أن الحاوية تستطيع الوصول إلى خدمة `db`.
> - الحاوية تعمل مبدئياً كـ root **فقط** لضبط ملكية مجلدات الرفع (الـ volumes تُركَّب كـ root)،
>   ثم تهبط تلقائياً لمستخدم `app` غير المميّز قبل تشغيل التطبيق.
> - **حجم `uploads` و`qrcodes` دائم (volume)** — لا تفقد الصور المرفوعة عند إعادة بناء الحاوية.
> - **مفاتيح Data Protection تُخزَّن في قاعدة البيانات**، فتبقى الجلسات صالحة عبر عمليات النشر.

---

## 7. طريقة النشر (ب): Native عبر systemd

### 7.1 تثبيت .NET 10 Runtime

```bash
# أضف مستودع Microsoft
wget https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/ms.deb
sudo dpkg -i /tmp/ms.deb
sudo apt-get update

# ثبّت ASP.NET Core Runtime 10
sudo apt-get install -y aspnetcore-runtime-10.0

# أدوات العميل لقاعدة البيانات (mysqldump)
sudo apt-get install -y mariadb-client

dotnet --info   # تحقّق من الإصدار
```

### 7.2 بناء ونشر التطبيق

```bash
cd /tmp
git clone https://github.com/alifaleh302-dev/ECert.git
cd ECert
dotnet publish ECert/ECert.csproj -c Release -o /opt/ecert/app

sudo chown -R ecert:ecert /opt/ecert/app
```

### 7.3 خدمة systemd

أنشئ `/etc/systemd/system/ecert.service`:

```ini
[Unit]
Description=ECert Training Center Management System
After=network.target mariadb.service
Wants=mariadb.service

[Service]
Type=notify
User=ecert
Group=ecert
WorkingDirectory=/opt/ecert/app
EnvironmentFile=/etc/ecert/ecert.env
ExecStart=/usr/bin/dotnet /opt/ecert/app/ECert.dll
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=ecert
# تحصين أمني
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ReadWritePaths=/opt/ecert/app/wwwroot/uploads /opt/ecert/app/wwwroot/qrcodes

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now ecert
sudo systemctl status ecert
sudo journalctl -u ecert -f
```

> **ملاحظة:** إن كنت تستخدم `Type=notify` فيلزم إضافة `Microsoft.Extensions.Hosting.Systemd`
> أو استخدم `Type=simple` بدلاً منه لتجنّب مشاكل بدء الخدمة:
> ```ini
> Type=simple
> ```

---

## 8. الوكيل العكسي (Nginx) و TLS

### 8.1 تثبيت Nginx و Certbot

```bash
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

### 8.2 إعداد Nginx

أنشئ `/etc/nginx/sites-available/ecert`:

```nginx
server {
    listen 80;
    server_name ecert.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name ecert.example.com;

    # ستُضاف تلقائياً بواسطة certbot
    # ssl_certificate     /etc/letsencrypt/live/ecert.example.com/fullchain.pem;
    # ssl_certificate_key /etc/letsencrypt/live/ecert.example.com/privkey.pem;

    client_max_body_size 20m;   # يجب أن يسمح برفع ملفات Excel (حد التطبيق 10MB) والصور

    location / {
        proxy_pass         http://127.0.0.1:10000;   # أو :5000 للطريقة Native
        proxy_http_version 1.1;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Real-IP         $remote_addr;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   Upgrade           $http_upgrade;
        proxy_set_header   Connection        "upgrade";
        proxy_read_timeout 300s;
    }
}
```

> **مهم جداً:** يجب تمرير `X-Forwarded-For` و`X-Forwarded-Proto` (والتطبيق مضبوط على
> `ForwardLimit = 1`). عدم تمريرهما يجعل النظام يرى عنوان الوكيل فقط، فيتأثر **تحديد المعدل**
> و**حظر المحاولات الفاشلة** و**روابط التحقق** (سيتحوّل الرابط إلى HTTP بدل HTTPS).

### 8.3 التفعيل وTLS

```bash
sudo ln -s /etc/nginx/sites-available/ecert /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx

# إصدار شهادة Let's Encrypt (سيعدّل إعداد Nginx تلقائياً)
sudo certbot --nginx -d ecert.example.com --agree-tos -m admin@example.com --redirect

# تجديد تلقائي (مُفعّل افتراضياً عبر systemd timer)
sudo certbot renew --dry-run
```

---

## 9. الجدار الناري وفتح المنافذ

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow OpenSSH
sudo ufw allow 'Nginx Full'     # 80 و 443 فقط
sudo ufw enable
sudo ufw status
```

> **لا تفتح** منفذ التطبيق (10000/5000) ولا منفذ قاعدة البيانات (3306) للإنترنت. اجعلهما محليين فقط.

---

## 10. التحقق بعد التنصيب (قائمة فحص)

```bash
# 1) فحص صحة التطبيق (يجب أن يعيد JSON بحالة Healthy)
curl -s http://127.0.0.1:10000/health | jq .

# 2) فحص الصفحة العامة
curl -sI https://ecert.example.com/ | head -1        # HTTP/2 200

# 3) فحص إعادة توجيه HTTPS
curl -sI http://ecert.example.com/ | grep -i location

# 4) فحص رؤوس الأمان
curl -sI https://ecert.example.com/ | grep -iE 'content-security|x-frame|strict-transport'
```

**مؤشرات النجاح:**
- `/health` يعيد `{"status":"Healthy", "checks":[{"name":"database","status":"Healthy"}]}`.
- الصفحة الرئيسية تعمل وتظهر بالعربية RTL.
- تسجيل الدخول يعمل، وتُطلب تغيير كلمة المرور عند أول دخول.

> **إن فشل `/health`** أو ظهرت الحاوية بحالة `CRASHED` مع نجاح البناء، فالسبب غالباً في مرحلة
> الإقلاع (قاعدة بيانات غير قابلة للوصول، أو سلسلة اتصال خاطئة، أو فشل ترقية المخطط). راجع
> سجلات التشغيل (وليس سجلات البناء) — انظر القسم 14.

---

## 11. إعدادات ما بعد أول دخول

1. **غيّر كلمة مرور `admin`** (مفروض تلقائياً عند أول دخول).
2. **`/SecuritySettings`** — اضبط:
   - **توكن بوت تليجرام** + **قناة التنبيهات** + **قناة النسخ الاحتياطي**.
   - **مفتاح تشفير النسخ الاحتياطي** + ساعة النسخ + تفعيل النسخ اليومي.
   - **المصادقة الثنائية (2FA)** وسياسة الحظر ومهلة الخمول.
   - اضغط **تشغيل نسخة الآن** للتحقق من عمل النسخ الاحتياطي المشفّر.
3. **`/FinanceSettings`** — اضبط **العملة**، **حد اعتماد الاسترداد**، وإقفال الفترة إن لزم.
4. **`/HomepageManage`** — املأ اسم المركز، الشعار، بيانات التواصل، الألوان، والأقسام.
5. **`/PhoneCountries`** — راجع مفاتيح الدول وبدايات الأرقام.
6. **`/Users` + `/Roles`** — أنشئ الأدوار المخصصة (أو استخدم القوالب الجاهزة) ووزّع المستخدمين
   مع **نطاق الدورات** المناسب.

> **لا تنسَ:** ربط كل مستخدم يريد استخدام 2FA بحساب تليجرام من صفحة تعديل المستخدم
> (**إنشاء رمز ربط**).

---

## 12. النسخ الاحتياطي والاستعادة

### 12.1 النسخ التلقائي (داخل النظام)

- خدمة خلفية يومية تُشغّل `mysqldump` ← تُشفّر الناتج (AES-256) ← ترفعه إلى **قناة تليجرام**.
- **لا تُكتب نسخة على قرص السيرفر** (التدفّق عبر الذاكرة)، فالنسخة الوحيدة المشفّرة في تليجرام.
- يمكن تشغيل نسخة فورية من `/SecuritySettings` (**تشغيل نسخة الآن**).

### 12.2 نسخ احتياطي يدوي إضافي (مُوصى به بشدّة)

لا تعتمد على آلية واحدة. أضف نسخة يومية خارجية:

```bash
# /etc/cron.daily/ecert-backup
#!/bin/sh
set -e
STAMP=$(date +%F)
mysqldump --single-transaction --routines --triggers \
  -h 127.0.0.1 -u ecert -p"$ECERT_DB_PASSWORD" ecert \
  | gzip > /var/backups/ecert/ecert-$STAMP.sql.gz
find /var/backups/ecert -name 'ecert-*.sql.gz' -mtime +30 -delete
```

```bash
sudo chmod +x /etc/cron.daily/ecert-backup
```

**ابدأ من صورة متّسقة عبر `--single-transaction`** (لا توقف الخدمة).

### 12.3 الاستعادة

```bash
# على قاعدة فارغة
gunzip -c /var/backups/ecert/ecert-2026-11-14.sql.gz | mysql -h 127.0.0.1 -u ecert -p ecert
```

> راجع أيضاً [docs/DATABASE_RECOVERY.md](DATABASE_RECOVERY.md) لتبديل قاعدة البيانات بأمان.

**قواعد ذهبية للنسخ:**
- احتفظ بنسخة **خارج السيرفر** (تخزين كائنات/سيرفر آخر).
- **لا ترفع ملف النسخة إلى المستودع العام.**
- خزّن `MySqlConnection` و`CERT_HMAC_SECRET` في متغيرات البيئة فقط.
- **لا تغيّر `CERT_HMAC_SECRET`**؛ تغييره يُبطل التحقق من كل الشهادات والسندات الحالية.

---

## 13. الترقية والتحديث

### 13.1 قبل الترقية

```bash
# خذ نسخة احتياطية مؤكّدة أولاً
mysqldump --single-transaction -h 127.0.0.1 -u ecert -p ecert | gzip > ~/ecert-pre-upgrade.sql.gz
```

### 13.2 Docker

```bash
cd /opt/ecert
sudo git pull
sudo docker build -t ecert:latest .
sudo docker compose up -d app     # يعيد إنشاء الحاوية بالصورة الجديدة
sudo docker compose logs -f app   # راقب نجاح الإقلاع وترقيات المخطط
```

### 13.3 Native

```bash
cd /tmp/ECert && sudo git pull
dotnet publish ECert/ECert.csproj -c Release -o /opt/ecert/app
sudo chown -R ecert:ecert /opt/ecert/app
sudo systemctl restart ecert
sudo journalctl -u ecert -n 100 --no-pager
```

### 13.4 ما يحدث عند الإقلاع (آمن ومتكرّر)

1. `EnsureCreated()` — إنشاء المخطط إن كانت القاعدة فارغة (مع إعادة محاولة عند الفشل الجزئي).
2. إنشاء جدول `DataProtectionKeys`.
3. تشغيل سلسلة **خدمات ترقية المخطط** idempotent (تضيف الأعمدة/الجداول الناقصة فقط، ولا تحذف).
4. تشغيل البذور **دون الكتابة فوق البيانات الموجودة**.

> **لا توجد EF Migrations تقليدية**، لذا **النسخة الاحتياطية قبل أي ترقية إلزامية**.
> النظام **لا يحذف** جداول/أعمدة قديمة تلقائياً.

---

## 14. استكشاف الأخطاء وإصلاحها

| العَرَض | السبب المرجّح | الحل |
|---------|----------------|------|
| فشل الإقلاع: «MySQL connection string is missing» | لم يُضبط متغير الاتصال | اضبط `ConnectionStrings__MySqlConnection` أو `DATABASE_URL`. |
| فشل الإقلاع: «Unsupported MySQL connection string format» | صيغة غير مدعومة | استخدم ADO.NET أو `mysql://`. |
| فشل الإقلاع: «Database initialization failed» | تعذّر الوصول للقاعدة / فشل ترقية | راجع السجلات؛ تحقّق من الوصول والصلاحيات والترميز `utf8mb4`. |
| `/health` يعيد 502 (خلف وكيل) | التطبيق لم يُقلع أو المنفذ خاطئ | راجع سجلات التطبيق؛ تأكّد من تطابق منفذ Nginx مع منفذ التطبيق. |
| تحوّل روابط التحقق إلى `http://` | `X-Forwarded-Proto` غير مُمرَّر | أضِفه في Nginx واضبط `PUBLIC_BASE_URL`. |
| تحديد المعدل يمنع الجميع (429) | عنوان العميل يظهر كعنوان الوكيل | مرّر `X-Forwarded-For` (النظام يثق بقفزة واحدة). |
| فشل النسخ الاحتياطي («تعذّر تنفيذ mysqldump») | `mysqldump` غير مثبّت في البيئة | ثبّت `mysql-client`/`mariadb-client` (مثبّت في صورة Docker). |
| الجلسات تُفقد بعد كل نشر | مفاتيح Data Protection لا تُحفظ | تأكّد أن جدول `DataProtectionKeys` موجود وأن مستخدم القاعدة يملك صلاحية الكتابة. |
| الصور المرفوعة تختفي بعد إعادة النشر | الـ volume غير مركّب | ركّب أحجاماً دائمة على `/app/wwwroot/uploads` و`/app/wwwroot/qrcodes`. |
| «تم حظر المحاولات مؤقتاً» لحساب إداري | تجاوز حد المحاولات الفاشلة | انتظر مدة الحظر أو عدّلها من `/SecuritySettings`. |
| مشكلة ترميز/حروف عربية مشوّهة | الترميز غير `utf8mb4` | أعد إنشاء القاعدة بـ `utf8mb4`/`utf8mb4_unicode_ci`. |
| تحذير `Duplicate key name` عند أول إقلاع | إقلاع متزامن/مقطوع لقاعدة فارغة | النظام يعيد المحاولة تلقائياً؛ إن استمر، أفرِغ القاعدة وأعد الإقلاع. |

**أماكن السجلات:**
- Docker: `docker compose logs -f app`
- Native: `journalctl -u ecert -f`
- رسائل الإقلاع الحرجة تُكتب أيضاً إلى `stderr` (مثل كلمة مرور البذرة العشوائية).

---

## 15. قائمة التحقق النهائية

- [ ] السيرفر محدَّث، ومستخدم نظام غير مميّز `ecert` جاهز.
- [ ] MySQL/MariaDB مثبّتة، وقاعدة `ecert` بترميز `utf8mb4`، ومستخدم مخصّص بصلاحيات مناسبة.
- [ ] `mysqldump` متاح (لتشغيل النسخ الاحتياطي المشفّر).
- [ ] متغيرات البيئة مضبوطة (اتصال، `CERT_HMAC_SECRET`، `PUBLIC_BASE_URL`)، وبصلاحية `600`.
- [ ] التطبيق يعمل (Docker أو systemd) ويستمع محلياً فقط.
- [ ] Nginx يُنهي TLS، يمرّر `X-Forwarded-For`/`X-Forwarded-Proto`، ويسمح برفع 20MB.
- [ ] شهادة TLS فعّالة وتجديدها التلقائي مُختبر.
- [ ] الجدار الناري يسمح بـ 22/80/443 فقط.
- [ ] `/health` يعيد `Healthy`.
- [ ] تسجيل الدخول يعمل، وكلمة مرور `admin` مُغيَّرة.
- [ ] `/SecuritySettings` مكتملة، و**نسخة احتياطية مشفّرة تجريبية نجحت**.
- [ ] `/FinanceSettings` (العملة/حد الاسترداد) مضبوطة.
- [ ] أحجام `uploads`/`qrcodes` دائمة.
- [ ] نسخة احتياطية خارجية (cron) تعمل وتُخزَّن خارج السيرفر.
- [ ] `CERT_HMAC_SECRET` موثّق في مدير أسرار ولا يُغيَّر لاحقاً.

---

<div align="center">

**نهاية التقرير التقني — تنصيب ECert على سيرفر خاص**

</div>

</div>
