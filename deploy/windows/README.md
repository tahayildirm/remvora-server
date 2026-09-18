# Windows / IIS / Plesk

[English full guide](../../docs/GUIDE.en.md) · [Türkçe tam rehber](../../docs/GUIDE.tr.md)

## English

Use your own API and panel HTTPS domains. This is an application deployment, not server-wide configuration. Requirements: .NET 10 Hosting Bundle/AspNetCoreModuleV2, matching x64 worker, WebSocket Protocol, one worker process, suitable idle timeout and protected persistent key-ring storage. Do not change unrelated application pools.

Build from this repository:

```sh
dotnet publish src/Remvora.Api -c Release -r win-x64 --self-contained false -o .artifacts/windows-iis/api
python3 scripts/package-windows.py --api-origin https://api.example.com --panel-origin https://rdp.example.com --provider MariaDB
```

Optional repeated `--stun-url` supplies browser STUN services selected by your operator. The package contains placeholders, not secrets; `app_offline.htm` keeps the application in maintenance. Configure the actual database/provider/version, allowed hosts/origin, encrypted RSA PFX and key directory through protected deployment settings. For same-origin use the same URL for both origins. IIS must deny public access to App_Data and configuration, keys and archives.

From the published directory run `Remvora.Api.exe --migrate`; on a **new empty installation only**, inject REMVORA_ADMIN_EMAIL, REMVORA_ADMIN_PASSWORD (8–256) and REMVORA_ORGANIZATION privately and run `Remvora.Api.exe --bootstrap`. Clear these variables. Existing installations run migrations only. Do not reuse credentials from another deployment. Remove maintenance only after successful initialization, then check HTTPS health/readiness, login and an actual WSS agent session.

`initialize.ps1 -ApplicationPath <absolute publish directory>` is an optional first-install helper requiring app_offline.htm, protected appsettings.Production.json and App_Data/bootstrap.json with email/password/organization. It deletes the bootstrap file only on success, restores process environment and leaves maintenance enabled. Do not run it for upgrades. Prefer direct secret-injected CLI if avoiding a temporary password file. Keep deployment ZIPs outside the public web root after use. Restore API configuration/key ring/database together during recovery. No hosting credential is shipped.

## Türkçe

Kendi API ve panel HTTPS alan adlarınızı kullanın; bu sunucu geneli değil uygulama kurulumudur. .NET 10 Hosting Bundle, uygun x64 worker, WebSocket Protocol, tek worker, uygun idle timeout ve korunan kalıcı key-ring gerekir. Diğer site havuzlarını değiştirmeyin.

Yukarıdaki komutlarla win-x64 derleyip kendi origin/provider değerlerinizle paketleyin. İsteğe bağlı tekrarlanabilir `--stun-url` tarayıcı STUN adreslerini verir. Paket sır içermez; app_offline.htm bakımda tutar. Gerçek DB/provider/sürüm, origin/hosts ve RSA PFX/anahtar ayarlarını korunan ortamda verin. Aynı-origin için iki URL aynı olabilir. App_Data, ayarlar, anahtar ve arşivlere HTTP erişimini engelleyin.

Yayın dizininde `Remvora.Api.exe --migrate`; yalnız **yeni boş kurulumda** gizli REMVORA_ADMIN_EMAIL/PASSWORD (8–256)/ORGANIZATION ortamını verip `--bootstrap` çalıştırın. Sonra geçici sırları temizleyin. Mevcut kurulumda sadece migration çalışır. Başka kurulumun parolasını kullanmayın. Başarı sonrası bakımı kaldırıp HTTPS health/readiness, giriş ve gerçek agent WSS oturumunu test edin.

initialize.ps1 isteğe bağlı ilk-kurulum yardımcısıdır; bakım dosyası, korunan appsettings.Production.json ve email/password/organization içeren App_Data/bootstrap.json ister. Başarıda bootstrap dosyasını siler, süreç ortamını geri alır, bakımı açık bırakır. Güncellemede kullanmayın. Geçici parola dosyası istemiyorsanız doğrudan gizli ortamla CLI kullanın. Yayın ZIP’leri iş bittikten sonra genel web kökünde kalmasın. Kurtarmada ayar/key-ring/veritabanını birlikte koruyun. Hosting parolası paketle verilmez.
