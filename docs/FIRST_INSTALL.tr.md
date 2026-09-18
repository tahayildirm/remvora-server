# İlk kurulum: paylaşımlı hosting, veritabanı ve ilk yönetici

[Türkçe](FIRST_INSTALL.tr.md) · [English](FIRST_INSTALL.en.md) · [Ana sayfa](../README.md)

Bu rehber **yeni, boş Remvora kurulumu** içindir. Sonunda kendi panel adresinizden Owner hesabıyla giriş yapabilecek, ikinci kullanıcıyı ve ilk cihazı ekleyebileceksiniz. Mevcut kurulumun güncellemesinde `--bootstrap` çalıştırılmaz; yedek aldıktan sonra yalnız `--migrate` kullanılır.

## 1. Kurulum planı ve hosting koşulları

Örnek yerleşim:

| Bileşen | Örnek adres | Nereye kurulur? |
|---|---|---|
| Server / API | `https://api.example.com` | .NET uygulaması çalıştırabilen hosting hesabı |
| Web / panel | `https://rdp.example.com` | Statik HTML/JS/CSS sunan hosting alanı |
| Agent | API adresine bağlanır | Yönetilecek Windows, Linux, macOS veya Raspberry Pi cihazı |
| Veritabanı | Hostingin verdiği DB host/portu | PostgreSQL, MySQL veya MariaDB servisi |

**Uygun paylaşımlı Windows hosting / IIS / Plesk üzerinde kurulabilir; yalnız bu uygulama için VPS almak zorunlu değildir.** Ancak her hosting paketi uygun değildir. Sağlayıcıdan şu koşulları doğrulayın:

- ASP.NET Core / .NET 10 Hosting Bundle ve AspNetCoreModuleV2; yayın mimarisiyle uyumlu uygulama havuzu (bu örnekte x64).
- HTTPS sertifikası ve uzun süreli WebSocket/WSS bağlantıları; proxy/hosting politikası bağlantıları erken kesmemeli.
- PostgreSQL, MySQL veya MariaDB erişimi ve Remvora'ya ait veritabanında migration için tablo/index oluşturma ve değiştirme izni.
- Kalıcı, yazılabilir ve webden indirilemeyen anahtar deposu; korunan RSA PFX dosyası/ayarlar.
- Aynı API için tek worker. Uykuya alma, sık recycle, trafik/CPU kotaları aktif uzak oturumları etkileyebilir.
- İlk kurulum ve güncellemelerde **bir defalık migration/bootstrap komutlarını çalıştırma imkânı**: konsol/SSH/PowerShell, uygun Plesk zamanlanmış görev veya hosting desteğinin sizin uygulamanız için çalıştırması.

**Sadece FTP erişiminiz varsa:** dosyaları yükleyebilirsiniz; ama veritabanı tabloları ve Owner hesabı kendiliğinden oluşmaz. Sağlayıcıdan aşağıdaki komutları uygulama dizininizde çalıştırmasını isteyin. Plesk'te “zamanlanmış görev” görünmesi, program çalıştırma yetkisinin mutlaka verildiği anlamına gelmez. Webden çağrılan bir kurulum/parola sıfırlama adresi yoktur. PHP-only hosting API için yeterli değildir. Linux paylaşımlı hosting de .NET süreç barındırma ve WSS koşullarını sağlamalıdır.

API ve panel için aynı ana alan adındaki alt alan adlarını tercih edin. Örnekleri kendi alan adlarınızla değiştirin; `example.com` gerçek kurulum hedefi değildir.

## 2. Boş veritabanını ve DB kullanıcısını oluşturun

### Plesk / hosting paneliyle — paylaşımlı hosting için önerilen yol

