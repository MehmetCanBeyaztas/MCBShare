# MCBShare protokolü

PC'deki sunucu ile telefon (web sayfası, iOS Kısayolu, HTTP Shortcuts) arasındaki kurallar.
Varsayılan adres `http://<pc-ip>:8765`. Sunucu yalnızca özel/yerel IP adreslerinden gelen istekleri kabul eder;
diğerlerine `403 not_local` döner.

## Kimlik

Kayıtlı cihazlar her istekte `X-MCB-Key: <anahtar>` header'ı gönderir. Anahtar cihaz kaldırılana kadar değişmez;
sunucu yalnızca özetini (SHA-256) saklar.

Eşleşme QR'ı `http://<pc-ip>:8765/#pair=<token>` adresini taşır. Token `#` sonrasında olduğu için sunucuya ve
loglara URL olarak gitmez.

### `POST /api/register`

```json
{ "token": "<eşleşme kodu>", "name": "Ayşe'nin iPhone'u" }
```

`200 { "deviceId": "...", "name": "...", "key": "..." }`. Yanlış token: `401 bad_token`.

### `GET /api/me`

Anahtarı doğrular. `200 { "deviceId": "...", "name": "..." }`.

## Telefondan PC'ye

### `POST /upload`

Gövde dosyanın ham baytlarıdır. Web sayfası ve kısayol aynı uç noktayı kullanır.

| Parametre | Zorunlu | Anlam |
| --- | --- | --- |
| `name` (sorgu) veya `X-MCB-Filename` (header) | evet | Dosya adı. Header'da URL kodlu yazılır. |
| `size` | `id` varsa evet | Toplam bayt |
| `id` | hayır | İstemcinin ürettiği yükleme kimliği, `[A-Za-z0-9_-]{8,64}`. Varsa parçalı yükleme yapılır. |
| `offset` | hayır | Bu parçanın başlangıcı, varsayılan 0 |
| `modified` | hayır | Dosya tarihi, Unix milisaniye. EXIF çekim tarihi varsa o kazanır. |
| `batchIndex`, `batchTotal` | hayır | Toplu gönderimde sıra (1'den başlar) ve toplam; "3/10" gösterimi için |

Yanıtlar:

- Parça alındı, dosya bitmedi: `200 { "complete": false, "offset": 8388608 }`
- Dosya bitti: `200 { "complete": true, "offset": N, "name": "foto (1).jpg", "duplicate": false }`
- `duplicate: true`: aynı içerik zaten alınmış, yeni kopya oluşturulmadı.

### `GET /upload/status?id=<id>`

`200 { "offset": N }`. Bağlantı koptuysa istemci buradan öğrendiği konumdan devam eder.

## PC'den telefona

Paylaşım kodu anahtar yerine geçer; bu uç noktalar `X-MCB-Key` istemez.

- `GET /d/<kod>`: tek dosyada indirme, çok dosyada indirme listesi sayfası.
- `GET /d/<kod>/<sıra>`: listedeki dosya (0'dan başlar). `Range` desteklenir.

Kod oluşturulduktan 15 dakika sonra, indirme başladıysa başlangıçtan 5 dakika sonra ölür. Ölü kod: `410`.
Başlamış bir indirme kod ölünce kesilmez.

## Pano

- `POST /api/clipboard` `{ "text": "..." }`: metni PC'ye gönderir. En fazla 1 MB.
- `GET /api/clipboard`: PC'nin en son gönderdiği metin, `{ "text": "...", "at": <unix ms> }`. Yoksa `text` boştur.
  Metin 15 dakika tutulur. Sayfa açıkken bu adresi 3 saniyede bir sorar; PC, metni her cihazın ilk alışını gösterir.

## Tanılama

`POST /api/clientlog` `{ "message": "..." }`: Telefondaki sayfanın gördüğü aktarım sorununu PC'nin loguna yazar.
En fazla 500 karakter saklanır.

## Hata biçimi

```json
{ "error": "disk_full", "message": "..." }
```

| Durum | `error` | Anlam |
| --- | --- | --- |
| 400 | `bad_request` | Eksik veya geçersiz parametre |
| 401 | `bad_key`, `bad_token` | Anahtar veya eşleşme kodu yanlış |
| 403 | `not_local` | İstek yerel ağdan gelmiyor |
| 404 | `not_found` | Böyle bir kod veya dosya yok |
| 409 | `offset_mismatch` | `offset` sunucudakiyle uyuşmuyor; yanıtta `offset` alanı doğrusunu verir |
| 409 | `upload_busy` | Bu yükleme başka bir bağlantıda sürüyor |
| 410 | `expired` | Paylaşım kodunun süresi doldu |
| 413 | `too_large` | Metin 1 MB'tan büyük |
| 429 | `too_many_attempts` | Çok fazla başarısız deneme; bir dakika bekleyin |
| 500 | `internal_error` | Beklenmeyen hata. Gelen baytlar `.part` olarak saklanır; aynı `id` ile yeniden denenebilir. |
| 507 | `disk_full` | PC'de yer yok |

Bağlantı hiç kurulamazsa PC kapalı, uykuda, başka ağda veya sunucu güvenilir olmayan ağ nedeniyle durmuş demektir.

## Bilinen sınır

Trafik yerel ağda düz HTTP'dir; aynı ağı dinleyen biri anahtarı ve dosyaları görebilir. Bu yüzden sunucu yalnızca
kullanıcının onayladığı Wi-Fi ağlarında çalışır.
