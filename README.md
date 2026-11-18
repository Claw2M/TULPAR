# TULPAR Mission Planner (v1.0.0)

SpaceX-style high-contrast Ground Control Station (GCS) for **ArduPilot & PX4** autonomous vehicles (Multirotor, Fixed-Wing, VTOL, Rover/İKA, Boat/İDA), built on **.NET 10 WPF (Windows x64)** and **Android (Landscape Phone & Tablet)**.

## İndirme / Kurulum (Releases)

- **Windows 10/11 (x64) Tek Dosya Kurulum Sihirbazı:** `Tulpar-Mission-Planner-Setup.exe`
- **Android (Telefon Yatay & Tablet) APK:** `Tulpar-Mission-Planner.apk`

## Öne Çıkan Özellikler

- **2× HiDPI Uydu & Hibrit Harita Motoru (Zoom 21):** `512×512 px` (`scale=2`) Google Uydu HD, Google Hibrit HD, Esri Clarity, OSM ve OpenTopoMap katmanları + SQLite (`tiles.db`) çevrimdışı önbellek ve `.mbtiles` desteği.
- **Çift Onaylı Uçuş Emniyeti (Modal Onay Popup):** `ARM`, `DISARM`, `KALKIŞ (TAKEOFF)` (otomatik `STABILIZE → GUIDED` geçişli), `▶ GÖREVİ BAŞLAT` (`AUTO + MAV_CMD_MISSION_START`), `⏸ GÖREVİ DURDUR` (`HOLD / LOITER`), `İNİŞ (LAND)`, `↩ EVE DÖN (RTL)` ve `GUIDED Yönlendirme` komutları için ekran ortası güvenlik onay penceresi.
- **Görsel Görev Planlayıcı:** Haritadan etkileşimli waypoint ekleme/sürükleme, görev hızı (`m/s`, varsayılan `3 m/s` `MAV_CMD_DO_CHANGE_SPEED`), hazır tarama/spiral/yörünge şablonları, `DO_JUMP` döngü bağlama, arazi/irtifa profili ve QGC WPL 110 içe/dışa aktarma.
- **3 Form Faktörlü Duyarlı Kokpit:** Masaüstü (`Desktop`), Tablet (`Tablet`) ve Telefon Yatay (`PhoneLandscape`) görünümleri arasında otomatik ölçeklenme.
- **Canlı Video & Telemetri:** LibVLC (`RTSP / UDP / RTP / DirectShow`) düşük gecikmeli kamera akışı, PFD yapay ufuk HUD, `.tlog` kayıt/oynatma (Replay) ve tam MAVLink v2 protokol motoru.

## Derleme & Test

```powershell
dotnet test Tulpar.sln
dotnet publish src/Tulpar.App/Tulpar.App.csproj -c Release -r win-x64 --self-contained true -o ./publish/Tulpar-win-x64
```