1. API alan adını seçin → **Veritabanları / Databases** → **Veritabanı ekle / Add Database** (menü adı sağlayıcıya göre değişebilir).
2. Paketin desteklediği türü seçin: PostgreSQL, MySQL veya MariaDB. MariaDB MySQL menüsü altında gösterilebilir; gerçek motoru kontrol edin.
3. Örneğin `remvora` adlı **yeni ve boş** bir veritabanı oluşturun. Hosting adın önüne hesap öneki ekleyebilir; bağlantıda panelde görünen tam adı kullanın.
4. Ayrı bir DB kullanıcısı oluşturun, güçlü bir parola belirleyin ve yalnız bu veritabanına erişim verin. İlk kurulumda schema migration yetkisi olmalı.
5. **Bağlantı bilgisi** ekranından host, port, tam DB adı, kullanıcı adını alın. `localhost` yalnız API ve DB aynı hosting ortamındaysa doğrudur; kendi bilgisayarınızdaki localhost değildir.
6. MySQL/MariaDB için phpMyAdmin/DB konsolunda `SELECT VERSION();` çalıştırın veya sağlayıcıdan sürümü öğrenin. Örneğin `11.4.5-MariaDB` için ayarda `11.4.5` kullanın; metin ekini yazmayın.

Tabloları elle oluşturmayın ve başka bir uygulamanın veritabanını seçmeyin. Remvora tablolarını 5. adımda migration oluşturacak.

### DB sunucusunu kendiniz yönetiyorsanız — SQL alternatifi

Bunlar yeni kurulum için yönetici konsolunda çalıştırılacak örneklerdir. `REPLACE_WITH_UNIQUE_DB_PASSWORD` alanını özel olarak değiştirin. Gerçek parolayı GitHub'a veya destek mesajına koymayın.

PostgreSQL (`psql`):

```sql
CREATE ROLE remvora_app LOGIN PASSWORD 'REPLACE_WITH_UNIQUE_DB_PASSWORD';
CREATE DATABASE remvora OWNER remvora_app;
```

MySQL veya MariaDB (aynı makinedeki API örneği):

```sql
CREATE DATABASE remvora CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'remvora_app'@'localhost' IDENTIFIED BY 'REPLACE_WITH_UNIQUE_DB_PASSWORD';
GRANT ALL PRIVILEGES ON remvora.* TO 'remvora_app'@'localhost';
```

Uzaktaki API için `localhost` yerine yalnız izin verilen API bağlantı kaynağını kullanın; genel internet erişimi açmayın. Paylaşımlı hosting SQL ile `CREATE DATABASE/USER` izni vermiyorsa panel yolunu kullanın. DB parolası **Remvora giriş parolası değildir**.

## 3. API ve panel dosyalarını hazırlayın

Geliştirme bilgisayarında .NET 10 SDK, Git ve paketleme için Python 3 gerekir. Derleme işlemi hostingde yapılmak zorunda değildir:

```sh
git clone https://github.com/tahayildirm/remvora-server.git
cd remvora-server
dotnet restore --locked-mode
dotnet publish src/Remvora.Api -c Release -r win-x64 --self-contained false -o .artifacts/windows-iis/api
python3 scripts/package-windows.py --api-origin https://api.example.com --panel-origin https://rdp.example.com --provider MariaDB
```

MySQL/PostgreSQL seçtiyseniz `--provider` değerini değiştirin. Oluşan `.artifacts/windows-iis/remvora-api-win-x64.zip` dosyasını **API sitesine** yükleyip çıkartın. Paketteki `app_offline.htm`, hazırlık bitene kadar yalnız bu uygulamayı bakımda tutar. Paket ayarları şablondur; DB parolası/ilk hesap içermez.

Web'i ayrı bir klasörde derleyin:

```sh
git clone https://github.com/tahayildirm/remvora-web.git
cd remvora-web
npm ci
npm run build
python3 scripts/package-windows.py --api-origin https://api.example.com
```

