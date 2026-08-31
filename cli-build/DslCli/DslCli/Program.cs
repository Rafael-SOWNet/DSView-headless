using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Linq;
using System.Text.Json;
using DslCli.Abstractions;
using DslCli.Abstractions;
using System.Threading;

// Minimal headless CLI for the DSLogic ("USB-based DSL Instrument v2" / DSLogic U3Pro16),
// built against a custom shared-library build of the Schildkroet/DSView fork's
// libsigrok4DSL (mainline libsigrok's dreamsourcelab-dslogic driver does not recognize
// this PID at all - see BACKPORTS.md / dsl.h in that fork for the 0x2A0E:0x002A entry).
//
// This talks to the fork's own high-level ds_* API (NOT the raw sr_* driver API) -
// ds_lib_init/ds_get_device_list/ds_active_device/ds_start_collect/ds_stop_collect,
// receiving samples via a ds_datafeed_callback_t delivering SR_DF_LOGIC packets.

// Direct libusb access, used ONLY to pulse the FPGA's PROG_B pin before the device is
// activated. See ForceFpgaReconfigure() in Program for why that is necessary.
internal static class UsbNative
{
    private const string Usb = "libusb-1.0.dll";

    [DllImport(Usb)] public static extern int libusb_init(out IntPtr ctx);
    [DllImport(Usb)] public static extern void libusb_exit(IntPtr ctx);
    [DllImport(Usb)] public static extern IntPtr libusb_open_device_with_vid_pid(IntPtr ctx, ushort vid, ushort pid);
    [DllImport(Usb)] public static extern void libusb_close(IntPtr devHandle);
    [DllImport(Usb)] public static extern int libusb_claim_interface(IntPtr devHandle, int iface);
    [DllImport(Usb)] public static extern int libusb_release_interface(IntPtr devHandle, int iface);
    [DllImport(Usb)] public static extern int libusb_control_transfer(IntPtr devHandle,
        byte requestType, byte request, ushort value, ushort index,
        byte[] data, ushort length, uint timeout);

    public const byte LIBUSB_REQUEST_TYPE_VENDOR = 0x40;
    public const byte LIBUSB_ENDPOINT_IN = 0x80;
    public const byte LIBUSB_ENDPOINT_OUT = 0x00;

    // from libsigrok4DSL/hardware/DSL/command.h
    public const byte CMD_CTL_WR = 0xb0;
    public const byte CMD_CTL_RD_PRE = 0xb1;
    public const byte CMD_CTL_RD = 0xb2;
    public const byte DSL_CTL_HW_STATUS = 2;
    public const byte DSL_CTL_PROG_B = 3;
    public const byte DSL_CTL_LED = 5;
    public const byte DSL_CTL_WORDWIDE = 7;
    public const byte bmWR_PROG_B = 1 << 2;
    public const byte bmLED_GREEN = 1 << 0;
    public const byte bmLED_RED = 1 << 1;
    public const byte bmFPGA_DONE = 1 << 6;

    public const ushort DSLOGIC_VID = 0x2A0E;
    public const ushort DSLOGIC_PID = 0x002A;

    // struct ctl_header { uint8 dest; uint16 offset; uint8 size; } then payload bytes.
    public static byte[] BuildCtlWrite(byte dest, byte value)
        => new byte[] { dest, 0, 0, 1, value };
}

internal static class Native
{
    private const string Lib = "libsigrok4dsl.dll";
    private const string Glib = "libglib-2.0-0.dll";

    [DllImport(Lib)] public static extern int ds_lib_init();
    [DllImport(Lib)] public static extern int ds_lib_exit();
    [DllImport(Lib)] public static extern int ds_trigger_reset();
    [DllImport(Lib)] public static extern int ds_trigger_set_en(ushort enable);
    [DllImport(Lib)] public static extern int ds_trigger_set_mode(ushort mode);
    [DllImport(Lib)] public static extern int ds_trigger_set_pos(ushort position);
    [DllImport(Lib)] public static extern int ds_trigger_probe_set(ushort probe, byte trigger0, byte trigger1);

    /// <summary>SIMPLE_TRIGGER from libsigrok.h - single-stage, one condition per channel.</summary>
    public const ushort SIMPLE_TRIGGER = 0;
    [DllImport(Lib)] public static extern void ds_log_level(int level);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern void ds_set_firmware_resource_dir(string dir);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern void ds_set_user_data_dir(string dir);

    public delegate void EventCallback(int ev);
    public delegate void DatafeedCallback(IntPtr sdi, IntPtr packet);

    [DllImport(Lib)] public static extern void ds_set_event_callback(EventCallback cb);
    [DllImport(Lib)] public static extern void ds_set_datafeed_callback(DatafeedCallback cb);

    [DllImport(Lib)] public static extern int ds_get_device_list(out IntPtr outList, out int outCount);
    [DllImport(Lib)] public static extern int ds_active_device(ulong handle);
    [DllImport(Lib)] public static extern int ds_active_device_by_index(int index);
    [DllImport(Lib)] public static extern int ds_have_actived_device();
    [DllImport(Lib)] public static extern int ds_reload_device_list();

    [DllImport(Lib)] public static extern int ds_start_collect();
    [DllImport(Lib)] public static extern int ds_stop_collect();
    [DllImport(Lib)] public static extern int ds_is_collecting();
    [DllImport(Lib)] public static extern int ds_release_actived_device();
    [DllImport(Lib)] public static extern int ds_get_last_error();

    [DllImport(Lib)] public static extern int ds_enable_device_channel_index(int chIndex, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(Lib)] public static extern int ds_set_actived_device_config(IntPtr ch, IntPtr cg, int key, IntPtr data);
    [DllImport(Lib)] public static extern int ds_get_actived_device_config(IntPtr ch, IntPtr cg, int key, out IntPtr data);
    [DllImport(Lib)] public static extern int ds_get_actived_device_config_list(IntPtr cg, int key, out IntPtr data);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern IntPtr ds_get_actived_device_config_info_by_name(string optname);
    [DllImport(Lib)] public static extern IntPtr ds_get_actived_device_config_info(int key);

    [DllImport(Glib)] public static extern IntPtr g_variant_get_fixed_array(IntPtr value, out UIntPtr nElements, UIntPtr elementSize);

    [DllImport(Glib)] public static extern IntPtr g_variant_new_uint64(ulong value);
    [DllImport(Glib)] public static extern IntPtr g_variant_new_double(double value);
    [DllImport(Glib)] public static extern IntPtr g_variant_new_int16(short value);
    [DllImport(Glib)] public static extern IntPtr g_variant_new_byte(byte value);
    [DllImport(Glib)] public static extern IntPtr g_variant_new_boolean([MarshalAs(UnmanagedType.Bool)] bool value);
    [DllImport(Glib, CharSet = CharSet.Ansi)] public static extern IntPtr g_variant_new_string(string value);
    [DllImport(Glib)] public static extern ulong g_variant_get_uint64(IntPtr variant);
    [DllImport(Glib)] public static extern short g_variant_get_int16(IntPtr variant);
    [DllImport(Glib)] public static extern double g_variant_get_double(IntPtr variant);
    [DllImport(Glib)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool g_variant_get_boolean(IntPtr variant);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DsDeviceBaseInfo
{
    public ulong handle;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 150)] public string name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string unique_id;
}

