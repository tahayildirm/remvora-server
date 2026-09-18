# Remvora Server

Repositories / Depolar: [Server](https://github.com/tahayildirm/remvora-server) · [Web](https://github.com/tahayildirm/remvora-web) · [Agent](https://github.com/tahayildirm/remvora-agent)

[English installation & administration](docs/GUIDE.en.md) · [Türkçe kurulum ve yönetim](docs/GUIDE.tr.md)

**EN:** Self-hosted remote-device management control plane: identity, organizations, users, enrollment, permissions, audit, WebRTC signaling and WebSocket relay. Pair with the separate Remvora Web and Remvora Agent repositories. MIT-licensed pre-release; no default credentials or hosted service is included.

**TR:** Kendi sunucunuzda çalışan uzak cihaz yönetimi: kimlik, organizasyon, kullanıcı, kayıt/onay, yetki, işlem geçmişi, WebRTC signaling ve WebSocket relay. Ayrı Remvora Web ve Remvora Agent depolarıyla kullanılır. MIT lisanslı ön sürüm; varsayılan hesap veya hazır barındırma hizmeti içermez.

## Start / Başlangıç

1. Choose PostgreSQL / MySQL / MariaDB and configure HTTPS/WSS, database and encrypted key ring. / Veritabanı, HTTPS/WSS ve şifreli anahtar deposunu yapılandırın.
2. `dotnet restore` → `dotnet build -c Release`.
3. `dotnet run --project src/Remvora.Api --no-launch-profile -- --migrate`.
4. Set `REMVORA_ADMIN_EMAIL`, `REMVORA_ADMIN_PASSWORD` (8–256), `REMVORA_ORGANIZATION` privately, then run the same command with `--bootstrap` once on an empty installation. / Gizli yönetici ayarlarını verip boş kurulumda bir kez bootstrap çalıştırın.
5. Deploy the API and Web; sign in, add subsequent users in Users, enroll agents. / API ve paneli yayınlayın; giriş yapın, Kullanıcılar bölümünden sonraki hesapları açın ve agentleri kaydedin.

Read the full guide before running commands: configuration is required, and migration does not create the first user. / Komutlardan önce tam rehberi okuyun: ayarlar gereklidir; migration ilk kullanıcıyı oluşturmaz.

## Development / Geliştirme

.NET 10 SDK. `dotnet build`, `dotnet test tests/Remvora.Domain.Tests`, and integration tests with a dedicated isolated database. Never target production. / Entegrasyon testleri ayrı test veritabanı ister; üretime yöneltmeyin.

[Status / Durum](docs/STATUS.md) · [Public release / Yayın](docs/PUBLIC_RELEASE.md) · [Security](SECURITY.md) · [Contributing](CONTRIBUTING.md) · [MIT](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.md)
