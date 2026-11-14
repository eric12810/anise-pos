"""Remove generated IIS configuration from a reviewed publish package."""
from hashlib import sha256
from pathlib import Path
import sys


def prepare(root):
    root = Path(root)
    config = root / "web.config"
    if not config.is_file():
        raise ValueError("Expected generated IIS web.config missing")
    config.unlink()
    rows = []
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.name == "release-manifest.sha256":
            continue
        rows.append(sha256(path.read_bytes()).hexdigest() + "  " + path.relative_to(root).as_posix())
    (root / "release-manifest.sha256").write_text("\n".join(rows) + "\n")


if __name__ == "__main__":
    prepare(sys.argv[1])
