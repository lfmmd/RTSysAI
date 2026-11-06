#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Read-only capability probe for the RTSys AI plugin ApiServer.

Usage:
    python tools/api_probe.py
    python tools/api_probe.py --json
    python tools/api_probe.py --host localhost --timeout 3

Only stdlib. Write routes (PUT/POST that change controller state) are listed
as SKIP_WRITE and are never sent. Exit codes: 0 = no defects, 1 = ERROR
verdict found, 2 = ApiServer unreachable (plugin not activated).
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime

DEFAULT_PORTS = (9090, 9091, 9092, 9093)
MAX_BODY = 4 * 1024 * 1024

# (id, method, path, kind)
ROUTES = [
    ("root", "GET", "/", "read"),
    ("status", "GET", "/api/status", "read"),
    ("programs", "GET", "/api/programs", "read"),
    ("program_source", "GET", "/api/programs/0/source", "read"),
    ("vr", "GET", "/api/vr/0", "read"),
    ("table", "GET", "/api/table/0", "read"),
    ("axes", "GET", "/api/axes", "read"),
    ("axis_detail", "GET", "/api/axes/0", "read"),
    ("project_items", "GET", "/api/project/items", "read"),
    ("ethercat_sdo", "GET",
     "/api/ethercat/sdo?slot=0&position=0&index=0x6040&subindex=0&type=uint16", "read"),
    ("drive", "GET", "/api/drive/0/0x6040", "read"),
    ("plugins", "GET", "/api/plugins", "read"),
    ("skills", "GET", "/api/skills", "read"),
    ("skill", "GET", "/api/skill/safe-coding", "read"),
    ("lookup", "GET", "/api/lookup?query=MOVE", "read"),
    ("tasks", "GET", "/api/tasks", "read"),
    ("memory", "GET", "/api/memory", "read"),

    # MP-only routes: must return a clean "not supported" JSON, never 500/NRE.
    ("mp_sysvars", "GET", "/api/sysvars", "mp"),
    ("mp_oscilloscope_open", "POST", "/api/oscilloscope/open", "mp"),
    ("mp_chat", "POST", "/api/chat", "mp"),
    ("mp_robots", "GET", "/api/robots", "mp"),
    ("mp_recipes", "GET", "/api/recipes", "mp"),
    ("mp_alarms", "GET", "/api/alarms", "mp"),
    ("mp_remote_devices", "GET", "/api/remote-devices", "mp"),
    ("mp_msbus_scan", "GET", "/api/msbus/scan", "mp"),
    ("mp_processes", "GET", "/api/processes", "mp"),
    ("mp_events", "GET", "/api/events", "mp"),

    # State-changing routes: never executed by this probe.
    ("w_vr", "PUT", "/api/vr/0", "write"),
    ("w_table", "PUT", "/api/table/0", "write"),
    ("w_compile", "POST", "/api/programs/0/compile", "write"),
    ("w_run", "POST", "/api/programs/0/run", "write"),
    ("w_stop", "POST", "/api/programs/0/stop", "write"),
    ("w_ethercat_sdo", "PUT", "/api/ethercat/sdo", "write"),
]

VERDICT_ORDER = ("OK", "NOT_CONFIGURED", "NOT_SUPPORTED", "ERROR",
                 "SKIP_WRITE", "UNREACHABLE")


