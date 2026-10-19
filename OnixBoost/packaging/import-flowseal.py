#!/usr/bin/env python3
"""Import Flowseal/zapret-discord-youtube general*.bat → dpi/args/flowseal_*.txt

Matches Flowseal Game Filter = disabled (sentinel port 12), full strategy including
ipset-all / QUIC / hostfakesplit — Soft Discord runs this vanilla via winws.
"""
from __future__ import annotations

import json
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ARGS = ROOT / "dpi" / "args"
LISTS = ROOT / "dpi" / "lists"
BIN = ROOT / "dpi" / "bin"

# Flowseal Game Filter disabled → TCP/UDP still set to sentinel port 12
GAME_TCP = "12"
GAME_UDP = "12"

BAT_MAP = [
    ("general.bat", "flowseal_general", "GENERAL"),
    ("general (ALT).bat", "flowseal_alt", "ALT"),
    ("general (ALT2).bat", "flowseal_alt2", "ALT2"),
    ("general (ALT3).bat", "flowseal_alt3", "ALT3"),
    ("general (ALT4).bat", "flowseal_alt4", "ALT4"),
    ("general (ALT5).bat", "flowseal_alt5", "ALT5"),
    ("general (ALT6).bat", "flowseal_alt6", "ALT6"),
    ("general (ALT7).bat", "flowseal_alt7", "ALT7"),
    ("general (ALT8).bat", "flowseal_alt8", "ALT8"),
    ("general (ALT9).bat", "flowseal_alt9", "ALT9"),
    ("general (ALT10).bat", "flowseal_alt10", "ALT10"),
    ("general (ALT11).bat", "flowseal_alt11", "ALT11"),
    ("general (ALT12).bat", "flowseal_alt12", "ALT12"),
    ("general (ALT13).bat", "flowseal_alt13", "ALT13"),
    ("general (EXP).bat", "flowseal_exp", "EXP"),
    ("general (SIMPLE FAKE).bat", "flowseal_simple_fake", "SIMPLE FAKE"),
    ("general (SIMPLE FAKE ALT).bat", "flowseal_simple_fake_alt", "SIMPLE FAKE ALT"),
    ("general (SIMPLE FAKE ALT2).bat", "flowseal_simple_fake_alt2", "SIMPLE FAKE ALT2"),
    ("general (FAKE TLS AUTO).bat", "flowseal_fake_tls_auto", "FAKE TLS AUTO"),
    ("general (FAKE TLS AUTO ALT).bat", "flowseal_fake_tls_auto_alt", "FAKE TLS AUTO ALT"),
    ("general (FAKE TLS AUTO ALT2).bat", "flowseal_fake_tls_auto_alt2", "FAKE TLS AUTO ALT2"),
    ("general (FAKE TLS AUTO ALT3).bat", "flowseal_fake_tls_auto_alt3", "FAKE TLS AUTO ALT3"),
]

# Discord Soft: try SIMPLE FAKE family first (Flowseal community / Run Tests), then ALTs
ORDER = [
    "flowseal_simple_fake",
    "flowseal_simple_fake_alt",
    "flowseal_simple_fake_alt2",
    "flowseal_alt13",
    "flowseal_alt9",
    "flowseal_alt11",
    "flowseal_alt3",
    "flowseal_alt10",
    "flowseal_alt12",
    "flowseal_alt",
    "flowseal_fake_tls_auto_alt3",
    "flowseal_fake_tls_auto",
    "flowseal_general",
    "flowseal_alt6",
    "flowseal_alt5",
    "flowseal_fake_tls_auto_alt",
    "flowseal_alt7",
    "flowseal_alt4",
    "flowseal_fake_tls_auto_alt2",
    "flowseal_alt2",
    "flowseal_alt8",
    "flowseal_exp",
]


def extract_winws_cmdline(bat_text: str) -> str:
    # Join continuation lines ending with ^
    lines = bat_text.splitlines()
    chunks: list[str] = []
    buf = ""
    capturing = False
    for line in lines:
        raw = line.rstrip("\n")
        if "winws.exe" in raw.lower():
            capturing = True
            # drop start "..." /min "path\winws.exe"  (closing quote may follow)
            idx = raw.lower().find("winws.exe")
            rest = raw[idx + len("winws.exe") :]
            rest = rest.lstrip('"').lstrip()
            buf = rest
            if raw.rstrip().endswith("^"):
                buf = buf.rstrip()[:-1]
                continue
            chunks.append(buf)
            capturing = False
            buf = ""
            continue
        if capturing:
            if raw.rstrip().endswith("^"):
                buf += " " + raw.rstrip()[:-1].strip()
            else:
                buf += " " + raw.strip()
                chunks.append(buf)
                capturing = False
                buf = ""
    if not chunks:
        raise ValueError("winws.exe cmdline not found")
    return " ".join(chunks[-1].split())


