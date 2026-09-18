"""Package the framework-dependent Windows publish output without secrets."""
from pathlib import Path
import json
import zipfile
import argparse
from urllib.parse import urlsplit
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--api-origin', required=True)
p.add_argument('--panel-origin', required=True)
p.add_argument('--provider', choices=['PostgreSQL','MySQL','MariaDB'], required=True)
p.add_argument('--stun-url', action='append', default=[])
a = p.parse_args()
for origin in (a.api_origin,a.panel_origin):
    u=urlsplit(origin)
    if u.scheme!='https' or not u.hostname or u.username or u.password or u.path not in ('','/') or u.query or u.fragment:
        p.error('Use HTTPS origins without credentials, path, query or fragment')
root = Path(__file__).resolve().parents[1]
assert (root / 'src/Remvora.Api/Remvora.Api.csproj').is_file()
publish = root / '.artifacts/windows-iis/api'
assert (publish / 'Remvora.Api.exe').is_file(), 'Publish win-x64 first'
(publish / 'appsettings.Development.json').unlink(missing_ok=True)
(publish / 'appsettings.Production.json').write_text(json.dumps({
    'AllowedHosts': urlsplit(a.api_origin).hostname, 'Database': {'Provider': a.provider},
    'WebRtc': {'StunServers': a.stun_url},
    'Security': {'AllowedOrigin': a.panel_origin.rstrip('/'), 'KeyRingPath': 'App_Data/keys'}
}, indent=2))
(publish / 'app_offline.htm').write_text('<!doctype html><html lang="tr"><meta charset="utf-8"><title>Remvora</title><h1>Remvora kurulumu hazırlanıyor</h1><p>Yapılandırma ve doğrulama tamamlandığında hizmet açılacak.</p></html>')
for path in publish.rglob('*'):
    if path.name in ('App_Data', '.runtime') or path.suffix in ('.pfx', '.key', '.pem'):
        raise RuntimeError('Private material found in publish directory')
with zipfile.ZipFile(root / '.artifacts/windows-iis/remvora-api-win-x64.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(publish.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(publish))
