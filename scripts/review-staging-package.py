"""Review a local publish directory. Never connects to hosting or starts the API."""
import hashlib
import gzip
import subprocess
from pathlib import Path

root = Path('artifacts/staging')
if not (root / 'AnisePOS.Api.dll').is_file() or not (root / 'web.config').is_file():
    raise SystemExit('Expected API assembly and IIS web.config are missing.')

# The POS page is inline JavaScript, so verify the reviewed publish output itself
# contains the hybrid interaction and that generated sidecars match every text asset.
pos_page = root / 'wwwroot/pos.html'
if not pos_page.is_file():
    raise SystemExit('Hybrid POS page is missing from the package.')
pos_bytes = pos_page.read_bytes()
pos_text = pos_bytes.decode('utf-8')
for marker in ('attachDishPress', 'cartLineKey', 'pointerdown', 'requiresSelection'):
    if marker not in pos_text:
        raise SystemExit(f'Hybrid POS marker is missing from package: {marker}')
if 'onclick=()=>openProduct(cat.products' in pos_text:
    raise SystemExit('Stale POS dish-card click handler remains in package.')
for source in sorted((root / 'wwwroot').rglob('*')):
    if not source.is_file() or source.suffix not in {'.html', '.css', '.js'}:
        continue
    for suffix, command in (
        ('.gz', lambda path: gzip.open(path, 'rb')),
        ('.br', lambda path: subprocess.Popen(
            ['brotli', '--decompress', '--stdout', str(path)], stdout=subprocess.PIPE,
        )),
    ):
        sidecar = source.with_suffix(source.suffix + suffix)
        if not sidecar.is_file():
            raise SystemExit(f'Missing generated sidecar: {sidecar.relative_to(root)}')
        if suffix == '.gz':
            with command(sidecar) as compressed:
                expanded = compressed.read()
        else:
            process = command(sidecar)
            expanded = process.communicate()[0]
            if process.returncode != 0:
                raise SystemExit(f'Invalid Brotli sidecar: {sidecar.relative_to(root)}')
        if expanded != source.read_bytes():
            raise SystemExit(f'Generated sidecar does not match {source.relative_to(root)}: {suffix}')

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
