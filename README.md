# VaultSync

**Dosyalarının zaman çizgisi.**

VaultSync, klasör değişikliklerini takip eden bir .NET ajanı, şifreli parça deposu ve React yönetim panelinden oluşan sürümlü yedekleme platformudur. Aynı içerik tekrar aktarılmaz; bir sürüm ancak tüm dosyalar doğrulandıktan sonra görünür olur.

## Neler yapar?

- **Artımlı aktarım:** dosyaları sabit 1 MiB parçalara ayırır; SHA-256 kimlikleriyle eksik parçaları sorar ve yalnız onları yükler.
- **Kalıcı gönderim kuyruğu:** manifest ve parçalar önce ajan diskinin outbox'ına yazılır. Bağlantı veya süreç kesilirse aynı işlem anahtarıyla devam eder. İlk başarılı bağlantıdan sonra çevrimdışı tarama yapılabilir.
- **Sürümler:** kaynak başına immutable manifest, parent/head kontrolü ve idempotent commit. Eski head sessizce yeni geçmişin üzerine yazılmaz.
- **Şifreli nesneler:** sunucu tarafında AES-256-GCM, her yazımda rastgele nonce; AAD olarak parça hash'i. Okuma sırasında GCM etiketi ve SHA-256 denetlenir.
- **Geri yükleme:** webden dosya/ZIP; ajandan hedef klasöre restore. Mevcut farklı dosya ezilmez, ayrı `.conflict-*` kopyası oluşur. Yol geçişleri ve sembolik bağlantılar reddedilir.
- **Karşılaştırma:** önceki sürüme göre yeni, değişen ve kaldırılan dosyalar; isteğe bağlı tam bütünlük taraması.
- **Cihaz yetkisi:** yönetici tarafından üretilen bir kerelik gösterilen cihaz anahtarları; sunucuda yalnız hash tutulur. Cihaz kendi snapshot'larını okuyabilir. Erişim iptali eski yedekleri silmez.
- **Panel:** gerçek depolama/sürüm metrikleri, kaynaklar, sürüm inceleme, işlem günlüğü, saklama politikası önizlemesi ve responsive tasarım.

## Teknolojiler

| Katman    | Teknoloji                                                    |
| --------- | ------------------------------------------------------------ |
| Web       | React 19, TypeScript, Vite, Lucide                           |
| API       | ASP.NET Core 10, EF Core                                     |
| Ajan      | .NET 10, FileSystemWatcher, HTTP, kalıcı disk outbox         |
| Metadata  | SQLite / PostgreSQL 17                                       |
| Nesneler  | Özel yerel disk / MinIO-S3 adaptörü                          |
| Doğrulama | Node test runner, gerçek .NET ajan testi, Chromium CI        |
| Dağıtım   | Docker Compose, Nginx, non-root API, Windows ajan artifact'ı |

## Hızlı başlatma

Gereksinimler: **.NET 10 SDK**, **Node.js 24**. Windows'ta `BASLAT.cmd` API ve webi açar; eksik SDK kurulumunu otomatik yapmaz.

```sh
npm ci
dotnet build backend
dotnet build agent
npm run build
```

