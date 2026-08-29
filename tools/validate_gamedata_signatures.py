#!/usr/bin/env python3
"""Validate that every Windows GameData byte pattern has one match in server.dll."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path


def load_jsonc(path: Path) -> dict[str, object]:
    raw = path.read_text(encoding="utf-8")
    without_full_line_comments = re.sub(r"^\s*//.*$", "", raw, flags=re.MULTILINE)
    return json.loads(without_full_line_comments)


def compile_pattern(pattern: str) -> re.Pattern[bytes]:
    parts: list[bytes] = []
    for token in pattern.split():
        if token in {"?", "??"}:
            parts.append(b".")
            continue
        if not re.fullmatch(r"[0-9A-Fa-f]{2}", token):
            raise ValueError(f"invalid signature token: {token!r}")
        parts.append(re.escape(bytes([int(token, 16)])))
    return re.compile(b"".join(parts), flags=re.DOTALL)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("server_dll", type=Path)
    parser.add_argument("signatures_jsonc", type=Path)
    args = parser.parse_args()

    server_bytes = args.server_dll.read_bytes()
    document = load_jsonc(args.signatures_jsonc)
    results: list[dict[str, object]] = []
    all_unique = True

    for name, entry in document.items():
        if not isinstance(entry, dict):
            continue
        windows_pattern = entry.get("windows")
        if not isinstance(windows_pattern, str) or not windows_pattern.strip():
            continue
        matches = [match.start() for match in compile_pattern(windows_pattern).finditer(server_bytes)]
        results.append(
            {
                "name": name,
                "match_count": len(matches),
                "file_offsets": [f"0x{offset:X}" for offset in matches[:8]],
            }
        )
        all_unique = all_unique and len(matches) == 1

    report = {
        "server_dll": str(args.server_dll.resolve()),
        "sha256": hashlib.sha256(server_bytes).hexdigest().upper(),
        "signature_count": len(results),
        "all_unique": all_unique,
        "results": results,
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if results and all_unique else 1


if __name__ == "__main__":
    raise SystemExit(main())
