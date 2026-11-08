"""Review a local publish directory. Never connects to hosting or starts the API."""
import hashlib
from pathlib import Path

root = Path('artifacts/staging')
if not (root / 'AnisePOS.Api.dll').is_file() or not (root / 'web.config').is_file():
    raise SystemExit('Expected API assembly and IIS web.config are missing.')

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
print(f'OFFLINE DRY RUN: {len(rows)} files. No FTPS connection or database access.')
print(manifest.read_text())
