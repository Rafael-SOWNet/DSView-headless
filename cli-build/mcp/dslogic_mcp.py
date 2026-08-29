"""
MCP server exposing the DSLogic U3Pro16 logic analyser.

Wraps cli-build/DslCli, which is the only thing that talks to this hardware headlessly -
mainline sigrok's dreamsourcelab-dslogic driver does not recognise PID 0x002A at all, so no
off-the-shelf tool works here.

Tools:
    dslogic_capture       capture, demux, optionally decode UART frames, and report timing
    dslogic_decode        re-decode an existing capture at a different baud, no hardware needed
    dslogic_status        is the device present and what state is its FPGA in
    dslogic_recover       clear a wedged FPGA (see the WORDWIDE note below)

Why a wrapper rather than a reimplementation: DslCli already carries the awkward parts - the
LA_CROSS_DATA demux, glitch rejection, and the FPGA reconfigure
that has to clear GPIF WORDWIDE first. Duplicating those here would mean two places to get
them wrong.
"""

import asyncio
import glob
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import TextContent, Tool

# This file lives at <repo>/cli-build/mcp/, so the build outputs sit alongside it. Resolved
# relatively rather than hardcoded, so the server works from any checkout location.
CLI_BUILD = Path(__file__).resolve().parents[1]

# Built by cli-build/DslCli. Override with DSLCLI_PATH if it lives elsewhere.
DEFAULT_DSLCLI = Path(
    os.environ.get(
        "DSLCLI_PATH",
        str(CLI_BUILD / "DslCli" / "DslCli" / "bin" / "Release" / "net11.0" / "DslCli.exe"),
    )
)
WORK_DIR = Path(os.environ.get("DSLOGIC_WORK_DIR", DEFAULT_DSLCLI.parent))

server = Server("dslogic")


def _run(args: list[str], timeout: int) -> tuple[int, str]:
    try:
        p = subprocess.run(
            args,
            cwd=str(WORK_DIR),
            capture_output=True,
            text=True,
            timeout=timeout,
            errors="replace",
        )
        return p.returncode, (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired:
        return 124, f"timed out after {timeout}s"
    except FileNotFoundError:
        return 127, f"DslCli not found at {DEFAULT_DSLCLI}"


def _summarise(out: str) -> str:
    """
    Keep the parts a caller actually reasons about and drop the libsigrok chatter.

    A capture emits hundreds of 'sr: dsl: receive_transfer' lines; passing those back would
    bury the per-channel statistics and the timing report in noise.
    """
    keep_prefixes = (
        "ForceFpgaReconfigure:",
        "Cross-data demux:",
        "  ch",
        "[polarity]",
        "[decode]",
        "[pair]",
        "  t=",
        "  ->",
        "Done.",
        "===",
        "  bursts:",
        "  frames:",
        "  bus driven",
        "    t=",
        "  NOTE:",
    )
    stat_words = ("turnaround", "duration", "idle", "period", "gap", "byte gap")
    lines = []
    for ln in out.splitlines():
        s = ln.rstrip()
        if not s:
            continue
        if s.startswith(("sr:", "srd:", "[event]", "[raw struct", "[logic packet")):
            continue
        if s.startswith(keep_prefixes) or any(w in s for w in stat_words):
            lines.append(s)
    return "\n".join(lines) if lines else out[-4000:]


@server.list_tools()
async def list_tools() -> list[Tool]:
    return [
        Tool(
            name="dslogic_capture",
            description=(
                "Capture logic-analyser data from the DSLogic U3Pro16 and analyse it. Demuxes the "
                "channel-interleaved buffer, writes a per-channel raw file, and - when decode_baud "
                "is given - decodes UART, runs any configured protocol decoder, and reports timing "
                "(device turnaround, host turnaround, frame durations, jitter). Capture BOTH lines "
                "of an RS485 pair (e.g. channels 0,1,2,3) to also get differential idle/busy "
                "detection, which reveals driver-enable windows even for transmissions too "
                "distorted to decode."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "out_name": {
                        "type": "string",
                        "description": "Base name for output files, e.g. 'capture'.",
                        "default": "capture",
                    },
                    "sample_rate_hz": {
                        "type": "integer",
                        "description": "Sample rate. 100000000 (100 MHz) gives 10 ns resolution, "
                        "which is what makes latency/jitter numbers meaningful.",
                        "default": 100000000,
                    },
                    "seconds": {"type": "number", "default": 2.0},
                    "channels": {
                        "type": "string",
                        "description": "Comma-separated channel list, e.g. '0,1,2,3'.",
                        "default": "0,1,2,3",
                    },
                    "decode_baud": {
                        "type": "integer",
                        "description": "UART baud to decode, e.g. 115200. Omit or 0 to capture only.",
                        "default": 0,
                    },
                    "decode_channels": {
                        "type": "string",
                        "description": "Channels to decode. Defaults to all captured channels.",
                    },
                    "threshold_volts": {
                        "type": "number",
                        "description": "Comparator threshold. Below ~1.0 V the comparator does not "
                        "resolve this bus; 1.1-2.3 V all work. Sweep it if a signal will not decode.",
                        "default": 1.5,
                    },
                },
            },
        ),
        Tool(
            name="dslogic_decode",
            description=(
                "Re-decode an already-captured file at a different baud or on different channels. "
                "Needs no hardware, so it is the cheap way to try alternatives before re-capturing."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "raw_file": {"type": "string", "description": "Path to a .raw capture."},
                    "channel_count": {"type": "integer", "description": "Channels in that capture."},
                    "channel": {"type": "integer", "description": "Channel to decode.", "default": 0},
                    "baud": {"type": "integer", "default": 115200},
                    "sample_rate_hz": {"type": "integer", "default": 100000000},
                    "invert": {
                        "type": "boolean",
                        "description": "Decode the inverted signal. A and B of an RS485 pair carry "
                        "the same data inverted; if bytes look like garbage, try this.",
                        "default": False,
                    },
                },
                "required": ["raw_file", "channel_count"],
            },
        ),
        Tool(
            name="dslogic_status",
            description=(
                "Report whether the DSLogic is present and what state its FPGA is in. FPGA_DONE=0 "
                "means the bitstream is not loaded; use dslogic_recover."
            ),
            inputSchema={"type": "object", "properties": {}},
        ),
        Tool(
            name="dslogic_recover",
            description=(
                "Recover a DSLogic whose FPGA will not configure (bitstream transfers fully, "
                "GPIF_DONE asserts, FPGA_DONE never does, so capture hangs). Clears GPIF WORDWIDE "
                "before reloading, which is the step that makes it work - the bitstream downloads "
                "over the same bus and a leftover 16-bit width corrupts it into a CRC failure."
            ),
            inputSchema={"type": "object", "properties": {}},
        ),
    ]


