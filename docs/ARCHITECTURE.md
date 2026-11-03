# API ve tutarlılık

Tek kasa; yönetici HTTP cookie veya `X-Vault-Key`, cihazlar yalnız `X-Vault-Key`. Yönetici anahtarı ortamda, cihaz anahtarının SHA-256 özeti metadata'da tutulur. Opaque web session 8 saat ve bellektedir. Restart sonrası giriş gerekir. Cookie HttpOnly/SameSite Strict; cookie yazmalarında Origin kontrolü, login 15/IP/dakika. Üretimde TLS zorunludur.

| Metot  | Yol                               | İşlev                                           |
| ------ | --------------------------------- | ----------------------------------------------- |
| GET    | /health                           | Canlılık / şema sürümü                          |
| POST   | /api/login                        | `{key}` web oturumu                             |
| POST   | /api/logout                       | Cookie oturumunu iptal                          |
| GET    | /api/overview                     | Yönetici metrikleri ve günlüğü                  |
| POST   | /api/devices                      | `{name}` → bir kez gösterilen cihaz anahtarı    |
| DELETE | /api/devices/{id}                 | Anahtarı iptal, yedekleri koru                  |
| GET    | /api/device                       | Cihaz kimliği ve head                           |
| POST   | /api/chunks/missing               | En fazla 512 hash                               |
| PUT    | /api/chunks/{sha256}              | Binary 1 byte–1 MiB, içerik hash denetimi       |
| POST   | /api/snapshots                    | `{key,parent,files:[{path,bytes,hash,chunks}]}` |
| GET    | /api/snapshots                    | Yetkili sürümler, son 200                       |
| GET    | /api/snapshots/{id}               | Dosya manifest'i                                |
| GET    | /api/snapshots/{id}/diff          | Parent ile ekleme/değişme/silme karşılaştırması |
| POST   | /api/snapshots/{id}/verify        | Tüm içeriği yeniden doğrula                     |
| GET    | /api/snapshots/{id}/file?path=... | Doğrulanmış dosya indirme                       |
| GET    | /api/snapshots/{id}/archive       | Doğrulanmış ZIP                                 |
| GET    | /api/retention/preview?keep=5     | Silmeden politika adayları                      |

HTTP: 400 girdi, 401 auth, 403 origin, 404 yetkili kaynak bulunamadı, 409 head/idempotency/kota, 422 içerik bütünlüğü, 429 login limiti. Mutasyonlar tek instance semaphore ile sıralanır; head EF concurrency token'ıdır. Snapshot, head ve audit aynı EF transaction'ında commit edilir. Nesne yüklemesi ayrı dayanıklılık sınırıdır; metadata'sı olmayan nesne process crash'te kalabilir. Çok instance garantisi yoktur.

Manifest'e girmeden önce tüm chunk'lar okunur ve her dosya hash/boyutu doğrulanır. Aynı cihaz/key/parent/içerik tekrarı önceki snapshot'ı döndürür; farklı içerik 409. Bir snapshot kaynak klasörün tam görünümüdür: sonradan silinen dosya yeni sürümde bulunmaz ama eski sürümde geri alınabilir. Cihazlar birbirinin kaynaklarını birleştirmez.

Metadata: Devices, Chunks, Snapshots, Audits. Chunks içerik-adresli ortak kasadır; kaynak anahtarları aynı güven sınırının ajanlarıdır. Hash varlık sorgusu ortak dedup bilgisini açığa çıkarabilir; çok tenant izolasyonu iddiası yoktur.

AES envelope: 12-byte nonce + 16-byte tag + ciphertext. AAD=ASCII SHA-256. Anahtar server-side 32-byte dosyadır. Plaintext anahtar veya nesne HTTP response'una alınmaz; dosya restore sırasında yetkili istemciye plaintext aktarılır. Anahtar kaybı bütün şifreli nesneleri etkiler. SQLite metadata + object files + master.key veya PostgreSQL dump + S3 objects + master.key birlikte yedeklenmelidir.
