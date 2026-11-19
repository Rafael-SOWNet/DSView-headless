using System.Collections.Generic;
using System.IO;

namespace DslCli.Abstractions
{
    /// <summary>
    /// One byte recovered from the UART decoder.
    /// </summary>
    /// <remarks>
    /// <see cref="Time"/> is the exact sample of the start-bit falling edge, so it is accurate to a
    /// single sample period (10 ns at 100 MHz). That is what makes the latency and jitter figures
    /// derived from these bytes meaningful rather than indicative — a decoder that reported only
    /// byte order would be useless for timing work.
    /// </remarks>
    public readonly struct DecodedByte
    {
        /// <summary>Seconds from the start of the capture to the start-bit edge.</summary>
        public double Time { get; }

        /// <summary>The eight data bits, LSB first on the wire.</summary>
        public byte Value { get; }

        /// <summary>False when the stop bit was not where it should have been.</summary>
        /// <remarks>
        /// Until 2026-08-31 this was sampled at bit position 9.5, which is only the stop bit on a
        /// frame with NO parity. On an 8-E-1 bus 9.5 is the PARITY bit, so the flag reported the
        /// parity value rather than framing - 71% "errors" on a bus where every frame's CRC was
        /// valid, because the parity bit simply tracks the data. It now means what it says, and
        /// parity is reported separately.
        /// </remarks>
        public bool FramingOk { get; }

        /// <summary>
        /// False when the parity bit disagreed with the data. Always true when the decode ran with
        /// no parity configured, since there is no bit to check.
        /// </summary>
        public bool ParityOk { get; }

        /// <summary>Creates a decoded byte with no parity bit on the wire.</summary>
        public DecodedByte(double time, byte value, bool framingOk)
            : this(time, value, framingOk, true)
        {
        }

        /// <summary>Creates a decoded byte, including the parity result.</summary>
        // A second constructor rather than an optional argument on the first: optional arguments are
        // baked into the call site, so adding one is source-compatible but NOT binary-compatible and
        // an already-built plugin dies with MissingMethodException. An overload is safe.
        public DecodedByte(double time, byte value, bool framingOk, bool parityOk)
            : this(time, value, framingOk, parityOk, 0)
        {
        }

        /// <summary>Creates a decoded byte, including the parity result and the rate it was read at.</summary>
        public DecodedByte(double time, byte value, bool framingOk, bool parityOk, int baud)
        {
            Time = time;
            Value = value;
            FramingOk = framingOk;
            ParityOk = parityOk;
            Baud = baud;
        }

        /// <summary>
        /// The bit rate this byte was decoded at, or 0 when the producer did not record it.
        /// </summary>
        /// <remarks>
        /// Not always the rate the capture was started with: a bus can change rate in the middle of
        /// a capture (GeoBUS SwitchBaudrate, 115200 to 250000 and back), and the host then decodes
        /// each burst at whichever candidate rate reads it cleanly. A decoder that converts byte
        /// counts into time (frame end, inter-byte gap) should use this rather than the single rate
        /// passed to <see cref="IProtocolDecoder.Decode"/> whenever it is non-zero.
        /// </remarks>
        public int Baud { get; }
    }

    /// <summary>What role a frame plays in a request/response protocol.</summary>
    public enum FrameKind
    {
        /// <summary>The decoder could not classify it.</summary>
        Other = 0,

        /// <summary>A master/host asking for something.</summary>
        Request,

        /// <summary>A slave/device answering a <see cref="Request"/>.</summary>
        Response,

        /// <summary>Unsolicited data pushed by a device.</summary>
        Stream,
    }

    /// <summary>
    /// A protocol frame located in the byte stream, reduced to what the host's timing analysis
    /// needs.
    /// </summary>
    /// <remarks>
    /// Everything protocol-specific — addresses, command bytes, payload — belongs in
    /// <see cref="Summary"/>, as text the decoder has already formatted. The host deliberately
    /// knows nothing about any particular protocol: that is what lets a decoder live in a separate
    /// repository, on its own release schedule and its own licence, without the analysis code
    /// having to grow a case for it.
    /// </remarks>
    public sealed class ProtocolFrame
    {
        /// <summary>Start-bit edge of the frame's first byte.</summary>
        public double TStart { get; }

