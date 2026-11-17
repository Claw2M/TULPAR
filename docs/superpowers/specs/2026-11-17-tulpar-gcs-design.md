# Tulpar Mission Planner — Tasarım Dokümanı

**Tarih:** 2026-11-17  
**Hedef Platform:** Windows 10/11 Masaüstü (`.NET 10`, `net10.0-windows`)  
**Görsel Tasarım Referansı:** `c:\Users\Yiğit Tarhan\Downloads\DESIGN-spacex.md` (SpaceX havacılık tasarım dili)

---

## 1. Amaç ve Neden

**Tulpar Mission Planner**, hem sahadaki gerçek uçuşlarda (USB veya Telemetri Radyosu üzerinden) hem de yazılım/simülasyon (SITL — UDP/TCP) ortamlarında Pixhawk ailesi otopilotlarla (**ArduPilot** ve **PX4**) çalışan, otonom rota planlama, canlı telemetri/HUD takibi, canlı kamera akışı ve uçuş komuta yeteneklerine sahip yerli bir Windows Yer Kontrol İstasyonudur (YKS).

Tek bir araç tipine bağlı kalmak yerine, MAVLink `HEARTBEAT` mesajından araç tipini (`MAV_TYPE`) ve otopilot yazılımını (`MAV_AUTOPILOT`) otomatik algılayarak arayüzü, HUD göstergelerini, uçuş modlarını ve görev komutlarını bağlanan platforma göre dinamik uyarlar. Bugün **Sabit Kanat (SİHA)**, **Multirotor (Drone)** ve **VTOL** araçlarını desteklerken; soyut araç profili mimarisi sayesinde ileride **İKA (Rover)** ve **İDA (Surface Boat)** platformlarını da ek uyarlama gerektirmeden aynı kokpite bağlayabilir.

---

## 2. Hedefler, Kapsam Dışı (Non-Goals) ve Kaçınılacaklar (Anti-Goals)

### Hedefler
1. **Çoklu Bağlantı Katmanı:**
   - Gerçek donanım için **Seri Port (COM)** (USB kablosu ve radyo telemetri modülleri, seçilebilir baud rate: 57600, 115200, 921600 vb.).
   - Simülasyon (SITL / Mission Planner / QGC / Gazebo) ve companion computer bağlantıları için **UDP** (dinleme veya hedef IP:Port) ve **TCP** istemcisi.
2. **MAVLink v1 & v2 Protokol Desteği ve Otomatik Algılama:**
   - `HEARTBEAT` üzerinden `MAV_TYPE` (Fixed Wing, Quad/Hexa/Octocopter, VTOL, Rover/UGV, Boat/USV) ve `MAV_AUTOPILOT` (ArduPilotMega, PX4) otomatik tanıma.
   - Bağlanınca otomatik veri akışı talebi (`REQUEST_DATA_STREAM` / `SET_MESSAGE_INTERVAL`).
   - Bağlantı kaybı (heartbeat timeout > 3s) algılama ve otomatik yeniden bağlanma/uyarı.
   - *(Karar)* **MAVLink2 `incompat_flags`:** v1 kapsamında standart çerçeve uyumluluğu sağlanır (bilinmeyen zorunlu `incompat_flags` bitleri güvenli şekilde reddedilip günlüğe yazılır); SHA-256 MAVLink2 Paket İmzalama (Packet Signing) v1 kapsamı dışındadır.
3. **Adaptif Araç Profili Motoru (`IVehicleProfile`):**
   - Bağlanan araca göre uçuş modu haritasını, HUD göstergelerini, hız/irtifa birincil metriklerini ve izin verilen otonom görev komutlarını otomatik belirleme.
