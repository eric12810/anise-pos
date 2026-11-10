"""Review a local publish directory. Never connects to hosting or starts the API."""
import hashlib
import gzip
from pathlib import Path

root = Path('artifacts/staging')
if not (root / 'AnisePOS.Api.dll').is_file() or not (root / 'web.config').is_file():
    raise SystemExit('Expected API assembly and IIS web.config are missing.')

# The POS page is inline JavaScript, so verify the reviewed publish output itself
# contains the hybrid interaction and that its checked-in gzip sidecar is current.
pos_page = root / 'wwwroot/pos.html'
pos_gzip = root / 'wwwroot/pos.html.gz'
if not pos_page.is_file() or not pos_gzip.is_file():
    raise SystemExit('Hybrid POS page or gzip sidecar is missing from the package.')
pos_bytes = pos_page.read_bytes()
pos_text = pos_bytes.decode('utf-8')
for marker in ('attachDishPress', 'cartLineKey', 'pointerdown', 'requiresSelection'):
    if marker not in pos_text:
        raise SystemExit(f'Hybrid POS marker is missing from package: {marker}')
if 'onclick=()=>openProduct(cat.products' in pos_text:
    raise SystemExit('Stale POS dish-card click handler remains in package.')
with gzip.open(pos_gzip, 'rb') as compressed:
    if compressed.read() != pos_bytes:
        raise SystemExit('POS gzip sidecar does not match pos.html.')

# Configuration stays on the server; never include local settings in a package.
for path in root.rglob('appsettings*.json'):
    path.unlink()

blocked = {'.git', 'data', 'app_data', 'database', 'tests', 'obj', 'node_modules'}
rows = []
for path in sorted(root.rglob('*')):
    if path.is_symlink():
        raise SystemExit('Package contains a symbolic link.')
    if not path.is_file():
        continue
    relative = path.relative_to(root)
    if (any(part.lower() in blocked for part in relative.parts)
            or path.name.lower().startswith(('.env', 'secrets', 'credentials'))
            or path.suffix.lower() in {'.pfx', '.pem', '.key', '.bak', '.sql', '.cs', '.csproj', '.pubxml'}):
        raise SystemExit(f'Unexpected package file: {relative}')
    rows.append(f'{hashlib.sha256(path.read_bytes()).hexdigest()}  {relative.as_posix()}')

manifest = Path('artifacts/staging-manifest.sha256')
manifest.write_text('\n'.join(rows) + '\n')
# Keep the approved checksum manifest inside the uploaded, immutable artifact.
(root / 'release-manifest.sha256').write_text(manifest.read_text())
print(f'OFFLINE DRY RUN: {len(rows)} files. No FTPS connection or database access.')
print(manifest.read_text())