İki ayrı terminal:

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run --project backend --urls http://127.0.0.1:9251
```

```sh
npm run dev
```

Web **http://127.0.0.1:9250/** · API **http://127.0.0.1:9251/health**.
Development giriş anahtarı `vaultsync-local-dev`. Production ortamında `OwnerKey` verilmeden API açılmaz. `.env` yalnız Compose tarafından okunur; yerel .NET çalıştırmasında ortam değişkenlerini kendiniz ayarlayın.

Yeni klonda pano boş başlar. **Cihaz bağla** ile bir kaynak oluşturun; gösterilen anahtarı güvenli kaydedin. Anahtar yeniden gösterilmez.

## Örnek çalışma alanı

Boş panoyu gerçek ajanla üretilen tanıtım verisiyle doldurmak için, API açık ve ajan derlenmişken Python 3 ile `python scripts/seed-demo.py` çalıştırılabilir. Betik yalnız loopback API kabul eder; üç örnek kaynak ve sekiz gerçek snapshot üretir. Gerçek kişisel dosya kullanmaz ve cihaz anahtarlarını diske yazmaz. Tekrar çalıştırmak yeni kaynaklar ekler. `DOTNET_EXE` ile özel .NET yolu seçilebilir.

## Ajan

```powershell
$env:VAULTSYNC_DEVICE_KEY='<panelde gösterilen cihaz anahtarı>'
$env:VAULTSYNC_API='http://127.0.0.1:9251'
dotnet run --project agent -- 'C:\MyDocuments'
# Tek tarama:
dotnet run --project agent -- 'C:\MyDocuments' --once
```

Watcher değişiklikleri biriktirir; 5 saniyelik kontrol ve 60 saniyelik periyodik tam tarama kullanır. Paylaşıma kapalı/değişen dosyada eksik snapshot yayınlamak yerine taramayı yeniden dener. Ajan açık kalmalıdır; bilgisayar kapalıyken yedek alınmaz.

Varsayılan outbox kullanıcı LocalApplicationData/VaultSync dizininde kaynak/anahtar/API kapsamına göre ayrılır. `VAULTSYNC_STATE` ile **kaynak klasörün dışında** özel bir dizin seçilebilir. Bu yerel outbox dosyaları plaintext'tir; hassas kaynaklarda işletim sistemi disk şifrelemesi ve ACL kullanın. API anahtarı dosyaya yazılmaz.

```powershell
dotnet run --project agent -- restore '<snapshot-id>' 'C:\Recovered'
```

Uzak API için HTTPS zorunludur; HTTP yalnız loopback geliştirmesinde kabul edilir. ZIP'i elle açarken de hedef dosyaları koruyun; otomatik restore'un çatışma davranışı manuel ZIP açmaya uygulanmaz.

## PostgreSQL ve Docker

```sh
cp .env.example .env
# Örnek şifreleri ve owner anahtarını değiştirin.
docker compose up --build
```

API 9251, web 9250 yalnız loopback'e yayınlanır; PostgreSQL hosta açılmaz. Metadata ve kasa named volume'lerde tutulur. Gerçek TLS/reverse proxy, secret rotasyonu ve backup/restore tatbikatı ayrıca kurulmalıdır.

Opsiyonel S3: API ortamında `S3__Endpoint` (host:port), `S3__Bucket`, `S3__AccessKey`, `S3__SecretKey`, `S3__Ssl=true`. Önceden özel bucket oluşturun. S3'e de yalnız şifreli nesneler gider. Anahtarlar `.env`/ortamda kalır; `.env` Git'e gönderilmez. S3 adaptörü kaynak olarak vardır; bu sürümün otomatik testi yerel nesne deposunu kapsar.

## Testler

API ve derlenmiş ajan açıkken:

```powershell
$env:DOTNET_EXE='dotnet'
$env:TEST_VAULT='<API VaultPath dizininin mutlak yolu>'
npm test
npm run build
```

Testleri ayrı metadata/kasa dizininde çalıştırın: cihaz ve sürüm üretirler. `TEST_VAULT` yalnız test deposunun şifreli nesnesini kontrollü değiştirip onararak tamper kontrolü yapar. Üretim verisinde çalıştırmayın.

CI SQLite ve PostgreSQL üzerinde API+gerçek ajan testlerini, web build'ini ve Chromium iş akışlarını çalıştırır. Windows self-contained ajan ve ekran görüntüleri 7 günlük artifact'lardır. DB, master key ve outbox artifact'a konmaz.

## Sınırlar ve sonraki adımlar

Bu sürüm **tek çalışma alanlı, tek API instance'lı sürümlü yedekleme** içindir; Dropbox benzeri çift yönlü canlı dosya sistemi veya çok kiracılı SaaS değildir. Yönetici ve kaynak yetkileri vardır; kullanıcı daveti/OIDC henüz yoktur.

- En fazla 50 cihaz, 2000 dosya/snapshot, 64 MiB/dosya, 256 MiB/snapshot ve 1 GiB kayıtlı benzersiz parça. UI son 200 sürümü listeler.
- Sabit boyutlu chunking kullanılır; içerik tanımlı Rabin chunking değildir. Dosya içi ekleme kaymaları yeniden aktarımı artırabilir.
- Boş klasörler, ACL, alternate streams ve symlink hedefleri yedeklenmez. Dosya yoluna göre case-insensitive çakışma reddi uygulanır.
- Metadata ilk sürüm şeması `EnsureCreated` ile kurulur; sonraki şema değişiminden önce migration planı gerekir. Otomatik destructive upgrade yapılmaz.
- Saklama politikası **önizlemedir**; otomatik sürüm silme/GC uygulanmaz. Başarısız yüklemenin sahipsiz parçaları kotada kalabilir. Yerel conflict manifest/cache temizliği operatör incelemesi gerektirir.
- Sunucu master key'i nesne deposunun yanındaki `master.key` dosyasındadır; server-side encryption E2EE değildir. Metadata ve master key ayrıca güvenli yedeklenmeli; anahtar kaybında kurtarma garanti edilmez.
- ZIP/restore ve doğrulama dosya verisini bellekte işler; sınırlar kasıtlıdır. Büyük ölçek, distributed lease/outbox, KMS ve stream restore sonraki sürüm işidir.
- Ajan console/servis dostudur; native masaüstü GUI veya mobil uygulama yoktur. Fiziksel cihaz/Docker-S3 dağıtımı ayrıca doğrulanmalıdır.

Geliştirici: **Berkay Özdemir**.