4. **SpaceX Estetiğinde Modüler Kokpit Arayüzü:**
   - Tam siyah (`#000000`) ve hafif yükseltilmiş siyah (`#0a0a0a`) yüzeyler, 1px `#3a3a3f` ince çizgiler, `D-DIN` büyük harf tipografi, pill-şekilli hayalet butonlar (`button-ghost-on-dark`), sıfır gölge/gradyan.
   - **Etkileşimli Harita (MapLibre GL JS):** Canlı araç konumu, yönelim oku, uçuş izi, Home noktası, geofence çokgeni ve sürükle-bırak waypoint çizimi; ayrıca uçuş sırasında harita sağ-tık menüsünden onay korumalı canlı yönlendirme (**"Buraya Git / Burada Tur At"** — Guided/Reposition).
   - **Çevrimdışı Harita Önbelleği ve `.mbtiles` Desteği:** Tüm harita karolarını yerel SQLite önbelleğine kaydeden yerel HTTP proxy + saha öncesi dikdörtgen bölge indirme aracı (hız sınırlı, açık kaynaklı Uydu/Sokak/Topografik veya özel XYZ şablon URL desteği) + harici **raster `.mbtiles`** dosyası içe aktarma.
   - **Adaptif HUD (Primary Flight Display):** 60 FPS vektörel yapay ufuk (roll/pitch), pusula şeridi, hız & irtifa şeritleri, dikey hız (VSI), pil ve mod bindirmeleri.
   - **Canlı Kamera Akışı:** RTSP / UDP / RTP ağ akışları ve Windows USB görüntü yakalama cihazları (`DirectShow`) için düşük gecikmeli video; harita üzerinde PiP pencere ve tek tıkla harita–kamera yer değiştirme.
   - **Otonom Rota Planlama, Geofence & Komutlar:** MAVLink Mission Protocol durum makinesi (`MISSION_COUNT`, `MISSION_REQUEST_INT`, `MISSION_ITEM_INT`, `MISSION_ACK`, `MISSION_REQUEST_LIST`, `MISSION_CLEAR_ALL`) ile hem **Görev (`MAV_MISSION_TYPE_MISSION`)** hem **Otopilot Geofence (`MAV_MISSION_TYPE_FENCE`)** yükleme/okuma/temizleme + harita üzerinde yerel ihlal uyarısı; `.waypoints` / JSON dosya kaydetme-yükleme; Arm/Disarm, Mod Değiştirme, Kalkış, İniş, acil `RTL` ve faydalı yük/servo (`DO_SET_SERVO`) komutları.
   - **Kayıt ve Dışa Açık Veri Akışı:** MAVLink `STATUSTEXT` uyarı akışı, otomatik `.tlog` + `.csv` telemetri kaydı ve ileride dış modüller/yarışma betikleri bağlanabilmesi için hem `Tulpar.Core` içi event/observable telemetri veri yolu hem de opsiyonel yerel **UDP MAVLink Yansıtıcı (Forwarder)** çıkışı.

### Kapsam Dışı (Non-Goals)
- Aynı anda birden fazla araca eşzamanlı bağlanma / sürü yönetimi (tek aktif araç kokpiti olarak tasarlanır).
- Pixhawk'a sıfırdan firmware yükleme (bootloader flashing) veya 1000+ parametrelik tam PID ayar ekranları.
- macOS veya Linux masaüstü desteği (yalnızca Windows masaüstü).
- Yarışma sunucusu (ör. Teknofest kilitlenme sunucusu) gibi dış HTTP protokolleri (ancak UDP MAVLink yansıtıcı ve iç telemetri veri yolu sayesinde dış modüller COM port kilidine takılmadan eklenebilir).
- Arazi takip (Terrain-following DEM / `TERRAIN_REQUEST`) motoru ve MAVLink2 SHA-256 Paket İmzalama (v1'de görev irtifaları **Home'a Göre Bağıl (`Relative-to-Home`)** varsayılanıyla, isteğe bağlı **AMSL** seçimiyle çalışır).