def http_request(url, method, timeout):
    """Return (status, body_text, error_text). status is None on connect failure."""
    req = urllib.request.Request(url, method=method,
                                 headers={"Accept": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read(MAX_BODY)
            return resp.status, raw.decode("utf-8", "replace"), None
    except urllib.error.HTTPError as exc:
        raw = exc.read(MAX_BODY)
        return exc.code, raw.decode("utf-8", "replace"), None
    except urllib.error.URLError as exc:
        reason = getattr(exc, "reason", exc)
        if getattr(reason, "winerror", None) == 10061:
            return None, "", "connection refused (WinError 10061)"
        return None, "", "URLError: %s" % reason
    except Exception as exc:  # timeout, refused, reset...
        return None, "", "%s: %s" % (type(exc).__name__, exc)


def detect_port(host, ports, timeout):
    """Probe '/' on each candidate port; accept only this plugin's JSON root."""
    tried = []
    for port in ports:
        status, body, err = http_request(
            "http://%s:%d/" % (host, port), "GET", timeout)
        tried.append({"port": port, "status": status, "error": err})
        if status is not None and (
                "controllerConfigured" in body or "rtsysIp" in body):
            return port, tried
    return None, tried


def classify(status, body):
    low = body.lower()
    if "未将对象引用" in body or "nullreference" in low:
        return "ERROR"
    if status == 500:
        return "ERROR"
    if "rtsysip not configured" in low:
        return "NOT_CONFIGURED"
    if "not supported on rtsys" in low:
        return "NOT_SUPPORTED"
    if status is not None and 200 <= status < 300:
        try:
            json.loads(body)
            return "OK"
        except ValueError:
            return "ERROR"
    return "HTTP_%s" % status


def preview(body, limit=200):
    text = " ".join(body.split())
    return text[:limit]


def run(host, ports, timeout):
    port, tried = detect_port(host, ports, timeout)
    results = []

    if port is None:
        for route_id, method, path, kind in ROUTES:
            if kind == "write":
                results.append({"id": route_id, "method": method, "path": path,
                                "kind": kind, "status": None, "elapsed_ms": None,
                                "verdict": "SKIP_WRITE", "preview": "",
                                "error": None})
            else:
                results.append({"id": route_id, "method": method, "path": path,
                                "kind": kind, "status": None, "elapsed_ms": None,
                                "verdict": "UNREACHABLE", "preview": "",
                                "error": "no API port reachable (plugin not activated)"})
        return {"reachable": False, "port": None, "ports_tried": tried,
                "results": results}

    for route_id, method, path, kind in ROUTES:
        if kind == "write":
            results.append({"id": route_id, "method": method, "path": path,
                            "kind": kind, "status": None, "elapsed_ms": None,
                            "verdict": "SKIP_WRITE", "preview": "",
                            "error": None})
            continue

        url = "http://%s:%d%s" % (host, port, path)
        started = time.time()
        status, body, err = http_request(url, method, timeout)
        elapsed = round((time.time() - started) * 1000.0, 1)
        verdict = "UNREACHABLE" if err else classify(status, body)
        results.append({"id": route_id, "method": method, "path": path,
                        "kind": kind, "status": status, "elapsed_ms": elapsed,
                        "verdict": verdict, "preview": preview(body),
                        "error": err})

    return {"reachable": True, "port": port, "ports_tried": tried,
            "results": results}


def summarize(results):
    summary = dict((v, 0) for v in VERDICT_ORDER)
    for item in results:
        summary[item["verdict"]] = summary.get(item["verdict"], 0) + 1
    return summary


def render_markdown(report):
    lines = []
    lines.append("# RTSys AI Plugin API Capability Matrix")
    lines.append("")
    lines.append("- Timestamp: %s" % datetime.now().strftime("%Y-%m-%d %H:%M:%S"))
    if report["reachable"]:
        lines.append("- ApiServer: reachable on port %d" % report["port"])
    else:
        lines.append("- ApiServer: UNREACHABLE (plugin not activated or not listening on ports %s)"
                     % ", ".join(str(p) for p in DEFAULT_PORTS))
    lines.append("")
    lines.append("## Summary")
    lines.append("")
    lines.append("| Verdict | Count |")
    lines.append("|---|---:|")
    for verdict in VERDICT_ORDER:
        lines.append("| %s | %d |" % (verdict, summarize(report["results"]).get(verdict, 0)))
    lines.append("")
    lines.append("Legend: OK=200+valid JSON; NOT_CONFIGURED=RTSysIP missing; "
                 "NOT_SUPPORTED=MP route stub; ERROR=500 or NullReference; "
                 "SKIP_WRITE=write route not executed; UNREACHABLE=connection failed.")
    lines.append("")
    lines.append("## Capability Matrix")
    lines.append("")
    lines.append("| # | Method | Path | Kind | HTTP | ms | Verdict | Preview (200 chars) |")
    lines.append("|---:|---|---|---|---:|---:|---|---|")
    for index, item in enumerate(report["results"], 1):
        status = "-" if item["status"] is None else str(item["status"])
        elapsed = "-" if item["elapsed_ms"] is None else ("%.1f" % item["elapsed_ms"])
        text = item["error"] if item["error"] else item["preview"]
        text = text.replace("|", "\\|")
        lines.append("| %d | %s | `%s` | %s | %s | %s | **%s** | %s |"
                     % (index, item["method"], item["path"], item["kind"],
                        status, elapsed, item["verdict"], text))
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description="Read-only API probe for RTSys AI plugin")
    parser.add_argument("--host", default="localhost", help="ApiServer host (default: localhost)")
    parser.add_argument("--port", type=int, default=None,
                        help="force a port instead of scanning 9090-9093")
    parser.add_argument("--timeout", type=float, default=3.0,
                        help="per-request timeout seconds (default: 3)")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    args = parser.parse_args()

    ports = (args.port,) if args.port else DEFAULT_PORTS
    report = run(args.host, ports, args.timeout)
    report["summary"] = summarize(report["results"])

    if args.json:
        # ensure_ascii=True keeps machine output valid even when stdout is cp936.
        print(json.dumps(report, ensure_ascii=True, indent=2))
    else:
        print(render_markdown(report))

    if not report["reachable"]:
        return 2
    return 1 if report["summary"].get("ERROR", 0) else 0


if __name__ == "__main__":
    sys.exit(main())
