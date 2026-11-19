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
- **Write pcap** of the decoded frames, for reading in Wireshark with the protocol's own
  dissector — when the decoder plugin supports it.
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

To export decoded frames to pcap, also implement `IPcapExportable` and return the raw frame
octets in `ProtocolFrame.Bytes`:

```csharp
public sealed class MyDecoder : IProtocolDecoder, IPcapExportable
{
    public int LinkType => 147;   // LINKTYPE_USER0..15 = 147..162
    ...
}
```

The host writes the file; it stamps the link type you name and copies the bytes you hand back,
without needing to understand the protocol. Ship a Wireshark dissector registered against the
same link type. The pcap is written in **nanosecond** format (magic `0xa1b23c4d`), because the
timestamps are sample-accurate — 10 ns at 100 MHz — and classic microsecond pcap would throw
most of that away.

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
       [--trigger SPEC] [--trigger-pos PCT] [--vcd] [--pcap]

DslCli capture.raw 100000000 1.0 0,3 115200 0,3
DslCli capture.raw 100000000 1.0 0,3 0 0 0 --trigger ch0=F --trigger-pos 20 --vcd
```

Writes `<base>.raw`, `<base>.chN.raw`, and — when `decodeBaud` is given —
`<base>.chN.uart.txt`, `<base>.chN.bursts.csv`, and `<base>.chN.frames.csv` if a decoder plugin
is installed. `--vcd` adds `<base>.vcd`; `--pcap` adds `<base>.chN.pcap`.

### A bus that changes rate mid-capture

`decodeBaud` may be a comma-separated list. Each stretch of activity is then decoded at every
listed rate, and the rate with the fewest framing and parity errors wins (ties go to the earlier
rate in the list). The first rate is the primary one: the protocol decoder receives it as its
default.

```sh
DslCli capture.raw 25000000 20 3 115200,250000 3 0 --framing 8E1
```

- **Segments are cut only at long idle gaps:** more than 12 bit times at the slowest rate. No idle
  stretch inside a character is that long, so a cut always falls between characters. A request and
  its answer usually share a segment. That does no harm, because both sides of an exchange use the
  same rate.
- **Ringing is ignored:** pulses shorter than a quarter bit at the fastest rate don't count when
  looking for those gaps.
- **Every decoded byte records its rate:** `DecodedByte.Baud`. A decoder should use that rate for
  frame end times and byte gaps. The rate passed to `Decode` is only the default.
- **Output:** `uart.txt` gets a fifth column with the rate (only with more than one rate). stdout
  gains a `[rates]` block with the segment count per rate, each rate change, and the segments that
  read cleanly at no rate. That last list is where a byte sent at the wrong rate shows up.

Measured on a GeoBUS capture with a 115200 → 250000 switch (2026-11-19, 25 MHz, 20 s): 3190
CRC-valid frames with both rates against 1585 at 115200 alone, and the three rate changes at the
same instants as a hand analysis.

### Analysing a capture again: `--from-raw`

`--from-raw` opens no device: it runs the decode and analysis on `<out.raw>` as it is on disk. The
file must have been captured with `[channels]` enabled at `[rate]`; `[secs]` is ignored, because the
length follows from the file size. A demuxed `<base>.chN.raw` is a one-channel capture, so pass `N`
alone as `[channels]`. In this mode the per-channel `.raw` files are not written again, and pcap
wall-clock stamps are approximate (file write time minus capture length). Intervals stay exact
to the sample.

```sh
DslCli bs1.ch3.raw 25000000 0 3 115200,250000 3 0 --framing 8E1 --from-raw
```

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
capture is free-running, exactly as before — and the tool says so rather than accepting the flag
and ignoring it. An out-of-range percentage is refused, not clamped.

**The pre-trigger window has a hardware ceiling.** This tool runs the device in STREAM mode, where
the firmware caps the pre-trigger buffer at **10% of the per-channel depth** (`dsl_fpga_arm` in
`dsl.c`; BUFFER mode allows 90%). Ask for more and you quietly get less, so the tool computes the
effective window the same way the firmware does and prints both:

```
trigger position: 50% of 100000000 samples = 50000000 requested, 13421772 effective
                  (STREAM ceiling 13421772 = 10% of 134217728 per-channel depth)
NOTE: the pre-trigger window is capped to 13421772 sample(s) (13.42% of this capture) ...
```

The ceiling scales with the per-channel depth, so it binds sooner the more channels you enable.
At typical capture lengths it is nowhere near — a 20 M-sample capture on two channels has a
53.7 M-sample ceiling — but on long or wide captures it decides your trigger position for you.

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
