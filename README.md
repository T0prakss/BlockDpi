# BlockDpi

Windows ve macOS için geliştirilmiş, tek tuşla çalışan yerel DPI ve DNS engel aşma aracı.

Trafiği uzak bir sunucuya yönlendirmez (VPN değildir). Paketleri yalnızca cihazdan çıkarken yerel olarak işlediği için hız kaybı veya oyunlarda ping artışı yapmaz.

## Özellikler

- **Şifreli DNS (DoH):** UDP 53 DNS engellerini ve sahte IP yönlendirmelerini Cloudflare (`1.1.1.1`) ve Google (`8.8.8.8`) üzerinden aşar.
- **TLS / HTTP Paket Bölme:** `ClientHello` paketlerini SNI orta noktasından böler, düşük TTL'li sahte paket gönderir ve QUIC (`UDP 443`) trafiğini TCP'ye yönlendirir.
- **Windows Desteği:** `WinDivert` sürücüsü ile çekirdek düzeyinde tüm sistem trafiğinde çalışır.
- **macOS Desteği:** Yerel DoH DNS, `pfctl` şeffaf yönlendirme ve HTTP/HTTPS/SOCKS5 katmanı ile hem tarayıcılarda hem de masaüstü uygulamalarda (Discord, Roblox vb.) çalışır.
- **Düşük Kaynak Tüketimi:** Arka planda (sistem tepsisinde) yalnızca **~2 - 6 MB RAM** ve **%0 CPU** kullanır; oyunlarda FPS veya sistem performansı kaybı oluşturmaz.
- **Otomatik Güncelleme:** GitHub üzerinden yeni sürümleri kontrol eder; isteğe bağlı tek tuşla veya otomatik güncellenir.
- **Ek Ayarlar:** Sadece seçili alan adlarında çalışma (Site Listesi), özel DNS tanımlama, sistem başlangıcında açılma ve sistem tepsisi (tray) desteği.

## Kurulum

### Windows
`dist/windows-x64/BlockDpi.exe` dosyasını yönetici olarak çalıştırıp ortadaki güç butonuna basın. İlk çalıştırmada `WinDivert.dll` ve `WinDivert64.sys` dosyaları klasörde yoksa otomatik indirilir.

### macOS
1. `BlockDpi.app` dosyasını `Applications` (Uygulamalar) klasörüne atın.
2. İlk açılışta Terminal üzerinden izinleri verin:
   ```bash
   xattr -cr /Applications/BlockDpi.app && chmod +x /Applications/BlockDpi.app/Contents/MacOS/BlockDpi && open /Applications/BlockDpi.app
   ```
3. Uygulama açıldığında ortadaki güç butonuna basın.

## Derleme

Projeyi derlemek için .NET 10 SDK gereklidir:

```powershell
powershell -ExecutionPolicy Bypass -File .\build\publish.ps1
```

Çıktılar `dist/` klasörü altında (`windows-x64`, `macos-osx-arm64`, `macos-osx-x64`) oluşturulur.

> **Not:** Bu projenin geliştirme sürecinde yapay zekadan (AI) yardım alınmıştır, bazı hatalar veya eksikler bulunabilir.

## Geliştirici

[@T0prakss](https://github.com/T0prakss)
