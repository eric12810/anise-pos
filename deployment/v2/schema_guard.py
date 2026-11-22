"""Keep database-affecting source changes out of routine application releases."""
from hashlib import sha256
from pathlib import Path
import os
import sys

CALLER_RELEASE = "0733b67507f85523b48192fff54e17d869bc5955"
CALLER_APPROVAL = "APPROVE CALLER SCHEMA RELEASE"
CALLER_CHANGES = {
    "src/AnisePOS.Api/Program.cs",
    "database/012_caller_events.sql",
    "database/013_caller_lifecycle.sql",
}


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


def check(current, candidate, approval="", release=""):
    before, after = requirements(current), requirements(candidate)
    changed = sorted(name for name in before.keys() | after.keys()
                     if before.get(name) != after.get(name))
    if changed and approval == CALLER_APPROVAL and release == CALLER_RELEASE and set(changed) == CALLER_CHANGES:
        return True
    if changed:
        raise ValueError("Database requirements changed; use a separately approved schema release: "
                         + ", ".join(changed))
    return True


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("Usage: schema_guard CURRENT_SOURCE CANDIDATE_SOURCE")
    check(sys.argv[1], sys.argv[2], os.environ.get("CALLER_SCHEMA_APPROVAL", ""),
          os.environ.get("RELEASE_SHA", ""))
    print("Production release database requirements approved")
