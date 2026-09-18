# Remvora Server — kendi sunucunuzda uzak masaüstü / self-hosted remote desktop

**TR:** Bilgisayarlarınızı ve Raspberry Pi cihazlarınızı tarayıcıdan yönetin: uzak masaüstü, terminal, dosya aktarımı ve sistem sesi; kullanıcı, cihaz ve erişim kontrolü sizin sunucunuzda. **Uygun paylaşımlı hosting üzerinde de kurulabilir.**

**EN:** Manage computers and Raspberry Pi devices from your browser: remote desktop, terminal, file transfer and system audio, with users, devices and access control on your own server. **Compatible shared hosting is supported.**

[Server](https://github.com/tahayildirm/remvora-server) · [Web panel](https://github.com/tahayildirm/remvora-web) · [Device agent](https://github.com/tahayildirm/remvora-agent) · [MIT](LICENSE)

| Başlamak istiyorum / Get started | Türkçe | English |
|---|---|---|
| Hosting → veritabanı → ilk Owner → giriş / Hosting → database → first Owner → login | [Adım adım ilk kurulum](docs/FIRST_INSTALL.tr.md) | [Step-by-step first installation](docs/FIRST_INSTALL.en.md) |
| Yönetim, ayarlar, güvenlik ve güncelleme / Administration, configuration, security and upgrades | [Tam rehber](docs/GUIDE.tr.md) | [Full guide](docs/GUIDE.en.md) |
| Doğrulanan özellikler ve sınırlar / Verified capabilities and limitations | [Durum](docs/STATUS.md) | [Status](docs/STATUS.md) |

## Türkçe

### Remvora nedir, kimin içindir?

Remvora, RDP benzeri **uzak masaüstü ve cihaz yönetimi** ihtiyacını tarayıcıdan karşılayan açık kaynak bir sistemdir. BT/destek ekipleri, okul ve kiosk yöneticileri, küçük işletmeler, farklı şubelerdeki cihazları yönetenler ve Raspberry Pi kullanıcıları için tasarlanmıştır. Yönetici bilgisayar, tablet veya telefon tarayıcısıyla bağlanır; yönetilecek cihazda agent çalışır.

Buradaki RDP ifadesi kullanım amacını anlatır: Remvora **Microsoft RDP protokolü uygulaması değildir**; `mstsc` ile bağlanılan bir RDP sunucusu değildir. Görüntü ve kontrol için WebRTC ile WebSocket kullanır.

### Neler yapabilirsiniz?

Aşağıdaki özellikler Server + Web + Agent birlikte çalıştığında sunulur; cihaz işletim sistemi ve yerelde verilen izinler geçerlidir.

| İmkân | Sağladığı kolaylık |
|---|---|
| Uzak masaüstü | Sıkıştırılmış görüntü, fare/klavye kontrolü, kısayollar ve desteklenen cihazlarda ekran seçimi |
| Gerçek terminal | Agent hesabının yetkileriyle shell/PTY; dizin istemi, mobil yardımcı tuşlar ve ayarlanabilir yazı boyutu |
| İki yönlü dosya aktarımı | İzin verilen paylaşım klasörüne yükleme/indirme; SHA-256 bütünlük kontrolü, uygun tarayıcıda sürükle-bırak/dosya yapıştırma |
| Metin panosu | Açık izin ve kullanıcı eylemiyle metin okuma/yapıştırma |
| Sistem sesi | Desteklenen agent platformlarında isteğe bağlı ses; tarayıcıda ses/sessiz ve seviye tercihini hatırlama |
| Bağlantıya göre görüntü | Otomatik FPS/bitrate/çözünürlük ayarı veya elle seçim; cihaz bazında tarayıcıda hatırlanan tercihler |
| Telefon ve tablet kullanımı | Varsayılan doğrudan dokunma, isteğe bağlı touchpad, iki parmakla yakınlaştırma, açılır araçlar ve desteklenen tarayıcılarda tam ekran |
| P2P + relay | Önce WebRTC doğrudan bağlantı; kurulamazsa API üzerinden WSS; bağlantı türü ve ilerleme göstergesi |
| Cihaz yaşam döngüsü | Kayıt tokenı, onay/etkinleştirme, gerçek çevrimiçi durum, erişimi iptal etme ve iki teyitli kalıcı silme |
| Kullanıcı ve yetki yönetimi | Organizasyon kapsamı, Owner/Admin/Operator/Viewer ve özel roller, cihaz grupları, profil/parola yönetimi |
| Yönetim ve güvenlik | TOTP ve kurtarma kodları, işlem geçmişi, API anahtarları, cihaz etiketleri ve konum/iletişim bilgileri |
| Size ait altyapı | PostgreSQL, MySQL veya MariaDB seçimi; MIT lisansı; zorunlu Remvora bulut hesabı yok |

Dil, terminal yazı boyutu ve ses tercihleri aynı tarayıcıda saklanır; farklı tarayıcılara otomatik eşitlenmez. Tarayıcı güvenlik kuralları bazı kısayolları, panoyu, otomatik sesi ve tam ekranı sınırlayabilir.

### Paylaşımlı hostinge nasıl kurulur?

API için **.NET 10 / ASP.NET Core, HTTPS, uzun süreli WebSocket, desteklenen DB, kalıcı özel anahtar deposu ve kurulum komutlarını çalıştırma imkânı** gerekir. Windows IIS/Plesk uygun şekilde yapılandırıldığında kullanılabilir. Panel statik dosyalardır; Node.js yalnız derleme bilgisayarında gerekir. Her paylaşımlı hosting paketi bu koşulları sağlamaz; PHP-only paket API için yeterli değildir.

Üç parça ayrı kurulur: **Server → API hosting**, **Web → panel hosting**, **Agent → yönetilecek cihaz**. VPS zorunlu değildir; fakat kaynak kotaları, uygulama havuzu uyku/recycle politikası ve relay trafiği performansı etkiler. API tek worker çalışmalıdır.

### Veritabanı ve ilk kullanıcı: doğru sıra

1. Hosting panelinde Remvora için **boş veritabanı ve ayrı DB kullanıcısı** oluşturun. PostgreSQL/MySQL/MariaDB seçin.
2. API'yi yayınlayın; gerçek bağlantı bilgilerini, provider/sürümü, panel origin'ini ve şifreli kalıcı key ring'i yapılandırın.
3. API yayın dizininde Production ayarlarıyla `Remvora.Api.exe --migrate` çalıştırın: **tablolar oluşur**.
4. `REMVORA_ADMIN_EMAIL`, `REMVORA_ADMIN_PASSWORD` (8–256 karakter), `REMVORA_ORGANIZATION` değerlerini özel olarak verip ayrı çağrıda `Remvora.Api.exe --bootstrap` çalıştırın: **ilk organizasyon ve Owner oluşur**.
5. Panelde belirlediğiniz e-posta/parolayla giriş yapın. Sonraki hesapları **Kullanıcılar** bölümünden oluşturun; bootstrap'ı tekrar çalıştırmayın.
6. Cihaz agentini kaydedin, panelde onaylayın, cihazda etkinleştirin ve istediğiniz özelliklere açık izin verin.

**Migration kullanıcı açmaz. Varsayılan admin hesabı yoktur.** Konsol erişiminiz yoksa hosting desteği bu komutları uygulamanız için çalıştırmalıdır. SQL örnekleri, korunan parola girdisi, Plesk adımları ve sorun giderme: **[İlk kurulum rehberi](docs/FIRST_INSTALL.tr.md)**.

### Sınırlar ve sorumluluklar

Bu bir ön sürümdür; [doğrulama durumunu](docs/STATUS.md) okuyun. Agent platform desteği aynı değildir; tüm cihazlarda tüm özellikler doğrulanmış sayılmaz. NAT/CGNAT ve güvenlik duvarı P2P'yi engelleyebilir; STUN her ağda doğrudan bağlantı garantisi vermez. WSS relay TLS ile korunur, fakat sunucunun içeriği göremediği uçtan uca şifreleme değildir. Yerleşik TURN sunucusu dağıtımı yoktur. Canlı oturum durumu tek API worker varsayar; çok worker/HA kurulumu desteklenmiş sayılmaz. Yalnız yönetmeye yetkili olduğunuz cihazlarda kullanın. Hosting ve trafik maliyetleri işletmeciye aittir.

## English

### What is Remvora and who is it for?

Remvora is an open-source **browser-based remote desktop and device management** system for IT/support teams, school and kiosk administrators, small businesses, distributed offices and Raspberry Pi users. Operators use a computer, tablet or phone browser; managed devices run an agent.

It serves an RDP-style use case but **does not implement Microsoft's RDP protocol** and is not an `mstsc` endpoint. Its transports are WebRTC and WebSocket.

### Capabilities and everyday conveniences

These capabilities combine Server + Web + Agent, subject to operating-system support and explicit local permissions.

| Capability | What it provides |
|---|---|
| Remote desktop | Compressed video, mouse/keyboard control, shortcuts and supported monitor selection |
| Real terminal | Shell/PTY under the agent's OS identity, directory prompt, mobile helper keys and adjustable font size |
| Two-way files | Upload/download in an allowed shared directory, SHA-256 verification and supported browser drop/file-paste transfer |
| Text clipboard | Explicit, permission-controlled read/paste actions |
| System audio | Optional supported-platform audio, remembered browser mute/volume preferences |
| Adaptive video | Automatic FPS/bitrate/resolution or manual controls; per-device preferences retained in the browser |
| Mobile controls | Direct touch by default, optional touchpad, pinch zoom, collapsible tools and browser-supported fullscreen |
| P2P and relay | WebRTC first, authenticated WSS through the API when direct connection fails; visible transport/progress |
| Device lifecycle | Enrollment token, approval/activation, actual online presence, revocation and twice-confirmed permanent deletion |
| Accounts and access | Organization scope, Owner/Admin/Operator/Viewer/custom roles, device groups and profile/password management |
| Administration | TOTP/recovery codes, audit history, API keys, device tags and location/contact information |
| Your infrastructure | PostgreSQL, MySQL or MariaDB; MIT license; no mandatory Remvora vendor-cloud account |

Language, terminal font and audio preferences persist in the same browser, not across browsers. Browser policies may restrict shortcuts, clipboard, automatic audio and fullscreen.

### Shared hosting deployment

The API requires **.NET 10 / ASP.NET Core, HTTPS, long-lived WebSockets, a supported database, persistent private key storage and a way to run setup commands**. Compatible Windows IIS/Plesk shared hosting can work; a VPS is not mandatory. Not every plan meets these requirements, and PHP-only hosting is insufficient for the API. The panel is static; Node.js is a build-time dependency only.

Deploy the three parts separately: **Server on API hosting, Web on panel hosting, Agent on each managed device**. Hosting quotas, app-pool sleep/recycling and relay bandwidth affect performance. Run a single API worker.

### Database and first account, in order

1. Create an **empty database and dedicated database user** in your hosting panel; choose PostgreSQL, MySQL or MariaDB.
2. Publish the API and configure its connection, provider/version, panel origin and encrypted persistent key ring.
3. In the deployed API directory with Production settings, run `Remvora.Api.exe --migrate` to **create tables**.
4. Privately set `REMVORA_ADMIN_EMAIL`, `REMVORA_ADMIN_PASSWORD` (8–256 characters) and `REMVORA_ORGANIZATION`; separately run `Remvora.Api.exe --bootstrap` to **create the organization and first Owner**.
5. Sign into the panel with that email/password. Create later accounts through **Users**, not bootstrap.
6. Enroll each agent, approve it in the panel, activate it on the device and explicitly enable the capabilities you need.

**Migration does not create users; no default administrator account exists.** Without console access, hosting support must execute the commands for your application. Find SQL examples, private password input, Plesk steps and troubleshooting in the **[first-installation guide](docs/FIRST_INSTALL.en.md)**.

### Operational limits

This is pre-release software: read [validation status](docs/STATUS.md). Agent capability/runtime coverage varies by platform. NAT/CGNAT or firewalls may prevent P2P; STUN cannot guarantee it. WSS relay uses TLS but is not server-blind end-to-end encryption. No built-in TURN server deployment is included. Live sessions assume a single API worker; multi-worker/high-availability operation is not a supported claim. Use only on devices you are authorized to manage. Hosting and bandwidth costs remain yours.

## Development / Geliştirme

.NET 10 SDK. Run `dotnet restore --locked-mode`, `dotnet build -c Release` and `dotnet test tests/Remvora.Domain.Tests`. Integration tests require a dedicated isolated test database; never target production. / Entegrasyon testleri için ayrı test veritabanı kullanın.

[Release checklist / Yayın kontrolü](docs/PUBLIC_RELEASE.md) · [Security](SECURITY.md) · [Contributing](CONTRIBUTING.md) · [Third-party notices](THIRD_PARTY_NOTICES.md) · [MIT license](LICENSE)
