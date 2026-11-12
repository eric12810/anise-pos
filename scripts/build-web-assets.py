"""Minify publish-time web assets and create deterministic compression sidecars."""
from __future__ import annotations

import gzip
import re
import shutil
import subprocess
import sys
from pathlib import Path


TEXT_SUFFIXES = {'.html', '.css', '.js'}
WHITESPACE = re.compile(r'\s+')


def minify_css(text: str) -> str:
    text = re.sub(r'/\*(?!\!).*?\*/', '', text, flags=re.S)
    text = WHITESPACE.sub(' ', text).strip()
    return re.sub(r'\s*([{}:;,>+~])\s*', r'\1', text)


def minify_html(text: str) -> str:
    parts = re.split(r'(<(?:script|style)\b[^>]*>.*?</(?:script|style)>)', text,
                    flags=re.I | re.S)
    for index in range(0, len(parts), 2):
        parts[index] = re.sub(r'<!--(?!\[if).*?-->', '', parts[index], flags=re.S)
        parts[index] = re.sub(r'>\s+<', '><', parts[index])
        parts[index] = parts[index].strip()
    return ''.join(parts)


def _identifier(char: str | None) -> bool:
    return bool(char) and (char.isalnum() or char in '_$')


def minify_js(text: str) -> str:
    """Remove comments and safe whitespace while preserving strings and ASI."""
    output: list[str] = []
    index = 0
    quote: str | None = None
    while index < len(text):
        char = text[index]
        if quote:
            output.append(char)
            if char == '\\' and index + 1 < len(text):
                index += 1
                output.append(text[index])
            elif char == quote:
                quote = None
            index += 1
            continue
        if char in "'\"`":
            quote = char
            output.append(char)
            index += 1
            continue
        if text.startswith('//', index):
            newline = text.find('\n', index + 2)
            index = len(text) if newline < 0 else newline
            continue
        if text.startswith('/*', index):
            end = text.find('*/', index + 2)
            index = len(text) if end < 0 else end + 2
            continue
        if char.isspace():
            end = index + 1
            while end < len(text) and text[end].isspace():
                end += 1
            previous = output[-1][-1:] if output else ''
            following = text[end:end + 1]
            if _identifier(previous) and _identifier(following):
                output.append(' ')
            index = end
            continue
        output.append(char)
        index += 1
    return ''.join(output).strip()


def minify(path: Path) -> bytes:
    text = path.read_text(encoding='utf-8')
    if path.suffix == '.html':
        result = minify_html(text)
    elif path.suffix == '.css':
        result = minify_css(text)
    else:
        result = minify_js(text)
    return (result + '\n').encode('utf-8')


def compress(path: Path, data: bytes) -> None:
    with path.with_suffix(path.suffix + '.gz').open('wb') as stream:
        output = gzip.GzipFile(fileobj=stream, mode='wb', compresslevel=9, mtime=0)
        output.write(data)
        output.close()
    brotli = shutil.which('brotli')
    if not brotli:
        raise SystemExit('brotli CLI is required to generate deployment assets')
    result = subprocess.run(
        [brotli, '-q', '11', '-f', '-o', str(path.with_suffix(path.suffix + '.br')), '-'],
        input=data, check=True, capture_output=True,
    )
    if result.stderr:
        raise SystemExit(result.stderr.decode(errors='replace'))


def main() -> None:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else 'artifacts/staging/wwwroot')
    if not root.is_dir():
        raise SystemExit(f'Publish wwwroot does not exist: {root}')
    for path in sorted(root.rglob('*')):
        if path.is_file() and path.suffix in TEXT_SUFFIXES and not path.name.endswith(('.gz', '.br')):
            data = minify(path)
            path.write_bytes(data)
            compress(path, data)
    print(f'Generated minified and compressed assets in {root}')


if __name__ == '__main__':
    main()