Bu komutlar Node.js 24/npm ve Python 3 bulunan geliştirme bilgisayarında çalışır. `dist/remvora-web-panel.zip` içeriğini **panel sitesine** yükleyin. Panel sunucusunda Node.js çalıştırmak gerekmez. Web klasörüne DB parolası, API ayarı veya anahtar koymayın. IIS ayarları için [Windows yayın rehberine](../deploy/windows/README.md) bakın.

## 4. API üretim ayarlarını girin

API yayın dizinindeki `appsettings.Production.json` dosyasını hostingin korunan yapılandırma aracıyla düzenleyin. IIS'in config/JSON/App_Data dosyalarını dışarı sunmadığını doğrulayın. Hosting ortam değişkenlerini destekliyorsa aynı ayarları o yolla verebilirsiniz; ortam değişkenleri JSON değerlerini geçersiz kılabilir. `.env` dosyası otomatik okunmaz.

MariaDB örneği — aşağıdaki bütün yer tutucuları değiştirin:

```json
{
  "AllowedHosts": "api.example.com",
  "Database": { "Provider": "MariaDB", "ServerVersion": "11.4.5" },
  "ConnectionStrings": {
    "Database": "Server=DB_HOST;Port=3306;Database=DB_NAME;User ID=DB_USER;Password=DB_PASSWORD"
  },
  "Security": {
    "AllowedOrigin": "https://rdp.example.com",
    "KeyRingPath": "App_Data/keys",
    "KeyRingCertificate": "App_Data/keyring.pfx",
    "KeyRingPassword": "PFX_PASSWORD"
  }
}
```

| DB seçimi | Provider | ConnectionStrings:Database örneği | ServerVersion |
|---|---|---|---|
| PostgreSQL | `PostgreSQL` | `Host=DB_HOST;Port=5432;Database=DB_NAME;Username=DB_USER;Password=DB_PASSWORD` | Gerekmez; kaldırın |
| MySQL | `MySQL` | `Server=DB_HOST;Port=3306;Database=DB_NAME;User ID=DB_USER;Password=DB_PASSWORD` | Gerçek sayısal sürüm, örn. `8.4.0` |
| MariaDB | `MariaDB` | MySQL ile aynı bağlantı biçimi | Gerçek sayısal sürüm, örn. `11.4.5` |

Uzak DB kullanıyorsanız sağlayıcının sertifika/CA talimatıyla PostgreSQL için `SSL Mode=VerifyFull`, MySQL/MariaDB için `SslMode=VerifyFull` yapılandırın. Yerel DB ile uzaktaki DB'nin TLS ayarı aynı olmak zorunda değildir; sertifika doğrulamasını hatayı gizlemek için kapatmayın. Provider değiştirmek mevcut verileri otomatik taşımaz.

`App_Data/keys` kalıcı olmalı ve yalnız API kimliği gerekli okuma/yazma erişimine sahip olmalıdır. `keyring.pfx`, Data Protection anahtarlarını şifrelemek içindir; sitenin HTTPS sertifikasından ayrıdır. Var olan key-ring/PFX dosyalarını güncellemede yeniden üretmeyin.

Yeni kurulum için Windows geliştirme/yönetim bilgisayarında, **tüm kaynak depolarının dışında korunan bir dizinde** PowerShell açın. PFX üretme örneği (parolayı ekranda göstermez):

```powershell
New-Item -ItemType Directory -Force .\private-remvora-keys | Out-Null
$cert = New-SelfSignedCertificate -Subject 'CN=Remvora Data Protection' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
$pfxPassword = Read-Host 'PFX parolası' -AsSecureString
Export-PfxCertificate -Cert $cert -FilePath .\private-remvora-keys\keyring.pfx -Password $pfxPassword | Out-Null
Remove-Variable pfxPassword
```

PFX'i korunan aktarım yoluyla API'nin `App_Data/keyring.pfx` konumuna yerleştirin; aynı parolayı `Security:KeyRingPassword` ayarına girin. Yerel özel klasörü kaynak deposunun dışında korunan bir konumda tutun, Git'e eklemeyin. Linux/OpenSSL alternatifi [genel rehberdedir](GUIDE.en.md#practical-secret-entry-and-key-ring-certificate-example).

