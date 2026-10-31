# MCBShare

Aynı Wi-Fi'deki telefon ile Windows 11 PC arasında, telefona uygulama kurmadan ve bulut kullanmadan iki yönlü dosya aktarımı.

## Yapı

- `src/MCBShare.Core`: Arayüzden bağımsız sunucu ve iş mantığı, telefon web sayfası (`Web/`)
- `src/MCBShare.App`: WinUI 3 uygulaması; tepsi, panel, aktarım kutusu, QR, ayarlar
- `tests/MCBShare.Core.Tests`: Core testleri
- `docs/PROTOCOL.md`: Telefonla konuşma kuralları

## Geliştirme

.NET 10 SDK yeterlidir.

```bash
dotnet test tests/MCBShare.Core.Tests
```

```bash
dotnet run --project src/MCBShare.App
```

Uygulama tepside açılır. Sunucu, panelden "Bu ağa güven" denene kadar kapalıdır. İlk açılışta Windows Güvenlik Duvarı izin isterse "Özel ağ"a izin verin.

Debug derlemesinde kullanılan ortam değişkenleri:

| Değişken | Etki |
| --- | --- |
| `MCBSHARE_DATA` | Ayar ve geçmiş klasörünü değiştirir |
| `MCBSHARE_BIND` | Sunucunun dinlediği adres, ör. `127.0.0.1` |
| `MCBSHARE_DEV=1` | Panel odak kaybında kapanmaz, aktarım kutusu tam ekranda da çıkar (yalnızca Debug) |

## Sürüm yayımlama

`vX.Y.Z` etiketi gönderildiğinde `.github/workflows/release.yml` testleri çalıştırır, uygulamayı derler ve Velopack paketini GitHub Releases'a yükler.

```bash
git tag v0.1.1
```

```bash
git push origin v0.1.1
```

Kurulu uygulama açılıştan 1 dakika sonra ve 6 saatte bir denetler. Yeni sürümü indirir; panel ve ayarlar kapalıyken, aktarım ve paylaşım yoksa kurup yeniden başlar. Kurulum `%LOCALAPPDATA%\MCBShareApp`, veriler `%LOCALAPPDATA%\MCBShare` altındadır.

Paket imzasızdır; ilk kurulumda SmartScreen uyarısı çıkabilir.

## Henüz yapılmayanlar

- Windows 11 sağ tık menüsü. Hazırlık: `MCBShare.exe --share <dosyalar>` çalışan kopyaya iletilir.
- Microsoft Store (MSIX) sürümü ve Store materyali
- Android HTTP Shortcuts ayar dosyası, mDNS