### Kaçınılacaklar (Anti-Goals)
- Araç tipini veya otopilot mod numaralarını arayüze sabit kodlamak (her zaman `IVehicleProfile` üzerinden çözümlenir).
- Seri port, UDP veya video çözümleme işlemlerinin UI thread'ini kilitlemesi.
- `MISSION_ACK` doğrulaması ve zaman aşımı tekrarları olmadan görev yüklenmiş gibi davranmak.
- Harita veya videonun tamamen internete bağımlı kalması (çevrimdışı sahada çalışmaz hale gelmesi).
- `DESIGN-spacex.md` sistemini bozan renkli kartlar, gölgeler, parlak gradyanlar veya karışık fontlar kullanmak.

---

## 3. Kısıtlar ve Görsel Tasarım Sistemi (`DESIGN-spacex.md` Uyumu)

Uygulamanın tüm görsel dili `DESIGN-spacex.md` ile uyumlu tutulur:

- **Renk Paleti:**
  - Ana zemin (`canvas-night`): `#000000`
  - İkincil panel yüzeyi (`canvas-night-soft`): `#0a0a0a`
  - 1px kenarlık (`hairline-on-dark`): `#3a3a3f`
  - Ana metin (`on-primary`): `#ffffff`
  - İkincil metin (`on-primary-mute`): `#f0f0fa` (soluk/etiket değerler için `#8e8e93` yalnızca ikincil telemetri birimlerinde)
  - *Uçuş Güvenliği İstisnası:* Havacılık emniyeti gereği yalnızca kritik alarmlar (`ARMED`, kritik batarya, link kaybı, `RTL` acil butonu kenarlığı) yüksek kontrastlı durum rengi (`#FF3B30` kritik, `#FFCC00` uyarı) kullanır; bunun dışındaki tüm UI kromu katı siyah–beyazdır.
- **Tipografi:**
  - `D-DIN-Bold` (başlıklar, büyük telemetri sayıları, `button-cap` 13px / tracking `1.17px`, tümü BÜYÜK HARF) ve `D-DIN` (gövde `16px` / `micro-cap` `12px` tracking `0.96px`).
  - Font dosyaları uygulama paketine gömülür; bulunamadığında `Arial Narrow`, `Arial`, `Verdana` yedek zinciri kullanılır. Sayısal telemetrilerde rakam zıplamasını önlemek için sabit genişlikli (tabular) hizalama uygulanır.
- **Şekiller ve Derinlik:**
  - Drop shadow, bulanıklık (blur) ve gradyan bindirmesi **yoktur**. Derinliği harita uydu görüntüsü, canlı kamera akışı ve 1px `#3a3a3f` çizgiler sağlar.
  - Kenar yuvarlaklığı: Girdi kutuları `4px` (`rounded.xs`), harita/video çerçeveleri `8px` (`rounded.sm`), ana aksiyon butonları `32px` (`rounded.pill`, `1px solid #ffffff` hayalet çerçeve).

---

## 4. Değerlendirilen Yaklaşımlar ve Seçilen Mimari

1. **Seçilen Yaklaşım — C# / .NET 10 WPF + WebView2 (MapLibre GL JS) + LibVLCSharp + Yerli MAVLink2 Motoru:**
   - Bilgisayarda `.NET 10.0.401` SDK ve Edge WebView2 Runtime (`157.x`) hazır kurulu.
   - Windows'ta seri port (`System.IO.Ports`) ve düşük gecikmeli UDP/TCP soketleri için en kararlı ekosistemdir.
   - *(Karar)* **Paketleme / Dağıtım:** `LibVLCSharp` (`libvlc\win-x64\plugins`), `WebView2Loader.dll` ve `e_sqlite3.dll` yerel eklenti klasörlerine ihtiyaç duyduğundan, tek dosya paketlemenin LibVLC eklentilerini bozmasını önlemek için uygulama **taşınabilir Windows klasörü (`Tulpar.exe` + yanındaki native DLL/plugin dizinleri, tek tıkla çalışan self-contained portable build)** olarak yayınlanır (istenirse ileride Inno Setup / MSIX tek kurulum paketine sarılır).
   - WPF `DrawingContext` üzerinde donanım hızlandırmalı 60 FPS vektörel HUD çizilir.
   - Harita yerel C# HTTP tile proxy + SQLite önbelleğiyle internet kesildiğinde bile sıfır kesintiyle çalışır.
   - `LibVLCSharp.WPF` ile RTSP, UDP/RTP ve USB `DirectShow` kameralar ek dış kurulum gerektirmeden oynatılır.