// Explicit offsets matching the C struct's natural x64 alignment exactly -
// auto-layout garbage values were observed for the "middle" fields (format/
// index/order/unitsize), so leave nothing to LayoutKind.Sequential's guesswork.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct SrDatafeedPacket
{
    [FieldOffset(0)] public ushort type;
    [FieldOffset(2)] public ushort status;
    [FieldOffset(8)] public IntPtr payload;
    [FieldOffset(16)] public int bExportOriginalData;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct SrDatafeedLogic
{
    [FieldOffset(0)] public ulong length;
    [FieldOffset(8)] public int format;
    [FieldOffset(12)] public ushort index;
    [FieldOffset(14)] public ushort order;
    [FieldOffset(16)] public ushort unitsize;
    [FieldOffset(18)] public ushort data_error;
    [FieldOffset(24)] public ulong error_pattern;
    [FieldOffset(32)] public IntPtr data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SrConfigInfo
{
    public int key;
    public int datatype;
    public IntPtr name; // const char*
}

internal static class SrConf
{
    public const int DEVICE_MODE = 30004;
    public const int DEVICE_SESSIONS = 30099;
}

internal static class SrT
{
    public const int UINT64 = 10000;
    public const int UINT8 = 10001;
    public const int CHAR = 10002;
    public const int BOOL = 10003;
    public const int FLOAT = 10004;
    public const int RATIONAL_PERIOD = 10005;
    public const int RATIONAL_VOLT = 10006;
    public const int KEYVALUE = 10007;
    public const int LIST = 10008;
    public const int INT16 = 10009;
}

internal static class DevMode
{
    public const short LOGIC = 0;
}

internal static class SrDf
{
    public const ushort HEADER = 10000;
    public const ushort END = 10001;
    public const ushort META = 10002;
    public const ushort TRIGGER = 10003;
    public const ushort LOGIC = 10004;
}

internal class Program
{
    private static FileStream? _rawOut;
    private static long _totalBytes;
    private static readonly object _lock = new();
    private static string _outBaseName = "capture";
    private static bool _loggedFormat;
    private static PcapSink? _pcapSink;

    // Number of enabled channels - needed to demux LA_CROSS_DATA (see CrossDemux).
    private static int _channelCount = 16;

    private static void OnEvent(int ev)
    {
        Console.WriteLine($"[event] {ev}");
    }

    private static void OnDatafeed(IntPtr sdi, IntPtr packetPtr)
    {
        var packet = Marshal.PtrToStructure<SrDatafeedPacket>(packetPtr);
        if (packet.type == SrDf.LOGIC && packet.payload != IntPtr.Zero)
        {
            var logic = Marshal.PtrToStructure<SrDatafeedLogic>(packet.payload);
            if (!_loggedFormat)
            {
                _loggedFormat = true;
                var raw = new byte[48];
                Marshal.Copy(packet.payload, raw, 0, 48);
                Console.WriteLine($"[raw struct bytes] {BitConverter.ToString(raw).Replace("-", " ")}");
                Console.WriteLine($"[logic packet] format={logic.format} (0=CROSS,1=SPLIT) index={logic.index} unitsize={logic.unitsize} order={logic.order} length={logic.length} data={logic.data}");
            }
            if (logic.length > 0 && logic.data != IntPtr.Zero)
            {
                var buf = new byte[logic.length];
                Marshal.Copy(logic.data, buf, 0, (int)logic.length);
                lock (_lock)
                {
                    // Only the interleaved stream is written here; per-channel files and any
                    // decoding are produced afterwards by CrossDemux(), which needs the whole
                    // capture. Note logic.index/unitsize/order are uninitialised stack garbage for
                    // the LA_CROSS_DATA path (dsl.c's receive_transfer only sets length/format/
                    // data_error/data), so they must not be used to route data anywhere.
                    _rawOut?.Write(buf, 0, buf.Length);
                    _totalBytes += buf.Length;
                }
            }
        }
        else if (packet.type == SrDf.END)
        {
            Console.WriteLine("[datafeed] SR_DF_END");
        }
    }

    // ---------------------------------------------------------------------------------------
    // LA_CROSS_DATA demux
    //
    // The capture buffer is NOT sample-interleaved. Per DSView's own reader
    // (DSView/pv/data/logicsnapshot.cpp, append_cross_payload) it is channel-interleaved in
    // 8-byte blocks:
    //
    //     _byte_fraction = (_byte_fraction + 1) % 8;   // 8 bytes = 64 samples for one channel
    //     ...
    //     _ch_fraction = (_ch_fraction + 1) % _channel_num;   // then move to the next channel
    //
    // So block 0 is channel 0, block 1 is channel 1, ... wrapping after _channel_num blocks, and
    // within a block the bits are consecutive samples of that one channel, LSB first.
    //
    // Reading this as sample-interleaved 16-bit words (the obvious but wrong assumption) makes
    // every channel look identical and produces almost nothing but 0x0000 and 0xFFFF, because a
    // run of idle samples on a single channel fills a whole word. Single edges then show up as
    // contiguous-bit values like 0x0007 or 0xFF80, which look like glitches but are really just
    // one transition inside a 64-sample block. Total size is a good sanity check:
    // bytes == samples * channels / 8.
    private static byte[][] CrossDemux(byte[] raw, int channelCount)
    {
        int blocks = raw.Length / 8;
        int blocksPerCh = blocks / channelCount;
        var outp = new byte[channelCount][];
        for (int ch = 0; ch < channelCount; ch++)
        {
            var b = new byte[blocksPerCh * 8];
            for (int i = 0; i < blocksPerCh; i++)
                Array.Copy(raw, (i * channelCount + ch) * 8, b, i * 8, 8);
            outp[ch] = b;
        }
        return outp;
    }

    private static bool GetBit(byte[] chBytes, long sample)
    {
        long byteIdx = sample >> 3;
        if (byteIdx < 0 || byteIdx >= chBytes.Length) return true; // treat off-end as idle high
        return (chBytes[byteIdx] & (1 << (int)(sample & 7))) != 0;
    }

    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    // Majority vote over a window centred on the bit's midpoint.
    //
    // This is not optional on this bus: a real RS485 line here shows heavy ringing, and a
    // pulse-width histogram of one capture found 211 of 339 transitions were 1-4 sample
    // (10-40 ns) spikes rather than real edges. Single-point sampling triggers on those, the
    // decoder loses framing, and whole frames disappear - which is why an earlier version of this
    // tool saw only the slave's responses and none of the master's requests.
    private static bool SampleBit(byte[] chBytes, double centre, double spb)
    {
        long half = Math.Max(1, (long)(spb * 0.25));
        long c = (long)centre;
        long ones = 0, n = 0;
        for (long s = c - half; s <= c + half; s++, n++)
            if (GetBit(chBytes, s)) ones++;
        return ones * 2 > n;
    }

    // 8-N-1 UART decode with glitch rejection. A start bit must be a genuine falling edge whose
    // low level survives a majority vote at the midpoint; each data bit is likewise majority-voted.
    // How a character is framed on the wire. Not cosmetic: it sets where the stop bit is, and
    // getting it wrong makes the stop-bit check land on the parity bit instead.
    //
    // Measured on a GeoBUS bus 2026-08-31, three ways agreeing: inter-byte spacing 95.4 us = 11 bit
    // times exactly at 115200 (11/115200 = 95.49 us); frame durations matching 14 bytes x 11 bits;
    // and the bit at 9.5 matching EVEN parity of the data byte in 15792 of 15792 characters. That
    // bus is 8-E-1, and the previously hardcoded 8-N-1 sampled parity and reported it as framing -
    // 71% "framing errors" on a bus whose every frame CRC-validated.
    //
    // Default stays None so existing users of this tool see no change; pass --framing 8E1 for a bus
    // that carries parity.
    private enum ParityMode { None, Even, Odd }

    private static List<DecodedByte> DecodeUart(byte[] chBytes, double sampleRate, int baud, bool invert = false,
                                               ParityMode parity = ParityMode.None)
    {
        if (invert)
        {
            var inv = new byte[chBytes.Length];
            for (int i = 0; i < chBytes.Length; i++) inv[i] = (byte)~chBytes[i];
            chBytes = inv;
        }
        double spb = sampleRate / baud;
        long total = (long)chBytes.Length * 8;
        var result = new List<DecodedByte>();
        long next = 0;
        // A real edge must be preceded by a stable idle-high period, otherwise ringing on the
        // trailing edge of the previous bit looks like a new start bit.
        long guard = Math.Max(1, (long)(spb * 0.25));

        for (long s = 1; s < total; s++)
        {
            if (s < next) continue;
            if (!(GetBit(chBytes, s - 1) && !GetBit(chBytes, s))) continue;

            bool stableBefore = true;
            for (long g = s - guard; g < s; g++)
                if (!GetBit(chBytes, g)) { stableBefore = false; break; }
            if (!stableBefore) continue;

            if (SampleBit(chBytes, s + spb * 0.5, spb)) continue;   // start bit must read low

            int val = 0;
            for (int b = 0; b < 8; b++)
                if (SampleBit(chBytes, s + spb * (1.5 + b), spb)) val |= 1 << b;

            // With parity the character is 11 bits: parity sits at 9.5 and the stop bit at 10.5.
            // Without it the stop bit is at 9.5. Sampling the wrong position is the whole bug this
            // replaces - it does not corrupt the data bits, so the byte values look right while the
            // framing flag reports something else entirely.
            bool parityOk = true;
            double stopPos = 9.5;
            if (parity != ParityMode.None)
            {
                bool parityBit = SampleBit(chBytes, s + spb * 9.5, spb);
                int ones = System.Numerics.BitOperations.PopCount((uint)val);
                bool expected = parity == ParityMode.Even ? (ones % 2 == 1) : (ones % 2 == 0);
                parityOk = parityBit == expected;
                stopPos = 10.5;
            }
            bool stopOk = SampleBit(chBytes, s + spb * stopPos, spb);

            result.Add(new DecodedByte(s / sampleRate, (byte)val, stopOk, parityOk));
            // Advance past the real end of the character, not past 9.5 - otherwise the scan re-enters
            // the tail of the character just decoded and leans on the idle guard to recover.
            next = s + (long)(spb * stopPos);
        }
        return result;
    }

    private static string Lower(FrameKind kind) => kind.ToString().ToLowerInvariant();

    // Protocol decoding is a plugin, not a built-in.
    //
    // The host's job stops at "here are the bytes and when each one started"; what those bytes mean
    // is somebody else's, and often somebody else's to keep private. Decoders are therefore ordinary
    // assemblies discovered at run time: drop one next to the executable in plugins/, or point
    // DSLCLI_PLUGINS at a directory, and it is picked up with no rebuild of this tool.
    private static List<IProtocolDecoder> LoadDecoders()
    {
        var decoders = new List<IProtocolDecoder>();
        var dir = Environment.GetEnvironmentVariable("DSLCLI_PLUGINS");
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(dir)) return decoders;

        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            try
            {
                foreach (var t in System.Reflection.Assembly.LoadFrom(dll).GetTypes())
                {
                    if (!typeof(IProtocolDecoder).IsAssignableFrom(t) || t.IsAbstract || t.IsInterface) continue;
                    if (Activator.CreateInstance(t) is IProtocolDecoder d) decoders.Add(d);
                }
            }
            catch (Exception ex)
            {
                // A plugin that will not load is worth one line, not a crash: the capture and every
                // protocol-independent analysis below still work without it.
                Console.WriteLine($"[plugin] skipped {Path.GetFileName(dll)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return decoders;
    }

    private sealed record Burst(double TStart, double TEnd, int Transitions);

    // Differential bus state from BOTH lines of the pair.
    //
    // While a transmitter is enabled the pair is driven complementary (A != B). When every driver
    // is disabled the lines are undriven and settle to the same level via the bias/termination
    // network (A == B). So A XOR B is a direct driver-enable indicator - it does not depend on
    // decoding anything, and it sees the driver turn-on/turn-off that surrounds the data, which a
    // single-ended view cannot distinguish from an idle line.
    //
    // This is what makes it possible to separate "the bus is quiet because nobody is talking" from
    // "a driver is on but sending idle/mark", and it brackets the true RS485 turnaround.
    private static void AnalyzeBusState(string label, byte[] chA, byte[] chB,
                                        double sampleRate, double minBusySeconds)
    {
        Console.WriteLine($"\n=== differential bus state: {label} ===");
        long total = Math.Min(chA.Length, chB.Length) * 8L;
        long minBusy = Math.Max(1, (long)(minBusySeconds * sampleRate));

        // Two clean passes rather than one stateful one: collect raw driven runs, then merge runs
        // that are separated only by brief undriven blips (ringing crossing zero mid-byte), then
        // drop anything still too short to be a real transmission.
        var runs = new List<(long, long)>();
        long drivenSamples = 0;
        long runStart = -1;
        for (long s = 0; s < total; s++)
        {
            bool driven = GetBit(chA, s) != GetBit(chB, s);
            if (driven)
            {
                drivenSamples++;
                if (runStart < 0) runStart = s;
            }
            else if (runStart >= 0)
            {
                runs.Add((runStart, s));
                runStart = -1;
            }
        }
        if (runStart >= 0) runs.Add((runStart, total));

        long mergeGap = (long)(minBusySeconds * sampleRate * 8);   // ~2 character times
        var merged = new List<(long, long)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && r.Item1 - merged[^1].Item2 <= mergeGap)
                merged[^1] = (merged[^1].Item1, r.Item2);
            else
                merged.Add(r);
        }

        var busy = new List<(double, double)>();
        foreach (var m in merged)
            if (m.Item2 - m.Item1 >= minBusy)
                busy.Add((m.Item1 / sampleRate, m.Item2 / sampleRate));

        double pct = total > 0 ? 100.0 * drivenSamples / total : 0;
        Console.WriteLine(string.Format(Inv, "  bus driven {0:F4}% of the capture ({1} driver-enable window(s))",
                                        pct, busy.Count));

        var durs = new List<double>();
        var gaps = new List<double>();
        for (int i = 0; i < busy.Count; i++)
        {
            durs.Add(busy[i].Item2 - busy[i].Item1);
            if (i > 0) gaps.Add(busy[i].Item1 - busy[i - 1].Item2);
        }
        Console.WriteLine(Stats("driver-enabled duration", durs));
        Console.WriteLine(Stats("bus idle (all drivers off)", gaps));

        // Each driver-enable window spans a whole transaction (request + turnaround + response),
        // so the idle BETWEEN two windows is the host's own turnaround: the FTDI/USB read path
        // delivering the response, the application acting on it, and the USB write path putting
        // the next request on the wire.
        //
        // Split by magnitude, because the two populations mean different things: a short gap is
        // the host going straight into its next transaction (this is the USB/FTDI + application
        // latency), while a long one is the host simply idle between polls and says nothing about
        // stack latency. Averaging them together is meaningless.
        const double BackToBackMax = 0.005;
        var hostGap = new List<double>();
        var pollGap = new List<double>();
        foreach (var g in gaps) (g <= BackToBackMax ? hostGap : pollGap).Add(g);
        Console.WriteLine(Stats("HOST turnaround (back-to-back)", hostGap));
        Console.WriteLine(Stats("host idle between polls", pollGap));

        Console.WriteLine("\n  driver-enable windows (first 20):");
        for (int i = 0; i < Math.Min(busy.Count, 20); i++)
        {
            string g = i == 0 ? "     -" : string.Format(Inv, "{0:F3}", (busy[i].Item1 - busy[i - 1].Item2) * 1000);
            Console.WriteLine(string.Format(Inv, "    t={0,12:F9}s driven={1,8:F3}ms idle_before={2,9}ms",
                busy[i].Item1, (busy[i].Item2 - busy[i].Item1) * 1000, g));
        }
    }

    // Group line activity into bursts separated by idle gaps.
    //
    // This is deliberately independent of UART decoding. On this bus the slave's frames decode
    // cleanly but the master's transmission arrives distorted at the tap point (measured run
    // lengths of 901/836/638/319 samples, i.e. not multiples of the 868-sample bit time at
    // 115200), so it cannot be byte-decoded there. Its EDGES are still sample-accurate, so
    // burst boundaries give exact request/response timing even when the bytes are unrecoverable -
    // which is what latency and jitter analysis actually needs.
    //
    // minGap should be a few character times; anything shorter is inter-byte spacing within one
    // transmission rather than a real turnaround.
    private static List<Burst> FindBursts(byte[] chBytes, double sampleRate, double minGapSeconds,
                                          int glitchSamples)
    {
        long total = (long)chBytes.Length * 8;
        long minGap = (long)(minGapSeconds * sampleRate);
        var bursts = new List<Burst>();

        long burstStart = -1, lastEdge = -1;
        int count = 0;
        bool prev = GetBit(chBytes, 0);
        long runStart = 0;

        for (long s = 1; s < total; s++)
        {
            bool cur = GetBit(chBytes, s);
            if (cur == prev) continue;
            // Reject ringing: only treat this as a real edge if the previous level was held
            // for longer than the glitch threshold.
            bool realEdge = (s - runStart) >= glitchSamples;
            prev = cur; runStart = s;
            if (!realEdge) continue;

            if (burstStart < 0) { burstStart = s; count = 0; }
            else if (s - lastEdge > minGap)
            {
                bursts.Add(new Burst(burstStart / sampleRate, lastEdge / sampleRate, count));
                burstStart = s; count = 0;
            }
            lastEdge = s; count++;
        }
        if (burstStart >= 0)
            bursts.Add(new Burst(burstStart / sampleRate, lastEdge / sampleRate, count));
        return bursts;
    }

    private static void AnalyzeBursts(string label, List<Burst> bursts, IReadOnlyList<ProtocolFrame> frames)
    {
        Console.WriteLine($"\n=== bus activity bursts: {label} ===");
        if (bursts.Count == 0) { Console.WriteLine("  no activity"); return; }

        var durations = new List<double>();
        var gaps = new List<double>();
        var turnarounds = new List<double>();
        var cycle = new List<double>();

        for (int i = 0; i < bursts.Count; i++)
        {
            durations.Add(bursts[i].TEnd - bursts[i].TStart);
            if (i > 0)
            {
                gaps.Add(bursts[i].TStart - bursts[i - 1].TEnd);
                cycle.Add(bursts[i].TStart - bursts[i - 1].TStart);
            }
        }

        // Split the inter-burst gaps into two populations rather than averaging them together.
        // A gap of a few hundred microseconds is a device turning the bus around inside one
        // transaction; a gap of ~1 s is the host waiting before it polls again. Mixing them
        // produces a meaningless mean (measured: 397 ms +/- 522 ms across both).
        const double IntraTransactionMax = 0.005;   // 5 ms
        var idleBetweenTransactions = new List<double>();
        for (int i = 1; i < bursts.Count; i++)
        {
            double g = bursts[i].TStart - bursts[i - 1].TEnd;
            if (g <= IntraTransactionMax) turnarounds.Add(g);
            else idleBetweenTransactions.Add(g);
        }

        // Host-side (USB/FTDI + application) latency, measurable purely from the wire.
        //
        // A burst that contains a CRC-valid response is the slave talking. The NEXT burst is the
        // master talking again, so the gap between them covers the entire host round trip: the
        // FTDI/USB read path delivering the response up to the application, the application
        // deciding what to send, and the USB write path putting the next request on the wire.
        // That is the host-side counterpart to the device turnaround measured above.
        var hostTurnaround = new List<double>();
        for (int i = 0; i + 1 < bursts.Count; i++)
        {
            bool isResponse = frames.Any(f => f.Kind == FrameKind.Response &&
                                                 f.TStart >= bursts[i].TStart - 1e-6 && f.TStart <= bursts[i].TEnd);
            if (!isResponse) continue;
            bool nextIsResponse = frames.Any(f => f.Kind == FrameKind.Response &&
                                                 f.TStart >= bursts[i + 1].TStart - 1e-6 && f.TStart <= bursts[i + 1].TEnd);
            if (nextIsResponse) continue;   // slave still talking, not a host turnaround
            hostTurnaround.Add(bursts[i + 1].TStart - bursts[i].TEnd);
        }

        Console.WriteLine($"  bursts: {bursts.Count}");
        Console.WriteLine(Stats("burst duration", durations));
        Console.WriteLine(Stats($"device turnaround (<= {IntraTransactionMax * 1000:F0} ms)", turnarounds));
        Console.WriteLine(Stats("host turnaround (resp->next req)", hostTurnaround));
        Console.WriteLine(Stats("idle between transactions", idleBetweenTransactions));
        Console.WriteLine(Stats("burst-to-burst period", cycle));

        Console.WriteLine("\n  bursts (first 30):");
        for (int i = 0; i < Math.Min(bursts.Count, 30); i++)
        {
            var b = bursts[i];
            string gap = i == 0 ? "     -" : string.Format(Inv, "{0:F3}", (b.TStart - bursts[i - 1].TEnd) * 1000);
            // Only claim a burst IS a frame when the frame actually starts inside it - a loose
            // tolerance previously labelled request bursts as responses.
            var f = frames.FirstOrDefault(x => x.TStart >= b.TStart - 1e-6 && x.TStart <= b.TEnd);
            string what = f is null ? "(no decoded frame - this side may be distorted at this tap)"
                                    : $"{Lower(f.Kind)} {f.Summary}";
            Console.WriteLine(string.Format(Inv,
                "    t={0,12:F9}s dur={1,8:F3}ms edges={2,-5} idle_before={3,9}ms  {4}",
                b.TStart, (b.TEnd - b.TStart) * 1000, b.Transitions, gap, what));
        }
    }

    private static string Stats(string label, List<double> xs, string unit = "ms", double scale = 1000.0)
    {
        if (xs.Count == 0) return $"  {label,-28} (none)";
        var s = new List<double>(xs); s.Sort();
        double mean = 0; foreach (var x in s) mean += x; mean /= s.Count;
        double var2 = 0; foreach (var x in s) var2 += (x - mean) * (x - mean);
        double sd = s.Count > 1 ? Math.Sqrt(var2 / (s.Count - 1)) : 0;
        double P(double f) { return s[Math.Min(s.Count - 1, (int)(f * (s.Count - 1)))]; }
        return string.Format(Inv,
            "  {0,-28} n={1,-5} min={2,8:F3} p50={3,8:F3} p95={4,8:F3} max={5,8:F3} mean={6,8:F3} sd={7,7:F3} {8}",
            label, s.Count, s[0] * scale, P(0.50) * scale, P(0.95) * scale, s[^1] * scale, mean * scale, sd * scale, unit);
    }

    // Behavioural timing report. Everything is derived from start-bit edges, which are accurate to
    // one sample period (10 ns at 100 MHz), so these numbers reflect what is physically on the bus
    // rather than anything the host's clock or USB stack reports.
    private static void AnalyzeTiming(string label, IReadOnlyList<ProtocolFrame> frames, List<DecodedByte> bytes, int baud)
    {
        Console.WriteLine($"\n=== timing analysis: {label} ===");
        if (frames.Count == 0) { Console.WriteLine("  no frames"); return; }

        double charTime = 11.0 / baud;
        var reqResp = new List<double>();      // end of request -> start of response (device turnaround)
        var reqPeriod = new List<double>();    // request start -> next request start (host polling rate)
        var intraGap = new List<double>();     // largest inter-byte gap within one frame (USB/FTDI chunking)
        var frameDur = new List<double>();

        ProtocolFrame? pendingReq = null;
        ProtocolFrame? prevReq = null;
        foreach (var f in frames)
        {
            frameDur.Add(f.TEnd - f.TStart);
            intraGap.Add(f.MaxByteGap);
            if (f.Kind == FrameKind.Request)
            {
                if (prevReq != null) reqPeriod.Add(f.TStart - prevReq.TStart);
                prevReq = f;
                pendingReq = f;
            }
            else if (f.Kind == FrameKind.Response && pendingReq != null)
            {
                reqResp.Add(f.TStart - pendingReq.TEnd);
                pendingReq = null;
            }
        }

        Console.WriteLine($"  frames: {frames.Count}  (requests={frames.Count(x => x.Kind == FrameKind.Request)}, " +
                          $"responses={frames.Count(x => x.Kind == FrameKind.Response)}, " +
                          $"stream={frames.Count(x => x.Kind == FrameKind.Stream)})");
        Console.WriteLine(Stats("device turnaround (req->resp)", reqResp));
        Console.WriteLine(Stats("host request period", reqPeriod));
        Console.WriteLine(Stats("frame duration", frameDur));
        Console.WriteLine(Stats("max intra-frame byte gap", intraGap));

        // A clean UART transmission has ~0 gap between characters. Any consistent, quantised gap
        // is the sender pausing mid-frame - typically the USB/serial write path (scheduling,
        // latency timer) on the host, not the line itself.
        double worst = 0; foreach (var g in intraGap) if (g > worst) worst = g;
        if (worst > 2 * charTime)
            Console.WriteLine(string.Format(Inv,
                "  NOTE: worst intra-frame gap {0:F3} ms is {1:F1} character times - the sender is " +
                "pausing mid-frame (USB/FTDI chunking), not transmitting continuously.",
                worst * 1000, worst / charTime));

        Console.WriteLine("\n  per-frame detail (first 30):");
        double prevEnd = double.NaN;
        for (int i = 0; i < Math.Min(frames.Count, 30); i++)
        {
            var f = frames[i];
            string sinceStr = double.IsNaN(prevEnd) ? "     -" :
                string.Format(Inv, "{0:F3}", (f.TStart - prevEnd) * 1000);
            Console.WriteLine(string.Format(Inv,
                "    t={0,12:F9}s {1,-8} dur={2,7:F3}ms idle_before={3,8}ms maxgap={4,6:F3}ms  {5}",
                f.TStart, Lower(f.Kind), (f.TEnd - f.TStart) * 1000, sinceStr,
                f.MaxByteGap * 1000, f.Summary));
            prevEnd = f.TEnd;
        }
    }

    private static void DumpDiagnosticConfig(string label)
    {
        const int SR_CONF_TEST = 30054;
        const int SR_CONF_OPERATION_MODE = 30065;
        const int SR_CONF_CHANNEL_MODE = 30067;
        const int SR_CONF_VTH = 30072;
        const int SR_CONF_DEVICE_MODE = 30004;

        Console.WriteLine($"--- diagnostic config read-back: {label} ---");
        void ReadBool(string name, int key)
        {
            if (Native.ds_get_actived_device_config(IntPtr.Zero, IntPtr.Zero, key, out IntPtr v) == 0 && v != IntPtr.Zero)
                Console.WriteLine($"  {name} = {Native.g_variant_get_boolean(v)}");
            else
                Console.WriteLine($"  {name} = <read failed>");
        }
        void ReadInt16(string name, int key)
        {
            if (Native.ds_get_actived_device_config(IntPtr.Zero, IntPtr.Zero, key, out IntPtr v) == 0 && v != IntPtr.Zero)
                Console.WriteLine($"  {name} = {Native.g_variant_get_int16(v)}");
            else
                Console.WriteLine($"  {name} = <read failed>");
        }
        void ReadDouble(string name, int key)
        {
            if (Native.ds_get_actived_device_config(IntPtr.Zero, IntPtr.Zero, key, out IntPtr v) == 0 && v != IntPtr.Zero)
                Console.WriteLine($"  {name} = {Native.g_variant_get_double(v)}");
            else
                Console.WriteLine($"  {name} = <read failed>");
        }

        ReadInt16("DEVICE_MODE", SR_CONF_DEVICE_MODE);
        ReadInt16("OPERATION_MODE", SR_CONF_OPERATION_MODE);
        ReadInt16("CHANNEL_MODE", SR_CONF_CHANNEL_MODE);
        ReadBool("TEST (true = in some test mode)", SR_CONF_TEST);
        ReadDouble("VTH", SR_CONF_VTH);
        Console.WriteLine("---");
    }

    // Root cause of the long-standing "every channel reports an identical signal" bug
    // (investigated 2026-08-28; capture data contained only the two words 0x0000 and 0xFFFF,
    // i.e. all 16 channels moving in perfect lockstep - impossible for real, independent pins).
    //
    // dsl_dev_open() in dsl.c only uploads the FPGA bitstream when the hardware status register
    // reports FPGA_DONE == 0:
    //
    //     *fpga_done = (hw_info & bmFPGA_DONE) != 0;
    //     if (sdi->status == SR_ST_ACTIVE) { if (!(*fpga_done)) { ... dsl_fpga_config(...) ... } }
    //
    // The DSLogic loads its FPGA from onboard flash at power-up, so in normal use FPGA_DONE is
    // already 1 and the upload is ALWAYS skipped. That matters because dsl_fpga_config() ends with
    // a step the skip path therefore never performs:
    //
    //     // step10: recover GPIF to be wordwide
    //     wr_cmd.header.dest = DSL_CTL_WORDWIDE;  wr_cmd.data[0] = bmWR_WORDWIDE;
    //
    // Without that, the GPIF bus stays narrow and every channel bit carries the same value -
    // exactly the observed symptom. The one historical log that produced good data is also the
    // only one containing "Configure FPGA using ... / FPGA configure done: 465304 bytes".
    //
    // Sending DSL_CTL_WORDWIDE by hand afterwards is NOT sufficient (verified - data unchanged);
    // the GPIF width has to be established as part of the configuration sequence. So instead we
    // clear FPGA_DONE up front by pulsing PROG_B low->high, which makes dsl_dev_open() take the
    // full upload path (including step10) on every run.
    //
    // Failure here is deliberately non-fatal: on a device whose FPGA cannot be reconfigured the
    // capture should still be attempted (and will simply exhibit the old behaviour) rather than
    // the tool refusing to run at all.
    private static void ForceFpgaReconfigure()
    {
        IntPtr ctx = IntPtr.Zero, dev = IntPtr.Zero;
        try
        {
            if (UsbNative.libusb_init(out ctx) < 0)
            {
                Console.WriteLine("ForceFpgaReconfigure: libusb_init failed, skipping.");
                return;
            }
            dev = UsbNative.libusb_open_device_with_vid_pid(ctx, UsbNative.DSLOGIC_VID, UsbNative.DSLOGIC_PID);
            if (dev == IntPtr.Zero)
            {
                Console.WriteLine("ForceFpgaReconfigure: DSLogic not found on libusb, skipping.");
                return;
            }
            UsbNative.libusb_claim_interface(dev, 0);

            byte before = ReadHwStatus(dev);
            Console.WriteLine($"ForceFpgaReconfigure: hw_status=0x{before:x2} FPGA_DONE={((before & UsbNative.bmFPGA_DONE) != 0 ? 1 : 0)}");

            // Restore the GPIF bus to its narrow width BEFORE touching PROG_B.
            //
            // The bitstream is downloaded to the FPGA over the same GPIF bus that later carries
            // sample data, and dsl_fpga_config() only sets WORDWIDE at the very end of a
            // *successful* configuration ("step10: recover GPIF to be wordwide") - so the download
            // itself expects the narrow bus. If a previous run left WORDWIDE set, the bitstream is
            // clocked out at the wrong width, arrives corrupted, and the FPGA fails its CRC check:
            // it then holds INIT_B low and never asserts FPGA_DONE, so the library's unbounded
            // "wait for FPGA_DONE" loop hangs forever with no error.
            //
            // Verified the hard way: a device left in that state could not be recovered by
            // libusb_reset_device, a warm reboot, or Disable/Enable-PnpDevice (none of which drop
            // VBUS), and the root hub has no usable per-port power switching. Clearing WORDWIDE
            // first recovered it immediately, no power cycle required.
            CtlWrite(dev, UsbNative.DSL_CTL_WORDWIDE, 0x00);

            // Mirrors dsl_fpga_config() steps 0-2: PROG_B low, LEDs off, PROG_B high.
            CtlWrite(dev, UsbNative.DSL_CTL_PROG_B, unchecked((byte)~UsbNative.bmWR_PROG_B));
            CtlWrite(dev, UsbNative.DSL_CTL_LED, unchecked((byte)(~UsbNative.bmLED_GREEN & ~UsbNative.bmLED_RED)));
            CtlWrite(dev, UsbNative.DSL_CTL_PROG_B, UsbNative.bmWR_PROG_B);

            byte after = ReadHwStatus(dev);
            bool cleared = (after & UsbNative.bmFPGA_DONE) == 0;
            Console.WriteLine($"ForceFpgaReconfigure: hw_status=0x{after:x2} FPGA_DONE={(cleared ? 0 : 1)}" +
                              (cleared
                                  ? " -> cleared, library will upload the bitstream (incl. WORDWIDE step)."
                                  : " -> NOT cleared; upload will be skipped and capture may show the lockstep-channel bug."));
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"ForceFpgaReconfigure: libusb-1.0.dll unavailable ({ex.Message}), skipping.");
        }
        finally
        {
            if (dev != IntPtr.Zero)
            {
                UsbNative.libusb_release_interface(dev, 0);
                UsbNative.libusb_close(dev);
            }
            if (ctx != IntPtr.Zero) UsbNative.libusb_exit(ctx);
        }
    }

    private static void CtlWrite(IntPtr dev, byte dest, byte value)
    {
        var buf = UsbNative.BuildCtlWrite(dest, value);
        UsbNative.libusb_control_transfer(dev,
            UsbNative.LIBUSB_REQUEST_TYPE_VENDOR | UsbNative.LIBUSB_ENDPOINT_OUT,
            UsbNative.CMD_CTL_WR, 0, 0, buf, (ushort)buf.Length, 3000);
    }

    private static byte ReadHwStatus(IntPtr dev)
    {
        // A read is a CMD_CTL_RD_PRE carrying the ctl_header, followed by CMD_CTL_RD for the data.
        var hdr = new byte[] { UsbNative.DSL_CTL_HW_STATUS, 0, 0, 1 };
        UsbNative.libusb_control_transfer(dev,
            UsbNative.LIBUSB_REQUEST_TYPE_VENDOR | UsbNative.LIBUSB_ENDPOINT_OUT,
            UsbNative.CMD_CTL_RD_PRE, 0, 0, hdr, (ushort)hdr.Length, 3000);
        var data = new byte[1];
        UsbNative.libusb_control_transfer(dev,
            UsbNative.LIBUSB_REQUEST_TYPE_VENDOR | UsbNative.LIBUSB_ENDPOINT_IN,
            UsbNative.CMD_CTL_RD, 0, 0, data, 1, 3000);
        return data[0];
    }

    // Value-change dump, so a capture opens in PulseView, GTKWave or DSView itself instead of
    // only in this tool's own text output.
    //
    // Ported from the dsl-capture CLI in wickerwaka's DSView fork
    // (github.com/wickerwaka/DSView, branch headless-cli, cli/output_vcd.c), GPLv3 like the rest
    // of this tree. Same idea, rewritten against this tool's already-demuxed per-channel buffers.
    //
    // Only level CHANGES are emitted - a VCD listing every sample of a 100 MHz capture would be
    // enormous and no slower to load for it.
    private static void WriteVcd(string path, byte[][] perCh, int[] channels, ulong sampleRateHz, long sampleCount)
    {
        // One tick = one sample period, in picoseconds. Integer division truncates, so a rate that
        // is not a divisor of 1e12 (e.g. 3 MHz) drifts; at the rates this device offers the error
        // is under a part per million over a whole capture, and the alternative - a fractional
        // timescale - is not expressible in VCD.
        ulong tickPs = sampleRateHz > 0 ? 1_000_000_000_000UL / sampleRateHz : 1UL;

        using var w = new StreamWriter(path);
        w.NewLine = "\n";

        w.WriteLine($"$comment generated by DslCli, {sampleRateHz} Hz, {sampleCount} samples $end");
        w.WriteLine($"$timescale {tickPs} ps $end");
        w.WriteLine("$scope module dslogic $end");

        // VCD identifier codes are printable ASCII from '!' (33) up; 16 channels never exhausts it.
        var id = new char[channels.Length];
        for (int i = 0; i < channels.Length; i++)
        {
            id[i] = (char)('!' + i);
            w.WriteLine($"$var wire 1 {id[i]} ch{channels[i]} $end");
        }

        w.WriteLine("$upscope $end");
        w.WriteLine("$enddefinitions $end");

        if (sampleCount <= 0) return;

        // Initial state at t=0, then one block per sample that changed anything.
        var prev = new bool[channels.Length];
        w.WriteLine("#0");
        for (int i = 0; i < channels.Length; i++)
        {
            prev[i] = GetBit(perCh[i], 0);
            w.WriteLine($"{(prev[i] ? '1' : '0')}{id[i]}");
        }

        for (long sample = 1; sample < sampleCount; sample++)
        {
            bool wroteStamp = false;
            for (int i = 0; i < channels.Length; i++)
            {
                bool cur = GetBit(perCh[i], sample);
                if (cur == prev[i]) continue;
                if (!wroteStamp)
                {
                    w.WriteLine($"#{(ulong)sample * tickPs}");
                    wroteStamp = true;
                }
                w.WriteLine($"{(cur ? '1' : '0')}{id[i]}");
                prev[i] = cur;
            }
        }
    }

    // pcap export of decoded frames, so a bus capture can be read in Wireshark with the protocol's
    // own dissector rather than as a wall of hex.
    //
    // NANOSECOND pcap (magic 0xa1b23c4d), not the classic microsecond one. The timestamps here come
    // from the sample index, so they are good to one sample period - 10 ns at 100 MHz - and the
    // classic format would throw away more than half that precision. Reading the timing off the
    // capture is the entire reason this tool exists, so it is worth the slightly less universal
    // magic number; Wireshark has understood it since 1.8.
    //
    // The host stays protocol-agnostic: it writes whatever bytes the decoder handed back, stamped
    // with whatever link type the decoder asked for. It never needs to know what a frame means.
    // Incremental pcap sink, for feeding Wireshark or tshark live instead of after the fact.
    //
    // Deliberately a NAMED PIPE or file, never stdout: this tool is chatty on stdout (polarity,
    // decode, timing, burst analysis all go to Console.Out), so a pcap on the same stream would be
    // shredded by its own diagnostics. A pipe keeps the two apart and works with both front ends:
    //
    //     DslCli cap.raw 25000000 2 0,2 115200 0,2 2.0 --pcap-stream \\.\pipe\decoded
    //     tshark -i \\.\pipe\decoded -l          (or Wireshark -i \\.\pipe\decoded -k)
    //
    // Start the reader first: opening the server end blocks until a client connects, which is the
    // behaviour you want - it means no frames are decoded into a pipe nobody is reading.
    //
    // The global header is written exactly once, on the first frame, because the link type comes
    // from the decoder and is not known before then. Each record is flushed as it is written; an
    // unflushed pcap looks identical to a silent bus, which on this bus is the one confusion worth
    // spending code to avoid.
    private sealed class PcapSink : IDisposable
    {
        private readonly Stream _s;
        private readonly BinaryWriter _w;
        private readonly bool _isPipe;
        private bool _headerWritten;
        public int FramesWritten { get; private set; }

        private PcapSink(Stream s, bool isPipe)
        {
            _s = s;
            _w = new BinaryWriter(s);
            _isPipe = isPipe;
        }

        // Open the sink BEFORE capturing, not lazily at first export: tshark and Wireshark are
        // pipe *clients* and fail immediately if the pipe does not exist yet ("error on pipe open:
        // The system cannot find the file specified. (2)") - they do not retry. The server end must
        // already be listening. The global header is still written lazily on the first frame, since
        // the link type comes from the decoder; only the pipe itself is early.
        public static PcapSink? Open(string target, double waitSeconds)
        {
            // \\.\pipe\name -> we are the server. Wireshark/tshark connects as the client.
            const string prefix = @"\\.\pipe\";
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string name = target.Substring(prefix.Length);
                var srv = new System.IO.Pipes.NamedPipeServerStream(
                    name, System.IO.Pipes.PipeDirection.Out, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                Console.WriteLine($"[stream] listening on {target} - start the reader now:");
                Console.WriteLine($"[stream]   tshark -i {target} -l");
                // Bounded, because an unbounded WaitForConnection() hangs the entire run forever if
                // the reader never starts or dies at startup, with the process looking busy.
                var conn = srv.WaitForConnectionAsync();
                if (!conn.Wait(TimeSpan.FromSeconds(waitSeconds)))
                {
                    Console.WriteLine($"[stream] no reader attached within {waitSeconds:F0}s - continuing without streaming.");
                    try { srv.Dispose(); } catch { }
                    return null;
                }
                Console.WriteLine("[stream] reader attached.");
                return new PcapSink(srv, isPipe: true);
            }
            return new PcapSink(new FileStream(target, FileMode.Create, FileAccess.Write), isPipe: false);
        }

        public void Write(IReadOnlyList<ProtocolFrame> frames, int linkType, DateTime captureStartUtc)
        {
            if (!_headerWritten)
            {
                WritePcapGlobalHeader(_w, linkType);
                _headerWritten = true;
            }
            foreach (var f in frames)
            {
                if (WritePcapRecord(_w, f, captureStartUtc)) FramesWritten++;
            }
            _w.Flush();
            _s.Flush();
        }

        public void Dispose()
        {
            try { _w.Flush(); _s.Flush(); } catch { }
            // A broken pipe here just means the reader quit first, which is normal on Ctrl-C.
            try { _w.Dispose(); } catch { }
            if (_isPipe) { try { _s.Dispose(); } catch { } }
        }
    }

    private const uint PcapMagicNanosecond = 0xa1b23c4d;

    private static void WritePcapGlobalHeader(BinaryWriter w, int linkType)
    {
        const uint SnapLen = 262144;
        w.Write(PcapMagicNanosecond);
        w.Write((ushort)2);          // version major
        w.Write((ushort)4);          // version minor
        w.Write(0);                  // thiszone: timestamps are UTC
        w.Write(0u);                 // sigfigs, always 0 in practice
        w.Write(SnapLen);
        w.Write((uint)linkType);
    }

    // Returns false for a frame the decoder gave no bytes for - nothing to put on the wire.
    private static bool WritePcapRecord(BinaryWriter w, ProtocolFrame f, DateTime captureStartUtc)
    {
        if (f.Bytes.Count == 0) return false;

        long baseTicks = captureStartUtc.Ticks - DateTime.UnixEpoch.Ticks;
        long baseSeconds = baseTicks / TimeSpan.TicksPerSecond;
        long baseNanos = (baseTicks % TimeSpan.TicksPerSecond) * 100;   // 1 tick = 100 ns

        // Split before converting to avoid losing sample-level precision in a double that
        // already carries the whole wall-clock offset.
        long secs = (long)Math.Floor(f.TStart);
        long nanos = (long)Math.Round((f.TStart - secs) * 1_000_000_000.0);
        if (nanos >= 1_000_000_000) { nanos -= 1_000_000_000; secs++; }

        long ts = baseSeconds + secs;
        long tn = baseNanos + nanos;
        if (tn >= 1_000_000_000) { tn -= 1_000_000_000; ts++; }

        var bytes = f.Bytes as byte[] ?? System.Linq.Enumerable.ToArray(f.Bytes);
        w.Write((uint)ts);
        w.Write((uint)tn);
        w.Write((uint)bytes.Length);   // captured length
        w.Write((uint)bytes.Length);   // original length - never truncated
        w.Write(bytes);
        return true;
    }

    private static void WritePcap(string path, IReadOnlyList<ProtocolFrame> frames, int linkType, DateTime captureStartUtc)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        const uint MagicNanosecond = 0xa1b23c4d;
        const uint SnapLen = 262144;

        w.Write(MagicNanosecond);
        w.Write((ushort)2);          // version major
        w.Write((ushort)4);          // version minor
        w.Write(0);                  // thiszone: timestamps are UTC
        w.Write(0u);                 // sigfigs, always 0 in practice
        w.Write(SnapLen);
        w.Write((uint)linkType);

        // Frame times are seconds from the start of the capture; anchor them to the wall clock so
        // the file reads sensibly next to other captures, while relative times stay exact.
        long baseTicks = captureStartUtc.Ticks - DateTime.UnixEpoch.Ticks;
        long baseSeconds = baseTicks / TimeSpan.TicksPerSecond;
        long baseNanos = (baseTicks % TimeSpan.TicksPerSecond) * 100;   // 1 tick = 100 ns

        foreach (var f in frames)
        {
            if (f.Bytes.Count == 0) continue;

            // Split before converting to avoid losing sample-level precision in a double that
            // already carries the whole wall-clock offset.
            long secs = (long)Math.Floor(f.TStart);
            long nanos = (long)Math.Round((f.TStart - secs) * 1_000_000_000.0);
            if (nanos >= 1_000_000_000) { nanos -= 1_000_000_000; secs++; }

            long ts = baseSeconds + secs;
            long tn = baseNanos + nanos;
            if (tn >= 1_000_000_000) { tn -= 1_000_000_000; ts++; }

            var bytes = f.Bytes as byte[] ?? System.Linq.Enumerable.ToArray(f.Bytes);
            w.Write((uint)ts);
            w.Write((uint)tn);
            w.Write((uint)bytes.Length);   // captured length
            w.Write((uint)bytes.Length);   // original length - never truncated
            w.Write(bytes);
        }
    }

    // What --trigger-pos actually buys you, which is not what you asked for.
    //
    // dsl_fpga_arm (dsl.c) turns trigger_pos into a pre-trigger sample count and then clamps it:
    //
    //     n = max(pos/100 * limit_samples, DSLOGIC_ATOMIC_SAMPLES)
    //     n = min(n, channel_depth * (stream ? 10 : DS_MAX_TRIG_PERCENT) / 100)
    //
    // This tool forces STREAM mode, so the ceiling is 10% of the per-channel depth - not 90%. Ask
    // for 50% of a long capture and you quietly get far less, with nothing said. Since the whole
    // point of a trigger position is knowing where in the buffer your event sits, that silence is
    // worse than the limit itself, so the numbers are computed here the same way and printed.
    private static void ReportEffectiveTriggerPosition(int posPercent, ulong limitSamples, int enabledChannelCount)
    {
        const uint AtomicSamples = 1u << 6;      // DSLOGIC_ATOMIC_BITS
        const ulong SamplesAlign = 1023UL;
        const int StreamMaxPercent = 10;         // the devc->stream branch of the clamp above

        ulong hwDepth = 0;
        if (Native.ds_get_actived_device_config(IntPtr.Zero, IntPtr.Zero, 30075 /* SR_CONF_HW_DEPTH */,
                out IntPtr v) == 0 && v != IntPtr.Zero)
        {
            hwDepth = Native.g_variant_get_uint64(v);
        }

        ulong requested = (ulong)(posPercent / 100.0 * limitSamples);
        ulong withFloor = Math.Max(requested, AtomicSamples);

        if (hwDepth == 0)
        {
            Console.WriteLine(string.Format(Inv,
                "  trigger position: {0}% = {1} pre-trigger sample(s) requested. Could not read " +
                "HW_DEPTH, so the STREAM-mode ceiling could not be checked.", posPercent, withFloor));
            return;
        }

        ulong channelDepth = (hwDepth / (ulong)Math.Max(1, enabledChannelCount)) & ~SamplesAlign;
        ulong ceiling = channelDepth * StreamMaxPercent / 100;
        ulong effective = Math.Min(withFloor, ceiling);

        Console.WriteLine(string.Format(Inv,
            "  trigger position: {0}% of {1} samples = {2} requested, {3} effective " +
            "(STREAM ceiling {4} = 10% of {5} per-channel depth).",
            posPercent, limitSamples, withFloor, effective, ceiling, channelDepth));

        if (effective < withFloor)
        {
            Console.WriteLine(string.Format(Inv,
                "  NOTE: the pre-trigger window is capped to {0} sample(s) ({1:F2}% of this capture), " +
                "well short of the {2}% asked for. That is the hardware's STREAM-mode limit, not a " +
                "setting - shorten the capture to get a larger fraction before the trigger.",
                effective, 100.0 * effective / Math.Max(1UL, limitSamples), posPercent));
        }
    }

    /// <summary>One parsed trigger clause: which channel, and the condition character the FPGA wants.</summary>
    private readonly record struct TriggerClause(int Channel, char Condition);

    // Trigger spec parsing, same surface as dsl-capture's --trigger (see WriteVcd's credit note):
    // comma-separated chK=COND, where COND is R(ising) F(alling) 1 0 E(ither) X(don't care).
    // The "ch" prefix is optional. Channels not mentioned stay don't-care.
    private static bool TryParseTrigger(string spec, out List<TriggerClause> clauses, out string? error)
    {
        clauses = new List<TriggerClause>();
        error = null;

        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var clause = raw.Trim();
            int eq = clause.IndexOf('=');
            if (eq <= 0 || eq == clause.Length - 1)
            {
                error = $"'{clause}' is not KEY=COND";
                return false;
            }

            var key = clause[..eq].Trim();
            var val = clause[(eq + 1)..].Trim().ToUpperInvariant();

            if (key.StartsWith("ch", StringComparison.OrdinalIgnoreCase)) key = key[2..];
            if (!int.TryParse(key, System.Globalization.NumberStyles.Integer, Inv, out int ch) || ch < 0 || ch > 15)
            {
                error = $"'{clause}' does not name a channel 0-15";
                return false;
            }

            if (val.Length != 1 || "RF10EX".IndexOf(val[0]) < 0)
            {
                error = $"'{clause}' has an unknown condition (want one of R F 1 0 E X)";
                return false;
            }

            clauses.Add(new TriggerClause(ch, val[0]));
        }

        return true;
    }

    /// <summary>Programs the simple trigger. Returns true if any clause is an actual condition.</summary>
    private static bool ApplyTrigger(List<TriggerClause> clauses, int triggerPosPercent)
    {
        int pos = Math.Clamp(triggerPosPercent, 0, 100);
        Native.ds_trigger_set_mode(Native.SIMPLE_TRIGGER);
        Native.ds_trigger_set_pos((ushort)pos);

        bool armed = false;
        foreach (var c in clauses)
        {
            // trigger1 is the second stage of a serial trigger, unused for SIMPLE_TRIGGER.
            Native.ds_trigger_probe_set((ushort)c.Channel, (byte)c.Condition, (byte)'X');
            if (c.Condition != 'X') armed = true;
        }
        return armed;
    }

    private static void Main(string[] args)
    {
        var ic = System.Globalization.CultureInfo.InvariantCulture;

        // Named flags may appear anywhere; everything else keeps the original positional meaning,
        // so existing invocations and the MCP server are unaffected.
        string? triggerSpec = null;
        int triggerPos = 0;
        bool wantVcd = false;
        bool wantPcap = false;
        string? pcapStreamTarget = null;
        int libLogLevel = 3;   // XLOG_LEVEL_INFO
        var framing = ParityMode.None;   // 8-N-1 unless told otherwise
        bool triggerPosGiven = false;
        double armSeconds = 0;
        bool triggerArmed = false;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--trigger" when i + 1 < args.Length:
                    triggerSpec = args[++i];
                    break;
                case "--trigger-pos" when i + 1 < args.Length:
                    // Refused rather than clamped: silently turning "--trigger-pos 500" into 100 is
                    // the kind of thing you only notice by not finding your pre-trigger data.
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.Integer, ic, out triggerPos)
                        || triggerPos < 0 || triggerPos > 100)
                    {
                        Console.WriteLine($"--trigger-pos wants a percent 0-100, got '{args[i]}'");
                        return;
                    }
                    triggerPosGiven = true;
                    break;
                case "--vcd":
                    wantVcd = true;
                    break;
                case "--pcap":
                    wantPcap = true;
                    break;
                case "--framing" when i + 1 < args.Length:
                    switch (args[++i].ToUpperInvariant())
                    {
                        case "8N1": framing = ParityMode.None; break;
                        case "8E1": framing = ParityMode.Even; break;
                        case "8O1": framing = ParityMode.Odd;  break;
                        default:
                            Console.Error.WriteLine("--framing takes 8N1, 8E1 or 8O1.");
                            return;
                    }
                    break;
                case "--log-level" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out libLogLevel) || libLogLevel < 0 || libLogLevel > 5)
                    {
                        Console.Error.WriteLine("--log-level takes 0..5 (0 none, 1 err, 2 warn, 3 info, 4 dbg, 5 detail).");
                        return;
                    }
                    break;
                case "--pcap-stream" when i + 1 < args.Length:
                    pcapStreamTarget = args[++i];
                    wantPcap = true;          // the stream is a pcap sink; same export path feeds it
                    break;
                case "--arm-timeout" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, ic, out armSeconds)
                        || armSeconds < 0)
                    {
                        Console.WriteLine($"--arm-timeout wants seconds, got '{args[i]}'");
                        return;
                    }
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }
        args = positional.ToArray();

        string outPath = args.Length > 0 ? args[0] : "capture.raw";
        _outBaseName = outPath.EndsWith(".raw") ? outPath[..^4] : outPath;
        ulong sampleRateHz = args.Length > 1 ? ulong.Parse(args[1], ic) : 24_000_000UL;
        double durationSeconds = args.Length > 2 ? double.Parse(args[2], ic) : 3.0;
        int[] enabledChannels = args.Length > 3
            ? Array.ConvertAll(args[3].Split(','), int.Parse)
            : new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        // args[4] = UART baud to decode (0/omitted = capture only)
        // args[5] = comma-separated channels to decode (default: every captured channel)
        int decodeBaud = args.Length > 4 ? int.Parse(args[4], ic) : 0;
        int[] decodeChannels = args.Length > 5
            ? Array.ConvertAll(args[5].Split(','), int.Parse)
            : enabledChannels;
        // args[6] = comparator threshold in volts (<=0 or omitted keeps the session default)
        double vthVolts = args.Length > 6 ? double.Parse(args[6], ic) : 0;

        if (args.Length == 0 || (args.Length == 1 && (args[0] == "-h" || args[0] == "--help")))
        {
            Console.WriteLine("usage: DslCli <out.raw> [sampleRateHz] [seconds] [channels] [decodeBaud] [decodeChannels] [vthVolts]");
            Console.WriteLine("             [--trigger SPEC] [--trigger-pos PCT] [--vcd]");
            Console.WriteLine("  e.g. DslCli capture.raw 100000000 1.0 0,3 115200 0,3");
            Console.WriteLine("       DslCli capture.raw 100000000 1.0 0,3 0 0 0 --trigger ch0=F --trigger-pos 20 --vcd");
            Console.WriteLine();
            Console.WriteLine("  --trigger SPEC      comma-separated chK=COND; COND is R rising, F falling,");
            Console.WriteLine("                      1 high, 0 low, E either edge, X don't care.");
            Console.WriteLine("                      Unmentioned channels are don't-care. Without this the");
            Console.WriteLine("                      capture is free-running, as before.");
            Console.WriteLine("  --trigger-pos PCT   pre-trigger capture position, percent (default 0).");
            Console.WriteLine("  --vcd               also write <base>.vcd, openable in PulseView/GTKWave/DSView.");
            Console.WriteLine("  --pcap              also write <base>.chN.pcap of the decoded frames, for Wireshark.");
        Console.WriteLine("  --framing F         8N1 (default), 8E1 or 8O1. With parity the stop bit is at 10.5,");
        Console.WriteLine("                      not 9.5 - wrong setting makes the framing check read parity.");
        Console.WriteLine("  --log-level N       libsigrok verbosity 0..5, default 3 (info). 5 logs every USB");
        Console.WriteLine("                      transfer and buries the decode output.");
        Console.WriteLine("  --pcap-stream P     stream decoded frames as pcap to a file or named pipe");
        Console.WriteLine("                      (backslash-backslash-dot-backslash-pipe-backslash-NAME). Start the reader first;");
        Console.WriteLine("                      until it attaches. NOT stdout - this tool is chatty there.");
            Console.WriteLine("  --arm-timeout SEC   with --trigger, how long to sit armed waiting for the trigger");
            Console.WriteLine("                      to fire. Independent of [seconds], which is the capture length,");
            Console.WriteLine("                      so a long wait costs no extra buffer.");
            Console.WriteLine("                      Needs a protocol decoder plugin that supports pcap export.");
            Console.WriteLine("Writes the interleaved capture, a demuxed <base>.chN.raw per channel,");
            Console.WriteLine("and - when decodeBaud is given - <base>.chN.uart.txt plus, if a protocol decoder");
            Console.WriteLine("plugin is installed, its decoded frames and their timing.");
            if (args.Length == 1) return;
        }

        _channelCount = enabledChannels.Length;

        // Firmware/FPGA resource dir - the official DSView install already has
        // DSLogicU3Pro16.bin (the FPGA bitstream this model needs). No matching
        // per-model ".fw" bootloader file exists there (only shared ones for older
        // models), but since this exact device has already been used continuously
        // with the official DSView today, its EZ-USB firmware is presumably already
        // loaded and the driver's version check should skip re-uploading it.
        string fwDir = @"C:\Program Files\DSView\res";

        Native.ds_set_firmware_resource_dir(fwDir);
        Native.ds_set_user_data_dir(Path.GetTempPath());

        Native.EventCallback evCb = OnEvent;
        Native.DatafeedCallback dfCb = OnDatafeed;
        Native.ds_set_event_callback(evCb);
        Native.ds_set_datafeed_callback(dfCb);

        // 3 = XLOG_LEVEL_INFO. Was hardcoded to 5 (DETAIL), which logs the USB completion callback
        // on every transfer - "receive_transfer(): status 0; timeout 0; received 63488 bytes" -
        // hundreds of lines a second at 25 MHz, burying the decode output and the timing report this
        // tool exists to produce. Everything load-bearing for diagnosis is sr_info: "Configure FPGA
        // using", "FPGA configure done", "config_set: setting channel mode to 16", "Set device ext
        // samplerate". Level 4 adds sr_dbg, 5 adds the per-transfer spew - use --log-level when
        // debugging the vendor library itself rather than the bus.
        Native.ds_log_level(libLogLevel);

        int rc = Native.ds_lib_init();
        Console.WriteLine($"ds_lib_init -> {rc}");

        rc = Native.ds_trigger_reset();
        Console.WriteLine($"ds_trigger_reset -> {rc}");

        // Confirmed 2026-08-28 by rebuilding DSView.exe from source with the existing
        // ARM_SETTING_DUMP instrumentation (in dsl.c's dsl_fpga_arm) and diffing a real working
        // DSView capture's arm-command bytes against this tool's own dump byte-for-byte: the
        // `setting.mode` word differs in bit 0 (TRIG_EN_BIT) - DSView's real capture has it SET
        // (0x1001, "triggers effective"), while this tool's ds_trigger_reset() leaves it CLEAR
        // (0x1000, "instant capture" - trigger circuit bypassed entirely). That bit was the actual
        // root cause of the fake ~1.5MHz internal-reference-signal bug: "instant capture" mode
        // routes the FPGA through a different capture path than the normal triggered pipeline that
        // reads real external pins. DSView always calls ds_trigger_set_en(true) itself (see
        // triggerdock.cpp/logicsignal.cpp) whenever its trigger UI initializes a device - it's not
        // part of ds_set_actived_device_config or the session JSON at all, which is why the config
        // diff done earlier found the two tools' config_set traces identical.
        rc = Native.ds_trigger_set_en(1);
        Console.WriteLine($"ds_trigger_set_en(1) -> {rc}");

        // A trigger is programmed on top of that, not instead of it: the enable above is required
        // for the real capture path regardless, and with no clauses every probe stays don't-care,
        // which fires immediately - i.e. free-running, exactly as this tool behaved before.
        if (triggerSpec is null && triggerPosGiven)
        {
            // Without a condition the capture starts immediately, so there is no trigger point for
            // a position to be relative to. Saying so beats accepting the flag and ignoring it.
            Console.WriteLine("--trigger-pos has no effect without --trigger: with no condition the " +
                              "capture is free-running and starts at sample 0.");
        }

        if (triggerSpec is not null)
        {
            if (!TryParseTrigger(triggerSpec, out var clauses, out var terr))
            {
                Console.WriteLine($"--trigger: {terr}");
                return;
            }
            triggerArmed = ApplyTrigger(clauses, triggerPos);
            Console.WriteLine($"trigger: {(triggerArmed ? "armed" : "no active condition (free-running)")}" +
                              $" [{string.Join(", ", clauses.ConvertAll(c => $"ch{c.Channel}={c.Condition}"))}]" +
                              $" pos={Math.Clamp(triggerPos, 0, 100)}%");
        }


        // Must happen BEFORE ds_get_device_list/ds_active_device_by_index, because the FPGA upload
        // decision is made inside dsl_dev_open() during activation.
        ForceFpgaReconfigure();

        rc = Native.ds_get_device_list(out IntPtr listPtr, out int count);
        Console.WriteLine($"ds_get_device_list -> {rc}, count={count}");
        if (count == 0)
        {
            Console.WriteLine("No devices found.");
            return;
        }

        int structSize = Marshal.SizeOf<DsDeviceBaseInfo>();
        int selected = -1;
        for (int i = 0; i < count; i++)
        {
            var info = Marshal.PtrToStructure<DsDeviceBaseInfo>(listPtr + i * structSize);
            Console.WriteLine($"  [{i}] handle={info.handle} name={info.name} id={info.unique_id}");
            if (selected < 0 && info.name.Contains("DSLogic", StringComparison.OrdinalIgnoreCase))
            {
                selected = i;
            }
        }
        if (selected < 0) selected = 0;

        rc = Native.ds_active_device_by_index(selected);
        Console.WriteLine($"ds_active_device_by_index({selected}) -> {rc}");
        if (rc != 0)
        {
            Console.WriteLine($"Failed to activate device (ds_get_last_error={Native.ds_get_last_error()}).");
            return;
        }

        DumpDiagnosticConfig("after activation, before any set_config");

        // DSView's own MainWindow::load_config_from_json (mainwindow.cpp:833) is the
        // reference implementation for "how do you actually get real sampling
        // working" - it requires DEVICE_MODE=LOGIC set FIRST (switch_work_mode),
        // then iterates the *entire* SR_CONF_DEVICE_SESSIONS key list and applies
        // every value found in the saved session JSON (channel mode, threshold,
        // samplerate, etc.) - not just a hand-picked few. Hand-picking DEVICE_MODE/
        // OPERATION_MODE/samplerate/threshold alone left every channel reading a
        // clean internal ~1.5MHz reference signal (identical on all 16 bits, proven
        // via bit-level analysis) instead of real external pins - most likely
        // because SR_CONF_CHANNEL_MODE (never set) selects between the "real
        // sampling" and some other internal preset. Replicate the generic loader
        // instead of guessing which additional keys matter.
        IntPtr devModeVariant = Native.g_variant_new_int16(DevMode.LOGIC);
        rc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, SrConf.DEVICE_MODE, devModeVariant);
        Console.WriteLine($"set DEVICE_MODE=LOGIC -> {rc}");

        // SR_CONF_OPERATION_MODE's handler (dslogic.c) is gated by
        // "devc->op_mode != nv" - op_mode already defaults to LO_OP_STREAM at scan
        // time, so setting it to STREAM again was a silent no-op that never ran
        // the code forcing devc->test_mode = SR_TEST_NONE. Toggle through BUFFER
        // first so the value genuinely changes, guaranteeing the STREAM branch's
        // body actually executes on the second call.
        const int OPERATION_MODE = 30065;
        const short LO_OP_BUFFER = 0, LO_OP_STREAM = 1;
        rc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, OPERATION_MODE, Native.g_variant_new_int16(LO_OP_BUFFER));
        Console.WriteLine($"set OPERATION_MODE=BUFFER (forcing a real change) -> {rc}");
        rc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, OPERATION_MODE, Native.g_variant_new_int16(LO_OP_STREAM));
        Console.WriteLine($"set OPERATION_MODE=STREAM (now a real transition, forces test_mode=NONE) -> {rc}");

        string sessionFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DreamSourceLab", "DSView", "DSLogic0.ses.dsc");
        Console.WriteLine($"Loading session config from {sessionFile}");
        using var doc = JsonDocument.Parse(File.ReadAllText(sessionFile));
        var root = doc.RootElement;

        rc = Native.ds_get_actived_device_config_list(IntPtr.Zero, SrConf.DEVICE_SESSIONS, out IntPtr listVariant);
        Console.WriteLine($"ds_get_actived_device_config_list(DEVICE_SESSIONS) -> {rc}");
        if (rc == 0 && listVariant != IntPtr.Zero)
        {
            IntPtr arrPtr = Native.g_variant_get_fixed_array(listVariant, out UIntPtr nElements, (UIntPtr)sizeof(int));
            int n = (int)nElements;
            var keys = new int[n];
            Marshal.Copy(arrPtr, keys, 0, n);

            foreach (var key in keys)
            {
                IntPtr infoPtr = Native.ds_get_actived_device_config_info(key);
                if (infoPtr == IntPtr.Zero) continue;
                var info = Marshal.PtrToStructure<SrConfigInfo>(infoPtr);
                string name = Marshal.PtrToStringAnsi(info.name) ?? "";
                if (!root.TryGetProperty(name, out var jval)) continue;

                IntPtr gvar = IntPtr.Zero;
                if (info.datatype == SrT.BOOL)
                    gvar = Native.g_variant_new_boolean(jval.GetInt32() != 0);
                else if (info.datatype == SrT.UINT64)
                    gvar = Native.g_variant_new_uint64(ulong.Parse(jval.GetString() ?? "0"));
                else if (info.datatype == SrT.UINT8)
                    gvar = Native.g_variant_new_byte((byte)(jval.ValueKind == JsonValueKind.String ? uint.Parse(jval.GetString()!) : (uint)jval.GetInt32()));
                else if (info.datatype == SrT.INT16)
                    gvar = Native.g_variant_new_int16((short)jval.GetInt32());
                else if (info.datatype == SrT.FLOAT)
                    gvar = Native.g_variant_new_double(jval.ValueKind == JsonValueKind.String ? double.Parse(jval.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : jval.GetDouble());
                else if (info.datatype == SrT.CHAR)
                    gvar = Native.g_variant_new_string(jval.GetString() ?? "");
                else if (info.datatype == SrT.LIST)
                    gvar = Native.g_variant_new_int16((short)jval.GetInt32());

                if (gvar == IntPtr.Zero)
                {
                    Console.WriteLine($"  skip '{name}' (unhandled datatype {info.datatype})");
                    continue;
                }

                int setRc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, info.key, gvar);
                Console.WriteLine($"  set '{name}' (key={info.key}) = {jval} -> {setRc}");
            }
        }

        // CLI-provided rate/duration override whatever the session file had for those two.
        IntPtr srVariant = Native.g_variant_new_uint64(sampleRateHz);
        rc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, 30000 /* SR_CONF_SAMPLERATE */, srVariant);
        Console.WriteLine($"override SAMPLERATE={sampleRateHz} -> {rc}");

        ulong limitSamples = (ulong)(sampleRateHz * durationSeconds);
        IntPtr limitVariant = Native.g_variant_new_uint64(limitSamples);
        rc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, 50001 /* SR_CONF_LIMIT_SAMPLES */, limitVariant);
        Console.WriteLine($"override LIMIT_SAMPLES={limitSamples} -> {rc}");

        // Comparator threshold. Worth sweeping: on a half-duplex RS485 bus the master and the
        // slave are different transceivers and need not drive the same levels, so a threshold
        // that resolves one side cleanly can clip or skew the other. A mis-set threshold does not
        // look like an error - it shows up as edges landing at the wrong times, i.e. run lengths
        // that are not multiples of the bit period, which is exactly what the master's frames do
        // at the default 1.5 V.
        if (vthVolts > 0)
        {
            IntPtr vthVariant = Native.g_variant_new_double(vthVolts);
            rc = Native.ds_set_actived_device_config(IntPtr.Zero, IntPtr.Zero, 30072 /* SR_CONF_VTH */, vthVariant);
            Console.WriteLine(string.Format(Inv, "override VTH={0:F2} V -> {1}", vthVolts, rc));
        }

        for (int ch = 0; ch < 16; ch++)
        {
            bool enable = Array.IndexOf(enabledChannels, ch) >= 0;
            Native.ds_enable_device_channel_index(ch, enable);
        }
        Console.WriteLine($"Enabled channels: {string.Join(",", enabledChannels)}");

        // Pipe first, capture second - see PcapSink.Open. 60 s is enough for a human to paste the
        // tshark line the message above prints.
        if (pcapStreamTarget is not null)
            _pcapSink = PcapSink.Open(pcapStreamTarget, 60.0);

        DumpDiagnosticConfig("immediately before ds_start_collect");

        _rawOut = new FileStream(outPath, FileMode.Create, FileAccess.Write);

        // Report the real pre-trigger window now that limit_samples and the channel set are final -
        // this is the last moment before the FPGA is armed with them.
        if (triggerArmed)
            ReportEffectiveTriggerPosition(triggerPos, limitSamples, enabledChannels.Length);

        rc = Native.ds_start_collect();
        Console.WriteLine($"ds_start_collect -> {rc}");
        if (rc != 0)
        {
            Console.WriteLine($"Failed to start collection (ds_get_last_error={Native.ds_get_last_error()}).");
            _rawOut.Dispose();
            return;
        }

        // Sitting armed is not the same as capturing, and the two need separate budgets.
        //
        // The device does not report "armed but not yet triggered", so this cannot distinguish
        // waiting from capturing - it can only allow enough total time for both. Tying that to
        // the capture length (the old durationSeconds + 2) meant waiting longer for a trigger
        // forced a bigger buffer, which is backwards: an armed analyser should be able to wait
        // all afternoon for a one-off event and still record only the milliseconds around it.
        double budget = durationSeconds + 2 + (triggerArmed ? armSeconds : 0);
        if (triggerArmed && armSeconds > 0)
            Console.WriteLine(string.Format(Inv,
                "Armed. Waiting up to {0:F0}s for the trigger, then capturing {1}s at {2} MHz on channels {3}...",
                armSeconds, durationSeconds, sampleRateHz / 1_000_000.0, string.Join(",", enabledChannels)));
        else
            Console.WriteLine($"Capturing for up to {durationSeconds}s at {sampleRateHz / 1_000_000.0} MHz on channels {string.Join(",", enabledChannels)}...");

        var captureStartUtc = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Native.ds_is_collecting() != 0 && sw.Elapsed.TotalSeconds < budget)
        {
            Thread.Sleep(50);
        }

        Native.ds_stop_collect();
        Thread.Sleep(200);

        lock (_lock)
        {
            _rawOut.Flush();
            _rawOut.Dispose();
        }

        Console.WriteLine($"Done. Wrote {_totalBytes} bytes to {outPath}");

        // An armed trigger that never fires produces a perfectly successful run with no data,
        // which reads exactly like broken hardware. Say which it was.
        if (_totalBytes == 0 && triggerArmed)
        {
            Console.WriteLine($"The trigger ({triggerSpec}) never fired, so nothing was captured. " +
                              "Check the condition against the line's actual idle state, or drop " +
                              "--trigger to capture free-running.");
            return;
        }

        // ---- demux + report -----------------------------------------------------------------
        var rawAll = File.ReadAllBytes(outPath);
        long expected = (long)(sampleRateHz * durationSeconds) * enabledChannels.Length / 8;
        Console.WriteLine($"\nCross-data demux: {rawAll.Length} bytes, {enabledChannels.Length} channel(s) " +
                          $"(expected ~{expected} bytes for {sampleRateHz / 1e6:0.###} MHz x {durationSeconds}s)");

        var perCh = CrossDemux(rawAll, enabledChannels.Length);
        for (int i = 0; i < perCh.Length; i++)
        {
            var chBytes = perCh[i];
            long samples = (long)chBytes.Length * 8;
            long high = 0, toggles = 0;
            bool prev = GetBit(chBytes, 0);
            for (long s = 0; s < samples; s++)
            {
                bool cur = GetBit(chBytes, s);
                if (cur) high++;
                if (s > 0 && cur != prev) toggles++;
                prev = cur;
            }
            string path = $"{_outBaseName}.ch{enabledChannels[i]}.raw";
            File.WriteAllBytes(path, chBytes);
            double duty = samples > 0 ? (double)high / samples : 0;
            string hint = toggles == 0 ? "  (flat - likely unconnected)"
                        : duty > 0.9 ? "  (idles high - looks like a UART line)"
                        : "";
            Console.WriteLine($"  ch{enabledChannels[i],-2} samples={samples,-12} duty={duty:F4} toggles={toggles,-8} -> {path}{hint}");
        }

        if (wantVcd && perCh.Length > 0)
        {
            // Every channel is demuxed from the same buffer, so they all hold the same count.
            long vcdSamples = (long)perCh[0].Length * 8;
            string vcdPath = $"{_outBaseName}.vcd";
            WriteVcd(vcdPath, perCh, enabledChannels, sampleRateHz, vcdSamples);
            Console.WriteLine($"  -> {vcdPath} ({vcdSamples} samples, {enabledChannels.Length} channel(s))");
        }

        // ---- optional UART decode, plus any protocol decoder plugin --------------------------
        if (decodeBaud > 0)
        {
            var decoders = LoadDecoders();
            var decoder = decoders.Count > 0 ? decoders[0] : null;
            Console.WriteLine(decoder is null
                ? "\n[plugin] no protocol decoder found - UART bytes and burst timing only."
                : $"\n[plugin] protocol decoder: {decoder.Name}");

            foreach (int ch in decodeChannels)
            {
                int idx = Array.IndexOf(enabledChannels, ch);
                if (idx < 0)
                {
                    Console.WriteLine($"\n[decode] channel {ch} was not captured, skipping.");
                    continue;
                }
                // Try both polarities and keep whichever decodes better.
                //
                // A and B of an RS485 pair carry the same data inverted, and which one a probe is
                // clipped to is not knowable from the capture alone - guessing wrong yields
                // plausible-looking but garbage bytes. A decoder's valid-frame count is an objective
                // tiebreak (verified: ch0 gives 2 valid frames upright and 0 inverted, ch2 the
                // exact reverse, at identical timestamps - same signal, opposite polarity).
                // With no decoder loaded there are no frames to count, so fall back to which
                // polarity produced fewer framing errors - weaker, but still better than assuming.
                var bytesNormal = DecodeUart(perCh[idx], sampleRateHz, decodeBaud, invert: false, parity: framing);
                var bytesInvert = DecodeUart(perCh[idx], sampleRateHz, decodeBaud, invert: true,  parity: framing);
                int nNormal, nInvert;
                string unit;
                if (decoder is not null)
                {
                    nNormal = decoder.Decode(bytesNormal, decodeBaud).Count;
                    nInvert = decoder.Decode(bytesInvert, decodeBaud).Count;
                    unit = "valid frame(s)";
                }
                else
                {
                    nNormal = bytesNormal.Count(b => b.FramingOk);
                    nInvert = bytesInvert.Count(b => b.FramingOk);
                    unit = "well-framed byte(s)";
                }
                bool useInvert = nInvert > nNormal;
                var bytes = useInvert ? bytesInvert : bytesNormal;
                Console.WriteLine($"\n[polarity] ch{ch}: upright={nNormal} {unit}, inverted={nInvert}" +
                                  $" -> using {(useInvert ? "INVERTED" : "upright")}");
                int nFraming = 0, nParity = 0;
                foreach (var b in bytes) { if (!b.FramingOk) nFraming++; if (!b.ParityOk) nParity++; }
                string framingName = framing switch
                {
                    ParityMode.Even => "8-E-1",
                    ParityMode.Odd  => "8-O-1",
                    _               => "8-N-1",
                };
                Console.WriteLine($"[decode] ch{ch} @ {decodeBaud} baud {framingName}: {bytes.Count} byte(s), " +
                                  $"{nFraming} framing error(s), {nParity} parity error(s)");
                if (bytes.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(bytes.Count, 64); i++) sb.Append($"{bytes[i].Value:X2} ");
                    Console.WriteLine($"  {sb}{(bytes.Count > 64 ? "..." : "")}");

                    // InvariantCulture matters here: on a Dutch-locale machine the default would
                    // write "0,724999380" and the file would not parse as numeric data.
                    string txt = $"{_outBaseName}.ch{ch}.uart.txt";
                    using (var w = new StreamWriter(txt))
                        foreach (var b in bytes)
                            w.WriteLine(string.Format(Inv, "{0:F9}	{1:X2}	{2}	{3}",
                                b.Time, b.Value, b.FramingOk ? "ok" : "FRAMING", b.ParityOk ? "ok" : "PARITY"));
                    Console.WriteLine($"  -> {txt}");

                    IReadOnlyList<ProtocolFrame> frames = Array.Empty<ProtocolFrame>();
                    if (decoder is not null)
                    {
                        Console.WriteLine($"  {decoder.Name} frames:");
                        decoder.PrintFrames(bytes, decodeBaud, Console.Out);
                        frames = decoder.Decode(bytes, decodeBaud);
                        AnalyzeTiming($"ch{ch}", frames, bytes, decodeBaud);

                        if (wantPcap)
                        {
                            if (decoder is IPcapExportable pcap)
                            {
                                int withBytes = 0;
                                foreach (var f in frames) if (f.Bytes.Count > 0) withBytes++;
                                if (withBytes == 0)
                                {
                                    Console.WriteLine($"  (no pcap: {decoder.Name} returned no frame bytes)");
                                }
                                else if (pcapStreamTarget is not null)
                                {
                                    // Opened lazily, and only once: the link type comes from the
                                    // decoder, and on a pipe the open blocks until a reader
                                    // attaches - doing that before we know there is anything to
                                    // send would stall a capture that was never going to emit.
                                    if (_pcapSink is null)
                                    {
                                        Console.WriteLine("  (no stream: no reader ever attached)");
                                    }
                                    else
                                    {
                                        _pcapSink.Write(frames, pcap.LinkType, captureStartUtc);
                                        Console.WriteLine($"  -> streamed {withBytes} frame(s) to {pcapStreamTarget}" +
                                                          $" ({_pcapSink.FramesWritten} total, linktype {pcap.LinkType})");
                                    }
                                }
                                else
                                {
                                    string pcapPath = $"{_outBaseName}.ch{ch}.pcap";
                                    WritePcap(pcapPath, frames, pcap.LinkType, captureStartUtc);
                                    Console.WriteLine($"  -> {pcapPath} ({withBytes} frame(s), linktype {pcap.LinkType})");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"  (no pcap: decoder {decoder.Name} does not support pcap export)");
                            }
                        }
                    }

                    // Burst view: works even where the signal is too distorted to byte-decode.
                    //
                    // The gap threshold matters. At 3 character times a request and its response
                    // merge into one burst (measured: a single 3.11 ms burst where the frames are
                    // only 1.41 ms and 1.61 ms). One character time separates them cleanly and
                    // exposes the ~0.1 ms device turnaround, while still being long enough not to
                    // split a single transmission at its inter-byte spacing.
                    double charT = 11.0 / decodeBaud;
                    var bursts = FindBursts(perCh[idx], sampleRateHz, 1.0 * charT,
                                            (int)((double)sampleRateHz / decodeBaud / 5.0));
                    AnalyzeBursts($"ch{ch}", bursts, frames);

                    string bcsv = $"{_outBaseName}.ch{ch}.bursts.csv";
                    using (var w = new StreamWriter(bcsv))
                    {
                        w.WriteLine("t_start_s,t_end_s,duration_s,edges,idle_before_s");
                        for (int i = 0; i < bursts.Count; i++)
                            w.WriteLine(string.Format(Inv, "{0:F9},{1:F9},{2:F9},{3},{4:F9}",
                                bursts[i].TStart, bursts[i].TEnd, bursts[i].TEnd - bursts[i].TStart,
                                bursts[i].Transitions,
                                i == 0 ? 0.0 : bursts[i].TStart - bursts[i - 1].TEnd));
                    }
                    Console.WriteLine($"  -> {bcsv}");

                    // If the complementary line of the pair was also captured, use both to get a
                    // true driver-enable view. Partner is found by matching frame timestamps: the
                    // other line of the same pair carries identical frames at identical times.
                    // Identify the partner line straight from the signal rather than from decoded
                    // frames - decoding can fail or shift, but A and B are anti-correlated by
                    // construction whenever anything is driving the bus. A flat/unconnected
                    // channel correlates at ~100% with everything and must be excluded, so require
                    // the candidate to actually toggle.
                    int partner = -1; double bestScore = 0;
                    for (int p = 0; p < perCh.Length; p++)
                    {
                        if (p == idx) continue;
                        long n = 0, differ = 0, ones = 0;
                        long step = Math.Max(1, perCh[p].Length / 200000);
                        for (long b = 0; b < perCh[p].Length; b += step)
                        {
                            byte x = perCh[idx][b], y = perCh[p][b];
                            for (int k = 0; k < 8; k++, n++)
                            {
                                int bx = (x >> k) & 1, by = (y >> k) & 1;
                                if (bx != by) differ++;
                                ones += by;
                            }
                        }
                        if (n == 0) continue;
                        double dutyP = (double)ones / n;
                        if (dutyP < 0.001 || dutyP > 0.999) continue;   // flat line, not a partner
                        double anti = (double)differ / n;               // fraction where A != B
                        if (anti > bestScore) { bestScore = anti; partner = p; }
                    }
                    if (partner >= 0)
                    {
                        Console.WriteLine(string.Format(Inv,
                            "\n[pair] ch{0} + ch{1}: lines differ {2:P2} of the time -> treating as the A/B pair",
                            ch, enabledChannels[partner], bestScore));
                        AnalyzeBusState($"ch{ch}/ch{enabledChannels[partner]}", perCh[idx], perCh[partner],
                                        sampleRateHz, 2.0 / decodeBaud);
                    }
                    else
                    {
                        Console.WriteLine("\n[pair] no complementary line captured - " +
                                          "differential idle/busy detection needs both A and B.");
                    }

                    if (frames.Count > 0)
                    {
                        // The protocol-specific columns collapse into one quoted "summary" field:
                        // the host does not know what a frame carries, only what the decoder chose
                        // to say about it. Quoted and doubled, so a summary containing a comma or a
                        // quote cannot break the row.
                        string csv = $"{_outBaseName}.ch{ch}.frames.csv";
                        using (var w = new StreamWriter(csv))
                        {
                            w.WriteLine("t_start_s,t_end_s,duration_s,kind,max_byte_gap_s,summary");
                            foreach (var f in frames)
                                w.WriteLine(string.Format(Inv,
                                    "{0:F9},{1:F9},{2:F9},{3},{4:F9},\"{5}\"",
                                    f.TStart, f.TEnd, f.TEnd - f.TStart, Lower(f.Kind), f.MaxByteGap,
                                    f.Summary.Replace("\"", "\"\"")));
                        }
                        Console.WriteLine($"  -> {csv}");
                    }
                }
            }
        }

        // Flush and close the stream sink before releasing the device, so a reader on the other end
        // of the pipe sees every frame this run decoded rather than losing the tail.
        if (_pcapSink is not null)
        {
            Console.WriteLine($"[stream] closing after {_pcapSink.FramesWritten} frame(s).");
            _pcapSink.Dispose();
            _pcapSink = null;
        }

        Native.ds_release_actived_device();
        Native.ds_lib_exit();
    }
}