@server.call_tool()
async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent]:
    if name == "dslogic_capture":
        out_name = str(arguments.get("out_name", "capture"))
        if not re.fullmatch(r"[A-Za-z0-9._-]+", out_name):
            return [TextContent(type="text", text="out_name may only contain letters, digits, dot, dash, underscore.")]
        rate = int(arguments.get("sample_rate_hz", 100_000_000))
        secs = float(arguments.get("seconds", 2.0))
        chans = str(arguments.get("channels", "0,1,2,3"))
        baud = int(arguments.get("decode_baud", 0) or 0)
        dchans = str(arguments.get("decode_channels") or chans)
        vth = float(arguments.get("threshold_volts", 1.5))

        args = [
            str(DEFAULT_DSLCLI),
            f"{out_name}.raw",
            str(rate),
            repr(secs),
            chans,
            str(baud),
            dchans,
            repr(vth),
        ]
        # Generous headroom: the run includes an FPGA reconfigure plus post-capture analysis.
        rc, out = _run(args, timeout=int(secs) + 180)
        body = _summarise(out)
        if rc != 0:
            body = f"(DslCli exited {rc})\n{body}"
        return [TextContent(type="text", text=body)]

    if name == "dslogic_decode":
        raw = Path(str(arguments["raw_file"]))
        if not raw.is_absolute():
            raw = WORK_DIR / raw
        if not raw.exists():
            return [TextContent(type="text", text=f"no such file: {raw}")]
        nch = int(arguments["channel_count"])
        ch = int(arguments.get("channel", 0))
        baud = int(arguments.get("baud", 115200))
        rate = float(arguments.get("sample_rate_hz", 100_000_000))
        invert = bool(arguments.get("invert", False))
        return [TextContent(type="text", text=_decode_offline(raw, nch, ch, baud, rate, invert))]

    if name == "dslogic_status":
        exe = CLI_BUILD / "wordwide.exe"
        if not exe.exists():
            return [TextContent(type="text", text="status helper (wordwide.exe) not built")]
        rc, out = _run([str(exe)], timeout=30)
        return [TextContent(type="text", text=out.strip() or f"exit {rc}")]

    if name == "dslogic_recover":
        exe = CLI_BUILD / "fpgaload2.exe"
        if not exe.exists():
            return [TextContent(type="text", text="recovery helper (fpgaload2.exe) not built")]
        rc, out = _run([str(exe)], timeout=120)
        tail = "\n".join(out.strip().splitlines()[-12:])
        return [TextContent(type="text", text=tail or f"exit {rc}")]

    return [TextContent(type="text", text=f"unknown tool: {name}")]