        /// <summary>End of the last byte's stop bit.</summary>
        public double TEnd { get; }

        /// <summary>Role of this frame in the exchange.</summary>
        public FrameKind Kind { get; }

        /// <summary>
        /// Largest idle gap between two consecutive bytes inside this frame. A clean UART
        /// transmission has one close to zero; anything larger is the sender pausing mid-frame,
        /// which is a host-side (USB scheduling, latency timer) effect rather than a line one.
        /// </summary>
        public double MaxByteGap { get; }

        /// <summary>Pre-formatted protocol detail for display, e.g. <c>cmd=0x70 len=3</c>.</summary>
        public string Summary { get; }

        /// <summary>
        /// The frame exactly as it appeared on the wire, or empty when the decoder does not supply
        /// it. Deliberately the raw bytes and nothing more: the host has no idea what they mean,
        /// and only passes them through to writers that need the original octets — currently the
        /// pcap export. Anything a human should read belongs in <see cref="Summary"/>.
        /// </summary>
        public IReadOnlyList<byte> Bytes { get; }

        /// <summary>Creates a protocol frame.</summary>
        public ProtocolFrame(double tStart, double tEnd, FrameKind kind, double maxByteGap, string summary,
                             IReadOnlyList<byte> bytes = null)
        {
            TStart = tStart;
            TEnd = tEnd;
            Kind = kind;
            MaxByteGap = maxByteGap;
            Summary = summary ?? string.Empty;
            Bytes = bytes ?? EmptyBytes;
        }

        private static readonly byte[] EmptyBytes = new byte[0];
    }

    /// <summary>
    /// Implemented alongside <see cref="IProtocolDecoder"/> by a decoder whose frames are worth
    /// exporting to pcap, for reading in Wireshark.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="IProtocolDecoder"/> so a decoder that only wants to print
    /// frames does not have to answer a question about link-layer types. The host writes the pcap;
    /// all it needs from the decoder is which link type to stamp on the file and the frame bytes
    /// (<see cref="ProtocolFrame.Bytes"/>). It never has to understand the protocol itself, which
    /// is what lets the dissector live in the decoder's own repository.
    /// </remarks>
    public interface IPcapExportable
    {
        /// <summary>
        /// The pcap link type (DLT) to record in the file header. For a bus with no registered
        /// link type, use one of the user range LINKTYPE_USER0..15 (147..162) and ship a dissector
        /// that registers against it.
        /// </summary>
        int LinkType { get; }
    }

    /// <summary>
    /// Locates protocol frames in a decoded UART byte stream.
    /// </summary>
    /// <remarks>
    /// Implement this in a class library, drop the built assembly into the host's <c>plugins</c>
    /// folder (or point <c>DSLCLI_PLUGINS</c> at it), and the host will discover it by reflection.
    /// Implementations need a public parameterless constructor.
    /// </remarks>
    public interface IProtocolDecoder
    {
        /// <summary>Short name shown in the host's output, e.g. the protocol's name.</summary>
        string Name { get; }

        /// <summary>
        /// Finds every frame in <paramref name="bytes"/>. Returning an empty list is normal and
        /// must not throw: the host calls this on both signal polarities and keeps whichever
        /// yields more frames, so "found nothing" is a meaningful answer.
        /// </summary>
        IReadOnlyList<ProtocolFrame> Decode(IReadOnlyList<DecodedByte> bytes, int baud);

        /// <summary>
        /// Writes a human-readable dump of the frames. Called once, after <see cref="Decode"/>
        /// has already chosen a polarity.
        /// </summary>
        void PrintFrames(IReadOnlyList<DecodedByte> bytes, int baud, TextWriter output);
    }
}