2. **Reddedilen Alternatif 1 — Tauri (Rust + React/TypeScript):**
   - WebView içinde düşük gecikmeli RTSP/UDP/USB kamera oynatmak için ek WebRTC/GStreamer medya köprüsü gerektirir; Windows kurulum ve hata ayıklama karmaşıklığını gereksiz artırır.
3. **Reddedilen Alternatif 2 — Python (PySide6 + pymavlink + OpenCV/GStreamer):**
   - Tek `.exe` paketlemesi ağır ve sürücü bağımlılıkları (GStreamer/Qt WebEngine) açısından Windows dağıtımında kırılgandır.

---

## 5. Sistem Mimarisi ve Bileşenler

Çözüm (`Tulpar.sln`) 4 projeden oluşur:

### 5.1 `Tulpar.Core` (`net10.0` — UI Bağımsız Çekirdek)
- **Transport Katmanı (`ITransport`):**
  - `SerialTransport`: COM port tarama, baud rate, DTR/RTS seçeneği, asenkron okuma/yazma döngüsü.
  - `UdpTransport`: SITL (varsayılan dinleme `0.0.0.0:14550` veya hedef IP:Port) ve radyo köprüleri.
  - `TcpTransport`: SITL / Mission Planner TCP (`127.0.0.1:5760` vb.) bağlantısı.
- **MAVLink Codec (`MavlinkParser`, `MavlinkEncoder`):**
  - MAVLink v1 (`0xFE`) ve MAVLink v2 (`0xFD`) paket ayrıştırma, X.25 CRC doğrulama, v2 zero-truncation ve temel `incompat_flags` uyumluluğu (desteklenmeyen `MAVLINK_IFLAG_SIGNED` vb. bayrakları güvenli şekilde raporlayıp yoksayma).
  - Gerekli `common` ve `ardupilotmega` mesaj/enum seti: `HEARTBEAT`, `SYS_STATUS`, `SYSTEM_TIME`, `GPS_RAW_INT`, `GLOBAL_POSITION_INT`, `ATTITUDE`, `VFR_HUD`, `BATTERY_STATUS`, `RC_CHANNELS`, `RADIO_STATUS`, `STATUSTEXT`, `COMMAND_LONG`, `COMMAND_ACK`, `SET_MODE`, `REQUEST_DATA_STREAM`, `SET_POSITION_TARGET_GLOBAL_INT`, `MISSION_COUNT`, `MISSION_REQUEST_INT`, `MISSION_REQUEST`, `MISSION_ITEM_INT` (`mission_type`: `MISSION` ve `FENCE`), `MISSION_ACK`, `MISSION_REQUEST_LIST`, `MISSION_CLEAR_ALL`, `MISSION_CURRENT`, `MISSION_SET_CURRENT`, `HOME_POSITION`.
- **Dışa Açık Veri Yolu & UDP Yansıtıcı (`TelemetryBus` & `MavlinkUdpForwarder`):**
  - `Tulpar.Core` içinde reaktif telemetri akışı (`TelemetrySnapshot` event/observable veri yolu) sunulur.
  - Ek olarak ayarlanabilir bir **UDP MAVLink Forwarder** (ör. `127.0.0.1:14551` salt-okunur telemetri yayını) ile COM port tekil kilidini bozmadan harici betiklere ham MAVLink akışı paylaşılabilir.