def tokenize(cmdline: str) -> list[str]:
    # Replace Flowseal vars
    s = cmdline
    s = s.replace("%GameFilterTCP%", GAME_TCP)
    s = s.replace("%GameFilterUDP%", GAME_UDP)
    s = s.replace("%GameFilter%", GAME_TCP)
    # Paths → OnixBoost placeholders
    s = re.sub(r'"%LISTS%([^"]+)"', r"%ROOT%lists\\\1", s)
    s = re.sub(r'"%BIN%([^"]+)"', r"%VBIN%\1", s)
    s = re.sub(r"%LISTS%(\S+)", r"%ROOT%lists\\\1", s)
    s = re.sub(r"%BIN%(\S+)", r"%VBIN%\1", s)
    # Clean trailing commas from empty game filter (shouldn't happen with sentinel 12)
    s = re.sub(r",(?=\s|$)", "", s)

    tokens: list[str] = []
    for m in re.finditer(r'"([^"]*)"|(\S+)', s):
        tok = m.group(1) if m.group(1) is not None else m.group(2)
        if tok in ("^",):
            continue
        tokens.append(tok)
    return tokens


def tokens_to_args_file(tokens: list[str]) -> str:
    lines: list[str] = []
    for t in tokens:
        if t == "--new":
            lines.append("--new")
        else:
            lines.append(t)
    return "\n".join(lines) + "\n"


def convert_bat(src: Path) -> str:
    text = src.read_text(encoding="utf-8", errors="replace")
    cmdline = extract_winws_cmdline(text)
    tokens = tokenize(cmdline)
    return tokens_to_args_file(tokens)


def sync_lists_bins(fs_root: Path) -> None:
    src_lists = fs_root / "lists"
    src_bin = fs_root / "bin"
    LISTS.mkdir(parents=True, exist_ok=True)
    BIN.mkdir(parents=True, exist_ok=True)
    for f in src_lists.iterdir():
        if not f.is_file():
            continue
        # Keep non-empty *-user.txt
        if f.name.endswith("-user.txt"):
            dst = LISTS / f.name
            if dst.is_file() and dst.stat().st_size > 0:
                continue
        shutil.copy2(f, LISTS / f.name)
    for f in src_bin.iterdir():
        if f.is_file():
            shutil.copy2(f, BIN / f.name)


def write_order(tag: str, strategies: list[tuple[str, str, str]]) -> None:
    by_id = {sid: title for sid, title, _ in strategies}
    ordered_ids = [sid for sid in ORDER if sid in by_id]
    for sid, _, _ in strategies:
        if sid not in ordered_ids:
            ordered_ids.append(sid)

    doc = {
        "version": tag,
        "profile": "vanilla-gamefilter-disabled",
        "order": ordered_ids,
        "strategies": [
            {
                "id": sid,
                "title": f"Flowseal {by_id[sid]}",
                "name": by_id[sid],
                "file": f"{sid}.txt",
            }
            for sid in ordered_ids
        ],
    }
    ARGS.mkdir(parents=True, exist_ok=True)
    (ARGS / "flowseal_auto_order.json").write_text(
        json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def main() -> int:
    import sys

    fs = Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/flowseal-1103/zapret-discord-youtube-1.10.3")
    if not fs.is_dir():
        print("FAIL: Flowseal root missing:", fs)
        return 1

    tag = fs.name.replace("zapret-discord-youtube-", "") or "1.10.3"
    strategies: list[tuple[str, str, str]] = []
    ARGS.mkdir(parents=True, exist_ok=True)

    for bat_name, sid, title in BAT_MAP:
        src = fs / bat_name
        if not src.is_file():
            print("SKIP missing", bat_name)
            continue
        out = convert_bat(src)
        path = ARGS / f"{sid}.txt"
        path.write_text(out, encoding="utf-8")
        # sanity: must have L7 discord
        if "filter-l7=discord" not in out and "filter-l7=discord,stun" not in out:
            # some might use different form
            if "discord,stun" not in out:
                print("WARN: no discord L7 in", sid)
        strategies.append((sid, title, f"{sid}.txt"))
        print("OK", sid, "lines", out.count("\n"))

    write_order(tag, strategies)
    sync_lists_bins(fs)
    print("Synced lists+bin from", fs)
    print("Wrote", ARGS / "flowseal_auto_order.json", "version", tag)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