## 5. Tabloları migration ile oluşturun

**API'nin yüklendiği makinede**, API yayın dizininde PowerShell açın. Aşağıdaki yol örnektir; Plesk'in gösterdiği kendi fiziksel uygulama yolunu kullanın:

```powershell
Set-Location 'D:\YOUR_API_PUBLISH_DIRECTORY'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
& '.\Remvora.Api.exe' --migrate
if ($LASTEXITCODE -ne 0) { throw 'Migration başarısız; sonraki adıma geçmeyin.' }
```

Migration, seçilen provider'ın tablolarını/indexlerini ve `__EFMigrationsHistory` kaydını oluşturur/günceller. DB kullanıcı/izinlerini hostingden önce hazırlamanız gerekir. Migration **ilk kullanıcıyı oluşturmaz**. `--migrate` ile `--bootstrap` iki ayrı çağrıdır; tek komutta birleştirmeyin.

Konsol erişimi yoksa hosting desteğine uygulama dizinini, `Production` ortamını ve `Remvora.Api.exe --migrate` komutunu iletin. Geçici görev kullanılıyorsa çıkış kodu/log sonucunu kontrol edin; parolalı komutu açık görev açıklamasına yazmayın.

## 6. İlk Owner hesabını oluşturun

Aynı yayın dizininde, aynı Production/DB ayarlarıyla bir defa çalıştırın:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:REMVORA_ADMIN_EMAIL = Read-Host 'İlk yönetici e-posta adresi'
$secret = Read-Host 'İlk yönetici parolası (8-256 karakter)' -AsSecureString
$env:REMVORA_ADMIN_PASSWORD = [System.Net.NetworkCredential]::new('', $secret).Password
$env:REMVORA_ORGANIZATION = Read-Host 'Organizasyon/kurum adı'
try {
    & '.\Remvora.Api.exe' --bootstrap
    if ($LASTEXITCODE -ne 0) { throw 'İlk kullanıcı oluşturulamadı; hata kaydını kontrol edin.' }
} finally {
    Remove-Item Env:\REMVORA_ADMIN_EMAIL, Env:\REMVORA_ADMIN_PASSWORD, Env:\REMVORA_ORGANIZATION -ErrorAction SilentlyContinue
    Remove-Variable secret -ErrorAction SilentlyContinue
}
```

Başarı çıktısı `Organization: <UUID>` biçimindedir. Bu işlem organizasyonu ve **Owner** yetkili hesabı veritabanına kaydeder; parolayı hash'ler. Giriş için belirlediğiniz e-posta/parolayı kullanırsınız; Plesk/DB/cihaz SSH bilgilerini değil. Organizasyon ID'sini giriş ekranına yazmanız gerekmez. Parolayı unutmayın; otomatik e-posta gönderimi veya varsayılan admin hesabı yoktur.

`Bootstrap only supports an empty installation` görürseniz işlem daha önce yapılmıştır. Tablo silmeyin; bootstrap ikinci kullanıcı oluşturma veya parola sıfırlama aracı değildir.

Konsolsuz hosting için sağlayıcının çalıştırabileceği alternatif: [initialize.ps1](../deploy/windows/initialize.ps1). Yalnız yeni kurulumda, `app_offline.htm` dururken, yukarıdaki üretim ayarları ve **HTTP erişimi kapalı** `App_Data/bootstrap.json` hazırlanır:

```json
{"email":"owner@example.com","password":"REPLACE_WITH_UNIQUE_OWNER_PASSWORD","organization":"My organization"}
```

Sağlayıcı `powershell -File initialize.ps1 -ApplicationPath "D:\YOUR_API_PUBLISH_DIRECTORY"` çalıştırır. Betik migration ve bootstrap yapar, yalnız başarıda bootstrap dosyasını siler; bakımı açık bırakır. Başarısızlıkta dosyayı korunan alanda tutun ve inceleme bitince kaldırın. İlk kullanıcı parolasını kaynak deposuna, web paneline veya açık destek kaydına koymayın. CLI/gizli ortam girdisi tercih edilir.

## 7. Yayını açın ve giriş yapın

1. Migration ve bootstrap başarılıysa yalnız bu API'nin `app_offline.htm` dosyasını kaldırın. IIS uygulama ortamı `Production` ve ayarları yukarıdakiyle aynı olmalı.
2. `https://api.example.com/health` ve `/health/ready` yanıtlarını kontrol edin. Readiness DB bağlantısını doğrular; tek başına uzak erişim testi değildir.
3. `https://rdp.example.com` açın; 6. adımda oluşturduğunuz e-posta/parolayla giriş yapın.
4. **Profilim / Organizasyon** bölümünde hesabı ve mevcut organizasyonu kontrol edin. **Güvenlik** bölümünden isteğe bağlı TOTP'yi etkinleştirip kurtarma kodlarını koruyun.
5. Yayın arşivlerini ve geçici ilk-kurulum sırlarını herkese açık dizinden kaldırın. DB, key-ring/PFX ve özel ayarları birlikte yedekleyin.

