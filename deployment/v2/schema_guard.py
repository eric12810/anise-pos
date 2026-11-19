"""Keep database-affecting source changes out of routine application releases."""
from hashlib import sha256
from pathlib import Path
import sys


def requirements(root):
    root = Path(root)
    program = root / "src/AnisePOS.Api/Program.cs"
    if not program.is_file():
        raise ValueError("Release source is missing POS startup code")
    paths = [program]
    for directory in ("src/AnisePOS.Api/Data", "src/AnisePOS.Api/Domain"):
        paths.extend((root / directory).rglob("*.cs"))
    paths.extend((root / "database").rglob("*.sql"))
    return {str(path.relative_to(root)): sha256(path.read_bytes()).hexdigest()
            for path in paths if path.is_file()}


def check(current, candidate):
    before, after = requirements(current), requirements(candidate)
    changed = sorted(name for name in before.keys() | after.keys()
                     if before.get(name) != after.get(name))
    if changed:
        raise ValueError("Database requirements changed; use a separately approved schema release: "
                         + ", ".join(changed))
    return True


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: schema_guard CURRENT_SOURCE CANDIDATE_SOURCE")
    check(sys.argv[1], sys.argv[2])
    print("Routine release database requirements unchanged")