- **Araç Profili Sistemi (`IVehicleProfile` & `VehicleProfileFactory`):**
  - `HEARTBEAT.type` (`MAV_TYPE`) ve `HEARTBEAT.autopilot` (`MAV_AUTOPILOT`) değerlerine göre aktif profil seçilir:
    - `FixedWingProfile` (`MAV_TYPE_FIXED_WING`) — SİHA
    - `MultirotorProfile` (`MAV_TYPE_QUADROTOR`, `HEXAROTOR`, `OCTOROTOR`, `TRICOPTER`, `HELICOPTER`) — Drone
    - `VtolProfile` (`MAV_TYPE_VTOL_*`) — VTOL
    - `RoverIkaProfile` (`MAV_TYPE_GROUND_ROVER`) — İKA (hazır profil)
    - `BoatIdaProfile` (`MAV_TYPE_SURFACE_BOAT`) — İDA (hazır profil)
  - Her profil şunları sağlar:
    - Platform sınıfı etiketi (`SİHA / SABİT KANAT`, `DRONE / QUADROTOR`, `VTOL`, `İKA / ROVER`, `İDA / YÜZEY`)
    - Otopilota özel uçuş modu tablosu (`ArduPlane` vs `ArduCopter` vs `ArduRover` vs `PX4` custom mode eşlemesi)
    - HUD yerleşim bayrakları (`ShowAirspeed`, `ShowHorizon`, `ShowAltitudeTape`, `ShowVtolTransition`, `SurfaceOnlyMode`)
    - Görev planlayıcıda sunulan uyumlu `MAV_CMD` komut listesi, varsayılan parametreler ve canlı harita yönlendirme (`Guided / Reposition`) komutu üreticisi (`COMMAND_LONG MAV_CMD_DO_REPOSITION` veya `SET_POSITION_TARGET_GLOBAL_INT` / ArduPilot Guided hedefi).
- **Görev & Geofence Protokol Yöneticisi (`MissionProtocolManager`):**
  - Hem `MAV_MISSION_TYPE_MISSION` (otonom rota) hem `MAV_MISSION_TYPE_FENCE` (dahil etme çokgeni/dairesi — `MAV_CMD_NAV_FENCE_POLYGON_VERTEX_INCLUSION` / `CIRCLE_INCLUSION`) için upload/download/clear desteği.
  - Araca yükleme (`UploadAsync`): `MISSION_CLEAR_ALL` opsiyonel -> `MISSION_COUNT(mission_type)` -> her `MISSION_REQUEST_INT` (veya eski `MISSION_REQUEST`) isteğine karşılık `MISSION_ITEM_INT` gönderimi -> `MISSION_ACK` (`MAV_MISSION_ACCEPTED`) doğrulaması.
  - Araçtan okuma (`DownloadAsync`): `MISSION_REQUEST_LIST(mission_type)` -> `MISSION_COUNT` -> sırayla `MISSION_REQUEST_INT(seq)` -> `MISSION_ACK`.
  - Her adımda `1500 ms` zaman aşımı ve en fazla `5` tekrar denemesi; ilerleme yüzdesi (`IProgress<MissionTransferProgress>`) raporlaması.
  - `.waypoints` (QGC WPL 110 — `MAV_FRAME_GLOBAL_RELATIVE_ALT_INT` ve `MAV_FRAME_GLOBAL_INT` AMSL desteği) ve `.tulpar.json` içe/dışa aktarma.
- **Kayıt Motoru (`TelemetryRecorder`):**
  - Bağlantı aktifken ham MAVLink paketlerini `.tlog` formatında ve özet telemetri satırlarını `.csv` olarak `Documents/Tulpar/Logs/` altına yazar.

