# Remvora Server — kurulum ve yönetim

**Yeni kurulum: [Veritabanı ve ilk Owner dahil adım adım başlangıç](FIRST_INSTALL.tr.md).**
[Türkçe](GUIDE.tr.md) · [English](GUIDE.en.md) · [Yayın kontrolü](PUBLIC_RELEASE.md)

## Nedir, kimler kullanır?

Remvora; BT ekipleri, okullar, kiosk filoları, küçük işletmeler, teknik destek ekipleri ve ev laboratuvarları için kendi sunucunuzda çalışan uzak cihaz yönetimidir. Operatör tarayıcıdan çalışır; .NET sunucu kimlik/yetki ve bağlantıları yönetir; her cihazda Rust agent çalışır. Yetkili sorun giderme, terminal komutları, masaüstü desteği, cihaz envanteri ve sınırlı dosya alışverişi için kullanılır. Gizli izleme aracı, genel RDP geçidi, kapsamlı MDM veya her işletim sistemi özelliğinin karşılığı değildir. Ayrı mobil uygulama yoktur; telefon/tablet web panelini kullanır.

Üç ayrı depo birlikte kurulur: **Remvora Server** (bu depo), **Remvora Web** (Angular 21, derleme için Node.js 24), **Remvora Agent** (Rust). Özel cihazlarınızı başkasının demo sunucusuna bağlamayın. Açık kaynak depoları: [Server](https://github.com/tahayildirm/remvora-server) · [Web](https://github.com/tahayildirm/remvora-web) · [Agent](https://github.com/tahayildirm/remvora-agent).

## Mimari ve kapsam

Tarayıcı ve agent API’ye kimliği doğrulanmış HTTPS/WSS bağlantısı kurar. Otomatik mod önce WebRTC P2P dener; doğrudan ICE bağlantısı kurulamazsa mevcut WSS sunucusu terminal/görüntü/girdi/ses/dosya verisini aktarır. Panel bağlantı türünü ve ilerlemeyi gösterir. STUN adres adaylarını keşfeder, veri aktaran relay değildir. CGNAT tek başına P2P’nin imkânsızlığını kanıtlamaz; NAT filtreleme, hotspot, UDP politikası ve aday iletimi de önemlidir. Gerçek iki ağı test edin. Yerleşik TURN kurulum aracı yoktur.

P2P verisi WebRTC ile şifrelenir. WSS relay iki ayrı TLS bağlantısıdır; **sunucuya karşı uçtan uca gizli şifreleme değildir**. Relay sunucu bant genişliği ve işlem kaynağı tüketir. Hosting uzun WebSocket bağlantılarına ve trafiğe izin vermelidir. Video sıkıştırılır; kalite/FPS/bitrate elle seçilebilir ve dinamik uyarlanabilir. Canlı signaling durumu tek API sürecindedir. Dağıtık broker geliştirmeden birden çok worker, replica veya yük dengeleme açmayın.

Kapsam: organizasyona bağlı kullanıcı/cihaz/katalog; grup/etiket/yetkili/lokasyon; roller ve cihaz grubu sınırları; kayıt/onay/anahtarla doğrulama; API anahtarı; TOTP/kurtarma kodu; işlem kaydı; terminal; masaüstü/girdi; izinli metin panosu; ses; sınırlı dosya aktarımı; devre dışı bırakma, iptal, kalıcı silme ve ayrıca izin verilmiş yeniden başlatma. Özellikler agent işletim sistemine ve yerel izinlere bağlıdır. Platform sınırları Agent rehberindedir.

## Gereksinimler

- Derleme için .NET 10 SDK; yayına uygun .NET 10 runtime/ASP.NET Core Hosting Bundle.
- PostgreSQL, MySQL veya MariaDB. Geliştirmede doğrulanan tabanlar PostgreSQL 17, MySQL 8.4, MariaDB 11.4’tür. MySQL/MariaDB gerçek sunucu sürümünü belirtin.
- Migration için yetkili veritabanı hesabı; kalıcı ve yazılabilir Data Protection anahtar dizini; bunu şifrelemek için RSA PFX sertifikası ve parolası.
- Güvenilir HTTPS sertifikası, DNS, WebSocket upgrade desteği. Yalnız PHP/statik hosting API’yi çalıştıramaz.
- Tercihen tek origin: `https://remvora.example`; `/api/`, `/ws/`, `/health` API’ye, kök yol web paneline gider. Aynı site altındaki ayrı HTTPS subdomainler tam CORS origin ayarıyla çalışabilir. Farklı site alan adlarında Strict cookie engeli olabilir; CORS açmak tek başına yeterli değildir.

Backend portunu gereksiz yere internete açmayın. `AllowedHosts` API hostlarını, `Security__AllowedOrigin` panelin son eğik çizgisiz origin’ini içerir. Proxy örneği Web deposundadır. IIS’te tek worker, .NET 10 Hosting Bundle, WebSocket Protocol, uygun idle timeout ve anahtar dizini izinleri gerekir. Yalnız kendi uygulamanızı/site havuzunuzu yapılandırın.

## Veritabanı ve ilk Owner hesabı

**Varsayılan kullanıcı/parola yoktur.** Genel kayıt formu yoktur. Hosting hesabı, veritabanı hesabı, agentin işletim sistemi hesabı ve Remvora kullanıcıları farklıdır.

Aşağıdaki değerleri servis ortam değişkenleri/gizli ayar sağlayıcınızla verin. `.env.example` referanstır; .NET bu dosyayı kendiliğinden yüklemez:

```text
Database__Provider=PostgreSQL
ConnectionStrings__Database=<veritabanı bağlantı dizginiz>
Database__ServerVersion=<MySQL/MariaDB gerçek sürümü; PostgreSQL için gerekmez>
Security__AllowedOrigin=https://remvora.example
AllowedHosts=remvora.example
Security__KeyRingPath=/protected/remvora/keys
Security__KeyRingCertificate=/protected/remvora/keyring.pfx
Security__KeyRingPassword=<PFX parolası>
ASPNETCORE_ENVIRONMENT=Production
```

Provider tam olarak `PostgreSQL`, `MySQL` veya `MariaDB` olmalıdır. Örnek biçimler; yer tutucuları özel olarak değiştirin:

```text
Host=DB_HOST;Database=remvora;Username=DB_USER;Password=DB_PASSWORD;SSL Mode=VerifyFull
Server=DB_HOST;Database=remvora;User ID=DB_USER;Password=DB_PASSWORD;SslMode=VerifyFull
```

Veritabanınızın TLS/güven ayarını kullanın; yerel ve uzaktaki veritabanının TLS düzeni aynı olmayabilir. Gerçek parolayı yayımlamayın. Provider değişikliği mevcut veriyi başka motora dönüştürmez.

Bu depo kökünde:

```sh
dotnet restore
dotnet build -c Release
dotnet run --project src/Remvora.Api --no-launch-profile -- --migrate
```

Migration şemayı kurar; ilk kullanıcıyı oluşturmaz. `REMVORA_ADMIN_EMAIL`, `REMVORA_ADMIN_PASSWORD` (8–256 karakter; uzun ve benzersiz önerilir) ve `REMVORA_ORGANIZATION` değişkenlerini gizli ayar sağlayıcısı veya ekranda göstermeyen parola girişiyle verin:

```sh
dotnet run --project src/Remvora.Api --no-launch-profile -- --bootstrap
```

Bootstrap, tüm kurulumda henüz organizasyon yoksa bir organizasyon ve bir **Owner** oluşturur; mevcut kurulumda çalışmayı reddeder. Yazılan organizasyon ID’sini özel işletim kaydına alın, geçici parola ortam değişkenlerini temizleyin. İkinci kullanıcı için bootstrap çalıştırmayın; giriş sorununu tablo silerek çözmeyin; veritabanı parolasını kullanıcı parolası yapmayın.

Yayın ve çalışma:

```sh
dotnet publish src/Remvora.Api -c Release -o .artifacts/server
# Yayın sunucusunda, yayımlanan dizinden:
dotnet Remvora.Api.dll
```

Kendi servis yöneticinizi/IIS sitenizi yapılandırın; paneli ayrıca kurun. HTTPS ile `/health` ve `/health/ready` kontrol edin. Readiness veritabanı erişimini gösterir, tüm uzak erişim akışını doğrulamaz. Üretimde şifreli key-ring yapılandırması gereklidir; sertifika/anahtar kaynak kodla verilmez. [Windows rehberi](../deploy/windows/README.md).

## İlk giriş ve sonraki kullanıcılar

1. Panelde bootstrap e-posta/parolasıyla giriş yapın. Girişte organizasyon ID’si istenmez. MFA açıksa TOTP/kurtarma kodunu ekleyin.
2. **Güvenlik** bölümünde TOTP açın; bir kez gösterilen kurtarma kodlarını özel olarak saklayın. Kod çalışmazsa saatleri kontrol edin.
3. **Kullanıcılar → Ekle** ile kişinin e-postasını, 8–256 karakter başlangıç parolasını ve verebildiğiniz rolü seçin. Parolayı özel kanaldan iletin. Otomatik davet e-postası/genel self-signup yoktur.
4. Kişi giriş yaptıktan sonra **Profilim / Organizasyon** bölümünde mevcut parolasını vererek kendi parolasını değiştirebilir. Profilde e-posta/parola düzenleme vardır; rastgele ek kişisel alanların bulunduğunu varsaymayın.
5. Owner/Admin/Operator/Viewer ve özel roller işlemleri belirler. Rol ekranındaki gerçek izinleri esas alın. Admin sahip olmadığı yetkiyi veya Owner rolünü veremez. Owner yeni Owner oluşturabilir; son Owner düşürülemez.
6. Cihaz grubu sınırında `null` tüm organizasyon cihazları, `[]` hiçbiri, ID listesi tam o gruplar demektir; alt gruplar otomatik dahil olmaz. Owner sınırlandırılamaz. Sınırlandırma, kapsamı aşabilecek yönetim işlemlerini de kaldırır.
7. Owner kendi organizasyonunun kullanıcı e-postası, rolü, MFA durumu, kilidi, cihaz kapsamı ve aktif oturum sayısını görebilir; e-posta/yeni parola belirleyebilir, kilidi açabilir, oturumları iptal edebilir. **Mevcut parolalar, hash’ler, MFA sırları ve kurtarma kodları Owner’a da gösterilmez.**

Profilde organizasyon seçimi, parola doğrulamasından sonra mevcut uygun hesapları listeler; hedef organizasyonun doğrulaması/MFA’sı ayrıca gerekir. Bu bir organizasyon oluşturma sihirbazı veya organizasyonlar arası genel superadmin değildir. Bootstrap yalnız ilk organizasyonu oluşturur; ek organizasyon açma için bu sürümde genel UI/CLI akışı yoktur.

## İlk cihaz

Panelde cihaz oluşturun → kayıt tokeni alın → cihazda `REMVORA_ENROLLMENT_TOKEN` ile Agent `enroll` çalıştırın → panelden onaylayın → Agent `activate` çalıştırın → seçilen özellik izinleriyle Agenti başlatın. Tam komutlar Agent rehberindedir. Aynı özel state ve işletim sistemi hesabını koruyun. Kayıt onayı çevrimiçi olmak değildir: Online/Busy/Offline/Unknown canlı signaling durumudur; `Active` kayıt etiketi erişim kanıtı değildir.

Devre dışı bırakma geri alınabilir, kimliği korur. İptal/revoke güveni kaldırır, yeniden kayıt gerekir. Kalıcı silme ayrı işlemdir, panel iki onay ister ve geri alınamaz kabul edilmelidir; işlem geçmişi kalabilir. Bağlantıyı düzeltmek için silme kullanmayın.

## İşletim, yedek ve güncelleme

Veritabanını, şifreli key-ring dizinini, RSA PFX’i, ayrı korunan PFX parolasını ve servis ayarlarını tutarlı biçimde yedekleyin. Sunucu yedeği statik web klasörüne konmaz. Yalıtılmış sistemde geri dönüş testi yapın: giriş/MFA, kullanıcı/cihazlar ve yeni uzak oturum. Şifreleme anahtarlarının kaybı MFA verilerini okunamaz yapabilir. Agent özel kimliğini ayrıca koruyun; cihazlar arasında kopyalamayın.

Güncelleme: sürüm notları → yedek → bu API’yi durdur/oturumları boşalt → uyumlu derlemeyi koy → seçilen provider için `--migrate` → başlat → health, giriş, presence ve gerçek terminal/masaüstünü dene. Güncellemede bootstrap çalıştırılmaz. Şema düşürme otomatik güvenli değildir; gerekirse uygulama ve veritabanını uyumlu yedekten birlikte döndürün. API yeniden başlarsa oturumlar kesilir. Web ve agent ayrı güncellenir.

İstek gövdelerini, terminal içeriğini, cookie/token/kurtarma kodlarını loglamayın. Audit yönetim olaylarını kaydeder; eksiksiz ekran/komut kaydı sistemi değildir. API anahtarını en az yetkiyle oluşturun, yalnız oluşturulurken görünen tokeni gizli saklayın ve kullanım bitince iptal edin. Kullanıcı oturumu varsayılan 12 saat, uzak oturum 1 saattir; sınırlar `.env.example`/SecurityPolicy’dedir. OS, .NET, EF provider ve native bağımlılıkları güncel tutun.

## Sorun giderme ve sınırlar

| Sorun | Kontrol |
|---|---|
| Sunucuya erişilemiyor | DNS/TLS, API süreci, `/health/ready`, panel API origin, tam CORS origin, aynı-site cookie |
| Giriş reddediliyor | Doğru Remvora hesabı, bootstrap, parola, tekrar hatalarda 15 dakika kilit, MFA/saat |
| Onaylı ama çevrimdışı | Agent servis/log, kimlik state, HTTPS güveni, WSS upgrade, cihaz etkinliği |
| P2P kurulamıyor | İki ağda gerçek ICE adayları ve UDP/NAT; ISP adına göre tahmin yerine bildirilen WSS fallback |
| Relay yavaş | İki uplink, gecikme/kayıp, hosting bant/CPU; adaptif kaliteyi düşürün; relay ek sunucu geçişidir |
| Siyah ekran/girdi yok | Açık grafik oturumu, destekli backend, yerel ekran/girdi izinleri, `--allow-desktop` |
| Oturum meşgul | Önceki oturumu kapatın; mevcut agent aynı anda tek uzak oturum taşır |
| Migration başarısız | Provider/sürüm, bağlantı, şema yetkisi ve migration geçmişi; üretim tablolarını silmeyin |
| Tüm Owner’lar erişimsiz | Varsayılan arka kapı/reset hesabı yok; denenmiş geri dönüş veya gözden geçirilmiş yönetici kurtarması |

E-postayla parola sıfırlama, kesintisiz ağ geçişi garantisi, dağıtık HA, otomatik update keşfi, native dosya panosu ve güvenli OS tuş dizileri vaat edilmez. İmzasız geliştirme paketleri ve seçilmiş cihaz testleri tam production onayı değildir. [Durum](STATUS.md).

## Geliştirme, lisans, dağıtım

`dotnet build`, domain testleri ve `REMVORA_TEST_DATABASE`/`REMVORA_TEST_PROVIDER` ile ayrı, silinebilir veritabanında entegrasyon testleri çalıştırın. Testte üretim kullanmayın. REST kökü `/api/v1`, Development OpenAPI yolu `/openapi/v1.json`’dır. Yetki kontrolü sunucudadır.

MIT, lisans bildirimi korunarak ticari dahil kullanma/değiştirme/dağıtma izni verir; garanti vermez. Bağımlılıklar kendi lisanslarını korur. Hosting, trafik, destek, sertifika veya üçüncü taraf servis ücreti dahil değildir. Yayın öncesi LICENSE, THIRD_PARTY_NOTICES, SECURITY ve PUBLIC_RELEASE okuyun.

## Gizli giriş ve key-ring sertifikası örneği

Elle Linux kurulumu için aşağıdaki **Bash** soruları parolayı komut geçmişine açık metin olarak yazmamanızı sağlar. Migration/bootstrap aynı süreç ortamını kullanmalı; normal servis için ayrıca kalıcı gizli ayar sağlayıcısı kurun:

```bash
read -r -p 'İlk Owner e-posta: ' REMVORA_ADMIN_EMAIL
read -r -s -p 'İlk Owner parolası: ' REMVORA_ADMIN_PASSWORD
printf '\n'
read -r -p 'Organizasyon adı: ' REMVORA_ORGANIZATION
export REMVORA_ADMIN_EMAIL REMVORA_ADMIN_PASSWORD REMVORA_ORGANIZATION
# Kurulum bölümündeki bootstrap komutunu çalıştırın.
# Başarıdan sonra:
unset REMVORA_ADMIN_EMAIL REMVORA_ADMIN_PASSWORD REMVORA_ORGANIZATION
```

Data Protection için ayrıca korunan RSA PFX kullanın; **bu genel HTTPS sertifikanız değildir**. Sertifika yönetim sisteminiz sağlayabilir. OpenSSL kuruluysa özel servis dizininde üretim örneği:

```sh
umask 077
openssl req -x509 -newkey rsa:3072 -keyout keyring.key -out keyring.crt -days 3650 -subj /CN=Remvora-DataProtection
openssl pkcs12 -export -inkey keyring.key -in keyring.crt -out keyring.pfx
```

OpenSSL özel anahtar/export parolalarını sorar; şifrelemeyi kapatmayın. Security__KeyRingCertificate oluşan PFX’i, Security__KeyRingPassword export parolasını göstermeli. ACL’yi servis/yöneticiyle sınırlayın; özel yedekleyin, web köküne veya yayın ZIP’ine koymayın. Rotasyonda eski veriyi çözmek için gereken anahtar/sertifikaları koruyun. HTTPS için ayrıca tarayıcı/agentin güvendiği sertifika gerekir.
