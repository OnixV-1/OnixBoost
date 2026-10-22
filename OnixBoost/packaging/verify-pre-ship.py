#!/usr/bin/env python3
"""Pre-ship checks — fail release if media TUN catch-all or missing proxy/PAC wiring."""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PUBLISH = ROOT / "publish"
DPI = PUBLISH / "dpi"
SRC_CORE = ROOT / "dpi" / "core"

errors: list[str] = []


def fail(msg: str) -> None:
    errors.append(msg)
    print("FAIL:", msg)


def ok(msg: str) -> None:
    print("OK:", msg)


def need(path: Path) -> None:
    if not path.is_file():
        fail(f"missing file: {path.relative_to(ROOT)}")
    else:
        ok(f"file {path.relative_to(ROOT)}")


def main() -> int:
    print("==> verify-pre-ship")
    for p in (
        SRC_CORE / "client-svc.json",
        SRC_CORE / "client-telegram.json",
        DPI / "core" / "OnixBoost.exe",
        DPI / "core" / "wintun.dll",
        PUBLISH / "OnixBoost.dll",
        ROOT / "Services" / "SystemProxyHelper.cs",
    ):
        need(p)

    svc = json.loads((SRC_CORE / "client-svc.json").read_text(encoding="utf-8"))
    inbounds = svc.get("inbounds") or []
    types = [i.get("type") for i in inbounds]
    if "mixed" not in types:
        fail("client-svc.json must use mixed inbound (no catch-all TUN for YT/TW)")
    else:
        ok("client-svc mixed inbound")
    if any(i.get("type") == "tun" for i in inbounds):
        fail("client-svc.json must NOT contain tun (YT/TW must not touch routing table)")
    else:
        ok("client-svc has no TUN")

    port = next((i.get("listen_port") for i in inbounds if i.get("type") == "mixed"), None)
    if port != 17890:
        fail(f"mixed listen_port={port}, want 17890")
    else:
        ok("mixed port 17890")

    tg = json.loads((SRC_CORE / "client-telegram.json").read_text(encoding="utf-8"))
    tg_in = (tg.get("inbounds") or [{}])[0]
    if tg_in.get("interface_name") != "OnixBoostTG" or not tg_in.get("route_address"):
        fail("telegram must stay OnixBoostTG + route_address")
    else:
        ok("telegram selective TUN intact")

    store = (ROOT / "Services" / "TunnelConfigStore.cs").read_text(encoding="utf-8")
    engine = (ROOT / "Services" / "TunnelEngine.cs").read_text(encoding="utf-8")
    proxy = (ROOT / "Services" / "SystemProxyHelper.cs").read_text(encoding="utf-8")
    mw = (ROOT / "MainWindow.axaml.cs").read_text(encoding="utf-8")

    for must, blob, label in (
        ("AppendMixedInboundToHy2", store, "store"),
        ("ScrubRuntimeLeaf", store, "store"),
        ("EnableMediaPac", engine, "engine"),
        ("DisableMediaPac", engine, "engine"),
        ("TunnelKind", engine, "engine"),
        ("FindProxyForURL", proxy, "pac"),
        ("ProxyEnable", proxy, "pac"),
        ("PacHttpPort", proxy, "pac"),
        ("http://127.0.0.1", proxy, "pac"),
        ("RebuildMediaTunnelAsync", mw, "main"),
        ("RebuildTelegramTunnelAsync", mw, "main"),
        ("_tunnelMedia", mw, "main"),
        ("_tunnelTg", mw, "main"),
    ):
        if must not in blob:
            fail(f"missing {must} in {label}")
        else:
            ok(f"{label}: {must}")
    if "file:///" in proxy and "AutoConfigURL" in proxy and "PacHttpPort" not in proxy:
        fail("PAC must not rely on file:// only")
    else:
        ok("PAC served over http://127.0.0.1")

    # PAC must not force ProxyEnable=1 (that couples Telegram Desktop into mixed proxy)
    enable_fn = proxy.find("public static void EnableMediaPac")
    disable_fn = proxy.find("public static void DisableMediaPac")
    if enable_fn < 0 or disable_fn < 0:
        fail("EnableMediaPac/DisableMediaPac missing")
    else:
        body = proxy[enable_fn:disable_fn]
        if 'SetValue("ProxyEnable", 1' in body or "ProxyEnable\", 1" in body:
            fail("EnableMediaPac must not set ProxyEnable=1 (breaks TG independence)")
        else:
            ok("PAC-only (no ProxyEnable=1)")
        if "no ProxyEnable" not in body and 'SetValue("ProxyEnable", 0' not in body:
            fail("EnableMediaPac should clear/disable ProxyEnable")
        else:
            ok("ProxyEnable forced off for media PAC")

    # Process independence: StopAsync must not KillCoreWorkers (sibling HY2)
    stop = engine.find("public Task StopAsync")
    if stop < 0:
        fail("TunnelEngine.StopAsync missing")
    else:
        stop_body = engine[stop : stop + 1200]
        if "KillCoreWorkers" in stop_body:
            fail("StopAsync must not KillCoreWorkers (kills sibling HY2)")
        else:
            ok("StopAsync is PID-scoped (no KillCoreWorkers)")

    # Must not call selective Google TUN path for media anymore as primary
    if "ApplySelectiveRouteAddress" in store and "MaterializeService" in store:
        # ensure MaterializeService body does not call ApplySelectiveRouteAddress
        start = store.find("public static string MaterializeService")
        end = store.find("public static string Materialize(", start + 1)
        body = store[start:end]
        if "ApplySelectiveRouteAddress" in body:
            fail("MaterializeService still uses ApplySelectiveRouteAddress (TUN catch risk)")
        else:
            ok("MaterializeService is proxy-path (no selective Google TUN)")
        if "AppendTelegramTunInbound" in body or "AppendTelegramCidrs" in body:
            fail("MaterializeService must not merge Telegram TUN into media process")
        else:
            ok("MaterializeService has no Telegram merge")

    if "await RebuildMediaDpiAsync" in mw:
        fail("MainWindow still calls RebuildMediaDpiAsync")
    else:
        ok("no Media DPI path in MainWindow")

    # Discord Soft = vanilla Flowseal 1.10.3 (no OnixBoost desync rewrites)
    winws = (ROOT / "Services" / "WinwsEngine.cs").read_text(encoding="utf-8")
    start = winws.find("public static List<string> SanitizeForDiscordStream")
    if start < 0:
        fail("SanitizeForDiscordStream missing")
    else:
        body = winws[start : start + 800]
        if "return args;" not in body:
            fail("SanitizeForDiscordStream must be identity (vanilla Flowseal)")
        else:
            ok("Discord Soft sanitize is identity (vanilla Flowseal)")
        if "ApplySimpleFakeAltTcp" in body or "SoftenDiscord" in body:
            fail("must not rewrite Flowseal TCP/UDP for Discord Soft")
        else:
            ok("no Soften/rewrite helpers on Discord Soft path")

    order = json.loads((ROOT / "dpi" / "args" / "flowseal_auto_order.json").read_text(encoding="utf-8"))
    if order.get("version") != "1.10.3":
        fail(f"flowseal_auto_order version={order.get('version')}, want 1.10.3")
    else:
        ok("Flowseal strategies version 1.10.3")
    if order.get("order", [None])[0] != "flowseal_simple_fake":
        fail("Discord Soft sweep must try SIMPLE FAKE first")
    else:
        ok("sweep starts with SIMPLE FAKE")
    sf = (ROOT / "dpi" / "args" / "flowseal_simple_fake.txt").read_text(encoding="utf-8")
    for needle in ("filter-l7=discord,stun", "ipset-all", "list-general-user"):
        if needle not in sf:
            fail(f"flowseal_simple_fake.txt missing {needle}")
        else:
            ok(f"simple_fake has {needle}")

    if errors:
        print(f"\n{len(errors)} check(s) failed")
        return 1
    print("\nAll pre-ship checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