### 5.2 `Tulpar.MapHost` (`net10.0` — Çevrimdışı Harita, `.mbtiles` ve Karo Sunucusu)
- `127.0.0.1` üzerinde dinamik boş portta çalışan gömülü yerel HTTP sunucusu.
- Uygulama içindeki gömülü **MapLibre GL JS** statik dosyalarını sunar (internet bağlantısı gerektirmez).
- Harita karoları için **Cache-First Tile Proxy & `.mbtiles` Desteği**:
  - Varsayılan harita katmanları: Uydu (Esri World Imagery), Sokak (OSM), Topografik (OpenTopoMap) + kullanıcı tanımlı özel XYZ URL şablonu.
  - İstek `GET /tiles/{provider}/{z}/{x}/{y}` geldiğinde önce yüklenmiş yerel `.mbtiles` katmanlarına (TMS Y-ekseni dönüşümü dahil) ve SQLite önbelleğine (`%LocalAppData%\Tulpar\tiles.db`) bakar; yoksa ve internet varsa indirip önbelleğe yazar.
  - **Bölge İndirme Aracı (`OfflineRegionDownloader`):** Kullanıcı haritada seçtiği dikdörtgen ve zoom aralığı (`z10–z18`) için karoları hız sınırlı (rate-limited, varsayılan maks. karo sınırı ve public sunucu uyarısı ile) önceden indirip ilerleme durumunu görür; ayrıca dışarıdan hazırlanmış (QGIS/MOBAC) raster `.mbtiles` dosyalarını doğrudan içe aktarabilir.

### 5.3 `Tulpar.App` (`net10.0-windows` — WPF Kokpiti)
- **Üst Komuta Barı (48px, `#000000`, alt 1px `#3a3a3f`):**
  - Bağlantı kontrolü: Tip seçimi (`USB / TELEMETRİ COM`, `UDP SITL`, `TCP SITL`), port/baud/adres alanı ve `BAĞLAN / KES` hayalet pill butonu.
  - Araç Kimlik Rozeti: Otomatik algılanan araç & otopilot (`SİHA · SABİT KANAT · ARDUPILOT` vb.).
  - Canlı Durum Şeridi: Uçuş Modu seçici, `ARMED / DISARMED` göstergesi, Batarya (`V / %`), GPS (`3D FIX · 14 UYDU`), Link (`%` / RSSI).
  - Acil Durum Aksiyonu: `RTL` (Eve Dön) ve `HOLD / LOITER` hızlı tetikleyici (çift onay korumalı kritik komutlar).
- **Sol Sütun (340px–380px, `#0a0a0a`, sağ 1px `#3a3a3f`):**
  - **Adaptif HUD (`HudControl`):** WPF vektör çizimi; yapay ufuk (yüksek kontrast teknik siyah-beyaz + ince ufuk çizgisi), roll yayı, pitch merdiveni, üst pusula şeridi, sol hız şeridi (`IAS/GS`), sağ irtifa/VSI şeridi (`ALT/VSI`). İKA/İDA modunda yapay ufuk yerine yönelim pusulası + yan yatış/hız göstergesine dönüşür.
  - **Alt Sekmeli Panel:**
    - `TELEMETRİ`: Büyük D-DIN sayısal kutular (İrtifa, Hava/Yer Hızı, Dikey Hız, Uzaklık/Home, Batarya, Akım, Eğim/Yaw, Gaz %).
    - `KOMUTLAR`: Arm/Disarm (güvenlik onaylı), Kalkış (hedef irtifa ile), İniş, Mod değiştirme, Servo/Faydalı Yük tetikleme (kanal & PWM), Home noktası güncelleme, UDP MAVLink yansıtıcı aç/kapat.
    - `GÜNLÜK`: MAVLink `STATUSTEXT` zaman damgalı sistem mesajları ve bağlantı olayları.
