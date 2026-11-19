"""Winserve explicit-TLS FTPS transport; curl credentials travel on stdin only."""
from pathlib import PurePosixPath
import subprocess
import tempfile
import time
from urllib.parse import quote


class CurlFTPS:
    def __init__(self, target, password):
        target.validate()
        if not password or any(c in password for c in "\r\n"):
            raise ValueError("Missing or invalid FTPS credential")
        self.target, self.password = target, password
        self.path = target.root
        self.stats = {kind: [0, 0.0] for kind in ("list", "download", "upload", "command")}

    def _path(self, name=""):
        path = PurePosixPath(name) if name.startswith("/") else PurePosixPath(self.path) / name
        if ".." in path.parts or any(c in str(path) for c in "\r\n\\"):
            raise ValueError("Unsafe FTPS path")
        value = str(path)
        if value != self.target.root and not value.startswith(self.target.root + "/"):
            raise ValueError("FTPS operation outside approved root")
        return value

    def _run(self, path, *, listing=False, upload=None, commands=()):
        credential = (self.target.user + ":" + self.password).replace("\\", "\\\\").replace('"', '\\"')
        # Winserve's staging account needs a server-absolute path; liveposftp is chrooted.
        prefix = "//" if self.target.name == "staging" else "/"
        url = "ftp://" + self.target.host + ":21" + prefix + quote(path.lstrip("/"), safe="/")
        if listing and not url.endswith("/"):
            url += "/"
        args = ["curl", "--silent", "--show-error", "--fail", "--ssl-reqd", "--proto", "=ftp",
                "--ftp-method", "multicwd", "--connect-timeout", "30", "--max-time", "180", "--config", "-"]
        for command in commands:
            args += ["--quote", command]
        if listing:
            args.append("--list-only")
        if upload is not None:
            args += ["--upload-file", upload]
        kind = "upload" if upload is not None else "command" if commands else "list" if listing else "download"
        started = time.monotonic()
        try:
            result = subprocess.run(args + [url], input=('user = "' + credential + '"\n').encode(), capture_output=True)
        finally:
            self.stats[kind][0] += 1
            self.stats[kind][1] += time.monotonic() - started
        if result.returncode:
            # curl/server output can contain hosting details or credentials.
            raise RuntimeError("FTPS operation failed (curl exit " + str(result.returncode) + ")")
        return result.stdout

    def timing_summary(self):
        return "FTPS: " + ", ".join(f"{kind}={count} calls/{seconds:.1f}s"
                                     for kind, (count, seconds) in self.stats.items())

    def cwd(self, path):
        self.path = self._path(path)

    def list(self):
        names = [PurePosixPath(line).name for line in self._run(self.path, listing=True).decode().splitlines() if line]
        if any(not name.isprintable() or name in {".", ".."} or "/" in name or "\\" in name for name in names):
            raise RuntimeError("Unsafe FTPS listing")
        return names

    def get(self, name):
        return self._run(self._path(name))

    def put(self, name, data):
        with tempfile.NamedTemporaryFile() as payload:
            payload.write(data)
            payload.flush()
            self._run(self._path(name), upload=payload.name)

    def mkdir(self, name):
        # Unlike RNFR/RNTO, MKD uses the absolute server path on this host.
        self._run(self.target.root, listing=True, commands=("MKD " + self._path(name),))

    def rename(self, source, destination):
        a, b = PurePosixPath(self._path(source)), PurePosixPath(self._path(destination))
        if a.parent != b.parent:
            raise ValueError("FTPS rename crosses directories")
        # curl changes into the URL directory before +quote; Winserve requires bare names.
        self._run(str(a.parent), listing=True, commands=("+RNFR " + a.name, "+RNTO " + b.name))

    def delete(self, name):
        path = PurePosixPath(self._path(name))
        self._run(str(path.parent), listing=True, commands=("+DELE " + path.name,))