def _load_frame_decoder():
    """
    Optional protocol frame decoder, loaded from a file named by DSLCLI_PY_DECODER.

    Same split as the C# side: this server demuxes and UART-decodes, and what the bytes *mean* is
    somebody else's module — often somebody else's to keep private. The module must expose
    decode_frames(values, times, baud) -> list[str], each string one formatted frame line.
    Returns None when nothing is configured, which is a normal, working state.
    """
    path = os.environ.get("DSLCLI_PY_DECODER")
    if not path:
        return None
    try:
        import importlib.util

        spec = importlib.util.spec_from_file_location("dslcli_frame_decoder", path)
        if spec is None or spec.loader is None:
            return None
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return getattr(mod, "decode_frames", None)
    except Exception:
        # A broken decoder must not take the byte-level decode down with it.
        return None


def _decode_offline(raw: Path, nch: int, ch: int, baud: int, rate: float, invert: bool) -> str:
    """
    Offline demux + UART decode, mirroring DslCli's logic.

    Kept in step with DslCli deliberately: the buffer is channel-interleaved in 8-byte blocks
    (see DSView's append_cross_payload) and bits are majority-voted to reject the sub-microsecond
    ringing an RS485 line produces. Protocol framing on top of the bytes is delegated to whatever
    DSLCLI_PY_DECODER names, if anything.
    """
    try:
        import numpy as np
    except ImportError:
        return "numpy is required for offline decoding"

    data = np.fromfile(raw, dtype=np.uint8)
    nblk = len(data) // 8
    if nblk < nch:
        return f"file too small: {len(data)} bytes"
    blocks = data[: nblk * 8].reshape(nblk, 8)
    chb = blocks[ch::nch].reshape(-1)
    bits = np.unpackbits(chb, bitorder="little").astype(np.int8)
    if invert:
        bits = 1 - bits

    spb = rate / baud
    half = max(1, int(spb * 0.25))

    def sample(centre: float) -> int:
        c = int(centre)
        lo, hi = max(0, c - half), min(len(bits), c + half + 1)
        if hi <= lo:
            return 1
        seg = bits[lo:hi]
        return 1 if seg.sum() * 2 > len(seg) else 0

    edges = np.flatnonzero((bits[:-1] == 1) & (bits[1:] == 0)) + 1
    out: list[tuple[float, int]] = []
    nxt = -1
    for e in edges:
        if e < nxt or e + int(spb * 10) >= len(bits):
            continue
        if sample(e + spb * 0.5):
            continue
        v = 0
        for k in range(8):
            if sample(e + spb * (1.5 + k)):
                v |= 1 << k
        out.append((e / rate, v))
        nxt = int(e + spb * 9.5)

    vals = [v for _, v in out]
    times = [t for t, _ in out]

    decode_frames = _load_frame_decoder()
    frames: list[str] = []
    if decode_frames is not None:
        try:
            frames = list(decode_frames(vals, times, baud))
        except Exception as exc:
            frames = [f"  (frame decoder failed: {type(exc).__name__}: {exc})"]

    head = " ".join("%02X" % v for v in vals[:48])
    summary = f"ch{ch} @ {baud} baud{' (inverted)' if invert else ''}: {len(out)} byte(s)"
    if decode_frames is not None:
        summary += f", {len(frames)} frame(s)"
    lines = [
        summary,
        f"  first bytes: {head}{' ...' if len(vals) > 48 else ''}",
    ]
    lines += frames[:40]
    if decode_frames is None:
        lines.append("  (no frame decoder configured - set DSLCLI_PY_DECODER to decode protocol frames)")
    elif not frames:
        lines.append("  (no valid frames - try invert=true, or a different baud/channel)")
    return "\n".join(lines)


async def main() -> None:
    async with stdio_server() as (r, w):
        await server.run(r, w, server.create_initialization_options())


if __name__ == "__main__":
    asyncio.run(main())
