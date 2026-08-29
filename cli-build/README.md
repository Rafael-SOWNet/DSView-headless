# DSLogic headless CLI

A command-line capture and analysis tool for the DreamSourceLab **DSLogic U3Pro16**
(`2A0E:002A`), built against this fork's `libsigrok4DSL`. No GUI, no X server — intended for
CI, for remote benches, and for driving from an agent via the bundled MCP server.

It exists because mainline libsigrok's `dreamsourcelab-dslogic` driver does not recognise this
PID at all, and because DSView itself cannot be scripted.

## What it does

- **Capture** at up to 100 MHz into a raw sample file.
- **Demux** `LA_CROSS_DATA`, which is channel-interleaved in 8-byte (64-sample) blocks rather
  than bit-interleaved. Getting this wrong makes every channel look identical — a useful sanity
  check is `bytes == samples × channels / 8`.
- **Decode UART** with majority-vote bit sampling. Not optional on a real RS485 line: a
  pulse-width histogram of one capture found 211 of 339 transitions were 10–40 ns ringing
  spikes rather than real edges, and single-point sampling loses framing on them.
- **Pick the polarity automatically.** A and B of a differential pair carry the same data
  inverted, and which one a probe is clipped to is not knowable from the capture alone.
- **Analyse timing** — burst structure, device turnaround, host turnaround, inter-frame gaps,
  and intra-frame byte gaps, which is where USB/serial scheduling artefacts show up.
- **Detect bus idle/busy differentially** when both lines of a pair are captured.
- **Trigger** on an edge or level per channel, with a pre-trigger position.
- **Write VCD** so a capture opens in PulseView, GTKWave or DSView itself.
- **Recover a wedged FPGA** without a power cycle (see below).

Timestamps come from the sample index, so they are accurate to one sample period — 10 ns at
100 MHz. That is what makes the latency and jitter figures meaningful rather than indicative.

## Protocol decoding is a plugin

The tool deliberately knows nothing about any particular protocol. It gets you as far as "here
are the bytes and when each one started"; what those bytes *mean* is supplied by a plugin, so
that a proprietary or in-house protocol can be decoded without its format living in this
repository.

Implement `IProtocolDecoder` from `DslCli.Abstractions` (netstandard2.0, so a plugin keeps
working across host .NET upgrades), then drop the built assembly into `plugins/` next to the
executable, or point `DSLCLI_PLUGINS` at a directory:

```csharp
public sealed class MyDecoder : IProtocolDecoder
{
    public string Name => "MyProtocol";
    public IReadOnlyList<ProtocolFrame> Decode(IReadOnlyList<DecodedByte> bytes, int baud) { ... }
    public void PrintFrames(IReadOnlyList<DecodedByte> bytes, int baud, TextWriter output) { ... }
}
```

Do **not** ship `DslCli.Abstractions.dll` alongside your plugin — the host provides it, and a
second copy gives the loader two identical-looking types, at which point the plugin is silently
ignored. Reference it with `<Private>false</Private>`.

With no plugin installed the capture, UART decode, burst analysis and FPGA recovery all still
work; only frame-level decoding and its timing breakdown are skipped.

The MCP server has the same seam: set `DSLCLI_PY_DECODER` to a Python file exposing
`decode_frames(values, times, baud) -> list[str]`.

## Building

```sh
./build.sh                                   # the C helpers, via CMake + Ninja (MSYS2/mingw64)
dotnet build DslCli/DslCli.csproj -c Release # the CLI
```

## Usage

```sh
DslCli <out.raw> [rate] [secs] [channels] [decodeBaud] [decodeChannels] [vthVolts]
       [--trigger SPEC] [--trigger-pos PCT] [--vcd]

DslCli capture.raw 100000000 1.0 0,3 115200 0,3
DslCli capture.raw 100000000 1.0 0,3 0 0 0 --trigger ch0=F --trigger-pos 20 --vcd
```

Writes `<base>.raw`, `<base>.chN.raw`, and — when `decodeBaud` is given —
`<base>.chN.uart.txt`, `<base>.chN.bursts.csv`, and `<base>.chN.frames.csv` if a decoder plugin
is installed. `--vcd` adds `<base>.vcd`.

### Triggering

`--trigger` takes comma-separated `chK=COND` clauses; the `ch` prefix is optional and channels
you do not mention stay don't-care.

| COND | meaning |
|---|---|
| `R` | rising edge |
| `F` | falling edge |
| `1` | level high |
| `0` | level low |
| `E` | either edge |
| `X` | don't care |

`--trigger-pos` sets the pre-trigger capture position as a percentage. Without `--trigger` the
capture is free-running, exactly as before.

A trigger that never fires yields a successful run with zero bytes, which looks identical to
broken hardware — so the tool says explicitly when that is what happened.

## FPGA recovery

The DSLogic can end up with its FPGA in a state no amount of re-plugging fixes, because a USB
reset does not drop VBUS. `fpgaload2` recovers it **without** a power cycle by clearing the GPIF
`WORDWIDE` bit *before* pulsing `PROG_B` — pulsing `PROG_B` on its own leaves the device unable
to reconfigure, which is a good way to make the problem worse. The other small C tools
(`usblist`, `wordwide`, `usbreset`, `fpgareset`, `portpower`, `progtest`) are the diagnostics
that led there.

## MCP server

`mcp/dslogic_mcp.py` exposes `dslogic_capture`, `dslogic_decode`, `dslogic_status` and
`dslogic_recover`. Paths are resolved relative to the checkout; override with `DSLCLI_PATH`,
`DSLOGIC_WORK_DIR`, `DSLCLI_PY_DECODER`.

## Credits

The trigger handling and VCD output were ported from the `dsl-capture` CLI in
[wickerwaka's DSView fork](https://github.com/wickerwaka/DSView) (branch `headless-cli`), GPLv3
like the rest of this tree. That tool is a separate, Qt-free C implementation with its own
strengths — a demo-driver mode and a CLI-only CMake build among them — and is worth a look if you
want capture without the analysis side of this one.

## Licence

GPLv3, as inherited from DSView. This is a fork of
[Schildkroet/DSView](https://github.com/Schildkroet/DSView), itself a fork of DreamSourceLab's
DSView; everything outside `cli-build/` is upstream's work. See `COPYING` in the repository
root.