- **Ana Görünüm Alanı (Tam alan harita + PiP Kamera):**
  - **Harita (`MapWebView`):** MapLibre GL JS üzerinde canlı araç ikonu (araç tipine göre uçak/multirotor/VTOL/İKA/İDA SVG), heading vektörü, son 500 noktalı uçuş izi, aktif hedef waypoint çizgisi, sürükle-bırak waypoint & geofence çokgeni, yerel geofence ihlal uyarısı, sağ-tık onaylı **"Buraya Git / Burada Tur At"** yönlendirmesi ve üstte kayan küçük `ÇEVRİMDIŞI HARİTA / MBTILES` + `HARİTA KATMANI` kontrolü.
  - **Canlı Kamera (`VideoFeedControl`):** Sağ altta yeniden konumlandırılabilir/büyütülebilir `8px` köşeli PiP çerçeve (`RTSP`, `UDP/RTP`, `USB DirectShow` kaynak seçimi + `YER DEĞİŞTİR` butonu ile kamerayı ana ekrana, haritayı PiP'e alma).
- **Alt / Açılır Görev & Geofence Planlayıcı Çekmecesi:**
  - Araç profiline uyumlu waypoint tablosu (`#`, `KOMUT`, `REFERANS (REL / AMSL)`, `ENLEM`, `BOYLAM`, `İRTİFA (m)`, `PARAM 1..4`) ve ayrı **GEOFENCE** sekmesi (çokgen/daire sınırı).
  - `HARİTADAN EKLE`, `ARACA YÜKLE`, `ARAÇTAN OKU`, `TEMİZLE`, `DOSYADAN AÇ / KAYDET` aksiyonları.
- **Duyarlı (Responsive) Davranış:**
  - `>= 1280px`: Tam 2 sütunlu kokpit + PiP kamera.
  - `< 1280px`: Sol panel daraltılabilir/gizlenebilir çekmeceye dönüşür; üst bar kompakt rozet moduna geçer, harita ve HUD kesintisiz erişilebilir kalır.

### 5.4 `Tulpar.Tests` (`net10.0` — xUnit Test Paketi)
- MAVLink v1/v2 paket çözme, CRC doğrulama, `incompat_flags` uyumluluğu ve bozuk paket reddetme testleri.
- `HEARTBEAT` tabanlı araç & otopilot otomatik algılama, `IVehicleProfile` mod/komut haritalama ve canlı yönlendirme (`Guided / Reposition`) komutu üretim testleri (Fixed-Wing, Multirotor, VTOL, Rover, Boat × ArduPilot/PX4).
- `MissionProtocolManager` sahte transport üzerinde hem `MISSION` hem `FENCE` yükleme/okuma, zaman aşımı tekrar denemesi ve `MISSION_ACK` hata yönetimi testleri.
- `.waypoints` (WPL 110 — `REL` ve `AMSL`) ile JSON görev serileştirme testleri.
- `TileCache` SQLite okuma/yazma, `.mbtiles` (TMS Y-ekseni dönüşümü) ve çevrimdışı fallback testleri.
- `MavlinkUdpForwarder` ham paket yansıtma testleri.

---

## 6. Uygulamacıya (Builder) Bırakılan Detaylar

Planlama ve kodlama aşamasında aşağıdaki detaylar mühendislik kararına bırakılmıştır:
- Kesin NuGet paket sürümleri (`Microsoft.Web.WebView2`, `LibVLCSharp.WPF`, `VideoLAN.LibVLC.Windows`, `System.IO.Ports`, `Microsoft.Data.Sqlite`, `xunit`).
- `D-DIN` / `D-DIN-Bold` açık lisanslı TTF fontlarının projeye gömülmesi (veya ağ erişimi yoksa sistemde mevcut DIN / Arial Narrow fallback ile görsel tutarlılık).
- MapLibre GL JS paketinin yerel `wwwroot` klasörüne indirilecek sürümü ve çevrimdışı vektör/raster stil JSON detayları.
- LibVLCSharp için yerel testlerde video akışı yokken temiz "SİNYAL BEKLENİYOR / KAMERA SEÇ" bekleme durumu tasarımı.
