"""Keep database-affecting source changes out of routine application releases."""
from hashlib import sha256
from pathlib import Path
import os
import sys

CALLER_RELEASE = "0733b67507f85523b48192fff54e17d869bc5955"
CALLER_APPROVAL = "APPROVE CALLER SCHEMA RELEASE"
STRIPE_SHORTCUT_RELEASE = "0de2e5734149f5a998feaeb2ce938e4f38de9369"
STRIPE_SHORTCUT_APPROVAL = "APPROVE POS SHORTCUT RELEASE 0de2e57"
STRIPE_SHORTCUT_CHANGES = {
    "database/production/create_caller_events.sql",
    "database/production/inspect_caller_events.sql",
    "src/AnisePOS.Api/Domain/Entities.cs",
}
CALLER_CHANGES = {
    "src/AnisePOS.Api/Program.cs",
    "database/012_caller_events.sql",
    "database/013_caller_lifecycle.sql",
}
PUSH_TEST_RATE_LIMIT = '''builder.Services.AddRateLimiter(options => options.AddPolicy("staff-push-test", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous", _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
'''


def requirements(root):
    root = Path(root)
    program = root / "src/AnisePOS.Api/Program.cs"
    if not program.is_file():
        raise ValueError("Release source is missing POS startup code")
    paths = [program]
    for directory in ("src/AnisePOS.Api/Data", "src/AnisePOS.Api/Domain"):
        paths.extend((root / directory).rglob("*.cs"))
    paths.extend((root / "database").rglob("*.sql"))
    return {str(path.relative_to(root)): sha256(
                path.read_bytes().replace(PUSH_TEST_RATE_LIMIT.encode(), b"")
                if path == program else path.read_bytes()).hexdigest()
            for path in paths if path.is_file()}


def check(current, candidate, approval="", release=""):
    before, after = requirements(current), requirements(candidate)
    changed = sorted(name for name in before.keys() | after.keys()
                     if before.get(name) != after.get(name))
    if changed and approval == CALLER_APPROVAL and release == CALLER_RELEASE and set(changed) == CALLER_CHANGES:
        return True
    if changed and approval == STRIPE_SHORTCUT_APPROVAL and release == STRIPE_SHORTCUT_RELEASE and set(changed) == STRIPE_SHORTCUT_CHANGES:
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