## 8. İkinci ve sonraki kullanıcılar

Owner ile giriş → **Kullanıcılar** → kullanıcı ekle → e-posta, başlangıç parolası ve rol seç → kaydet. Gerekirse yalnız belirli cihaz gruplarına kapsam verin. Başlangıç parolasını özel kanaldan iletin; otomatik davet maili gönderilmez. Kullanıcı giriş yaptıktan sonra profilinden mevcut parolasını vererek yeni parolasını belirler. Owner organizasyonu içindeki hesapları/yetkileri yönetir; mevcut parolaları okuyamaz. Ek organizasyon oluşturma sihirbazı bu sürümde yoktur.

## 9. İlk cihaza bağlanın

Panelde cihaz oluşturun → kayıt tokenı alın → cihazda [Agent kurulumu](https://github.com/tahayildirm/remvora-agent/blob/main/docs/GUIDE.tr.md) ile `enroll` → panelde onay → cihazda `activate` → istenen izinlerle agenti başlatın. Agentin sunucu adresi `https://api.example.com/` olmalıdır. Panelden **Masaüstü** veya **Terminal** açın; gerçek çevrimiçi durumunu, P2P/WSS göstergesini ve bir komut/girdi sonucunu doğrulayın.

## En sık ilk-kurulum hataları

| Sorun | Yapılacak işlem |
|---|---|
| `Access denied` / DB bağlantısı yok | Tam DB adı/kullanıcı/host/port, kullanıcı-host yetkisi ve sağlayıcının TLS şartlarını kontrol edin. |
| `relation/table does not exist` | Doğru DB üzerinde `--migrate` başarıyla bitmiş mi kontrol edin. |
| Kullanıcı bulunmuyor / giriş reddediliyor | Migration hesap açmaz; doğru DB'de bootstrap sonucunu kontrol edin. |
| Production key-ring hatası | PFX yolu/parolası, dosya erişimi ve kalıcı keys dizinini doğrulayın. |
| Panel “sunucuya erişilemiyor” | API çalışıyor mu, HTTPS, panel metadata API origin'i ve `Security:AllowedOrigin` tam eşleşiyor mu kontrol edin. |
| Cihaz çevrimdışı | Agent API URL'si, kimlik/onay, servis ve WSS bağlantısını kontrol edin. |
| Aynı ağda hızlı, farklı ağda yavaş | P2P/WSS göstergesine bakın; relay hosting trafiği ve her iki ağın kapasitesini kullanır. |

[Genel yönetim rehberi](GUIDE.tr.md) · [Güncel sınırlar](STATUS.md) · [Güvenlik](../SECURITY.md)
