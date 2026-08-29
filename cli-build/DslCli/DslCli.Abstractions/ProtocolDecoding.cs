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
        public bool FramingOk { get; }

        /// <summary>Creates a decoded byte.</summary>
        public DecodedByte(double time, byte value, bool framingOk)
        {
            Time = time;
            Value = value;
            FramingOk = framingOk;
        }
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

        /// <summary>Creates a protocol frame.</summary>
        public ProtocolFrame(double tStart, double tEnd, FrameKind kind, double maxByteGap, string summary)
        {
            TStart = tStart;
            TEnd = tEnd;
            Kind = kind;
            MaxByteGap = maxByteGap;
            Summary = summary ?? string.Empty;
        }
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
