using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.LianLiLedRing
{
    /// <summary>
    /// Lighting effects for the Lian Li Universal Screen 8.8" LED ring,
    /// ordered to match the vendor's L-Connect effect list.
    /// </summary>
    public enum LianLiLedEffect
    {
        [Description("Off")]
        Off = 0,
        [Description("Rainbow")]
        Rainbow,
        [Description("Wave")]
        Wave,
        [Description("Static Color")]
        StaticColor,
        [Description("Breathing")]
        Breathing,
        [Description("Rainbow Morph")]
        RainbowMorph,
        [Description("Paint")]
        Paint,
        [Description("Runway")]
        Runway,
        [Description("Tide")]
        Tide,
        [Description("Blow Up")]
        BlowUp,
        [Description("Meteor")]
        Meteor,
        [Description("Snooker")]
        Snooker,
        [Description("Mixing")]
        Mixing,
        [Description("Ping-Pong")]
        PingPong,
        [Description("Bullet Stack")]
        BulletStack,
        [Description("Twinkle")]
        Twinkle,
        [Description("River")]
        River,
        [Description("Hourglass")]
        Hourglass,
        [Description("Electric Current")]
        ElectricCurrent,
        [Description("Rainbow Wave")]
        RainbowWave,
    }

    /// <summary>
    /// USB transport for the Lian Li Universal Screen 8.8" LED ring
    /// (VID 0x0416, PID 0x8050, 60 LEDs). This is a separate USB device
    /// from the LCD (0x1CBE:0xA088).
    ///
    /// Protocol: 64-byte bulk packets on EP01, no encryption, no response:
    ///   byte[0]     = 0x11 (set LED chunk)
    ///   byte[1]     = LED offset (0, 20, 40)
    ///   byte[4..63] = 20 LEDs x 3 bytes RGB
    /// Three packets paint the full 60-LED ring. The ring has no hardware
    /// effect engine; animations are rendered host-side and streamed.
    /// </summary>
    public sealed class LianLiLedRingDevice : IDisposable
    {
        public const int VendorId = 0x0416;
        public const int ProductId = 0x8050;
        public const int LedCount = 60;

        private const int PacketSize = 64;
        private const byte CmdSetLeds = 0x11;
        private const int LedsPerChunk = 20;
        private const int WriteTimeoutMs = 1000;

        private static readonly ILogger Logger = Log.ForContext<LianLiLedRingDevice>();

        private readonly UsbDevice _usbDevice;
        private readonly UsbEndpointWriter _writer;
        private bool _disposed;

        private LianLiLedRingDevice(UsbDevice usbDevice)
        {
            _usbDevice = usbDevice;

            if (_usbDevice is IUsbDevice wholeUsbDevice)
            {
                wholeUsbDevice.SetConfiguration(1);
                wholeUsbDevice.ClaimInterface(0);
            }

            _writer = _usbDevice.OpenEndpointWriter(WriteEndpointID.Ep01);
        }

        /// <summary>Find and open the first LED ring. Returns null when absent.</summary>
        public static LianLiLedRingDevice? TryOpen()
        {
            try
            {
                foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
                {
                    if (deviceReg.Vid == VendorId && deviceReg.Pid == ProductId && deviceReg.Open(out var usbDevice))
                    {
                        return new LianLiLedRingDevice(usbDevice);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLi LED ring open failed");
            }

            return null;
        }

        /// <summary>Send one full 60-LED frame (180 RGB bytes) as three chunk packets.</summary>
        public bool SendFrame(byte[] rgb)
        {
            if (rgb == null || rgb.Length < LedCount * 3)
            {
                throw new ArgumentException($"Frame must be at least {LedCount * 3} bytes", nameof(rgb));
            }

            try
            {
                for (var chunk = 0; chunk < LedCount / LedsPerChunk; chunk++)
                {
                    var packet = new byte[PacketSize];
                    packet[0] = CmdSetLeds;
                    packet[1] = (byte)(chunk * LedsPerChunk);
                    Buffer.BlockCopy(rgb, chunk * LedsPerChunk * 3, packet, 4, LedsPerChunk * 3);

                    var errorCode = _writer.Write(packet, WriteTimeoutMs, out var transferred);
                    if (errorCode != ErrorCode.None || transferred != PacketSize)
                    {
                        Logger.Warning(
                            "LianLi LED ring write failed: {ErrorCode}, transferred {Transferred}/{Expected}",
                            errorCode,
                            transferred,
                            PacketSize);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "LianLi LED ring send failed");
                return false;
            }
        }

        public bool TurnOff() => SendFrame(new byte[LedCount * 3]);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_usbDevice.IsOpen)
                {
                    _writer.Dispose();

                    if (_usbDevice is IUsbDevice wholeUsbDevice)
                    {
                        wholeUsbDevice.ReleaseInterface(0);
                    }

                    _usbDevice.Close();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "LianLi LED ring dispose failed");
            }
        }
    }

    /// <summary>A precomputed LED animation: frame sequence plus base frame interval.</summary>
    public sealed class LianLiLedAnimation
    {
        public LianLiLedAnimation(List<byte[]> frames, double intervalBaseMs)
        {
            Frames = frames;
            IntervalBaseMs = intervalBaseMs;
        }

        public List<byte[]> Frames { get; }
        public double IntervalBaseMs { get; }
    }

    /// <summary>
    /// LED effect frame generators. Original implementations informed by the
    /// observable behavior of the vendor effects (frame counts, envelopes and
    /// the panel's mirrored bezel geometry), written independently.
    ///
    /// Geometry: the 60 LEDs surround the screen bezel with symmetry anchors
    /// at LED 47 and LED 17. Effects are authored on shorter logical strips
    /// and mirrored onto the bezel:
    ///  - 31-strip: two mirrored halves (used by Paint, Runway, Hourglass, ...)
    ///  - 16-strip: four mirrored quadrants (Tide, Snooker, ...)
    ///  - 25-strip: two halves with 7-LED corner blocks (Meteor, Ping-Pong, ...)
    /// </summary>
    public static class LianLiLedEffectRenderer
    {
        private const int LedCount = LianLiLedRingDevice.LedCount;
        private const double DefaultIntervalMs = 11.0;

        // Fixed palette used by the vendor for Bullet Stack and Twinkle.
        private static readonly (byte R, byte G, byte B)[] Palette7 =
        {
            (153, 0, 204),
            (255, 51, 204),
            (255, 153, 0),
            (255, 255, 0),
            (0, 255, 102),
            (51, 255, 255),
            (66, 87, 248),
        };

        public static LianLiLedAnimation Build(
            LianLiLedEffect effect,
            IReadOnlyList<(byte R, byte G, byte B)> colors,
            int brightnessPercent,
            bool reverse)
        {
            var bright = Math.Clamp(brightnessPercent, 0, 100) * 255 / 100;
            var frames = new List<byte[]>();
            var interval = DefaultIntervalMs;

            switch (effect)
            {
                case LianLiLedEffect.Rainbow:
                    interval = 16.5;
                    BuildRainbow(frames, bright, reverse);
                    break;
                case LianLiLedEffect.Wave:
                    interval = 14.3;
                    BuildWave(frames, colors, bright, reverse);
                    break;
                case LianLiLedEffect.StaticColor:
                    BuildStatic(frames, ColorAt(colors, 0), bright);
                    break;
                case LianLiLedEffect.Breathing:
                    BuildBreathing(frames, ColorAt(colors, 0), bright);
                    break;
                case LianLiLedEffect.RainbowMorph:
                    BuildRainbowMorph(frames, bright);
                    break;
                case LianLiLedEffect.Paint:
                    BuildPaint(frames, colors, bright);
                    break;
                case LianLiLedEffect.Runway:
                    BuildRunway(frames, colors, bright);
                    break;
                case LianLiLedEffect.Tide:
                    BuildTide(frames, colors, bright);
                    break;
                case LianLiLedEffect.BlowUp:
                    BuildBlowUp(frames, colors, bright);
                    break;
                case LianLiLedEffect.Meteor:
                    BuildMeteor(frames, colors, bright, reverse);
                    break;
                case LianLiLedEffect.Snooker:
                    BuildSnooker(frames, colors, bright);
                    break;
                case LianLiLedEffect.Mixing:
                    BuildMixing(frames, colors, bright);
                    break;
                case LianLiLedEffect.PingPong:
                    BuildPingPong(frames, colors, bright);
                    break;
                case LianLiLedEffect.BulletStack:
                    BuildBulletStack(frames, bright, reverse);
                    break;
                case LianLiLedEffect.Twinkle:
                    BuildTwinkle(frames, bright);
                    break;
                case LianLiLedEffect.River:
                    interval = 14.3;
                    BuildRiver(frames, colors, bright, reverse);
                    break;
                case LianLiLedEffect.Hourglass:
                    BuildHourglass(frames, colors, bright);
                    break;
                case LianLiLedEffect.ElectricCurrent:
                    BuildElectricCurrent(frames, colors, bright);
                    break;
                case LianLiLedEffect.RainbowWave:
                    interval = 16.5;
                    BuildRainbowWave(frames, bright, reverse);
                    break;
                case LianLiLedEffect.Off:
                default:
                    frames.Add(new byte[LedCount * 3]);
                    break;
            }

            if (frames.Count == 0)
            {
                frames.Add(new byte[LedCount * 3]);
            }

            return new LianLiLedAnimation(frames, interval);
        }

        // ---------- color helpers ----------

        private static (byte R, byte G, byte B) ColorAt(IReadOnlyList<(byte R, byte G, byte B)> colors, int index)
        {
            if (colors == null || colors.Count == 0)
            {
                return (255, 102, 0);
            }

            return colors[index % colors.Count];
        }

        private static byte Scale(byte value, int factor255) => (byte)(value * factor255 >> 8);

        private static (byte R, byte G, byte B) Scale((byte R, byte G, byte B) c, int factor255) =>
            (Scale(c.R, factor255), Scale(c.G, factor255), Scale(c.B, factor255));

        /// <summary>Piecewise-linear RGB color wheel; position in [0,1).</summary>
        private static (byte R, byte G, byte B) Wheel(double position)
        {
            position -= Math.Floor(position);
            var p = position * 3.0;
            var seg = (int)Math.Floor(p);
            var f = p - seg;
            var down = (byte)(255.0 * (1.0 - f));
            var up = (byte)(255.0 * f);

            return seg switch
            {
                0 => (down, up, (byte)0),
                1 => ((byte)0, down, up),
                _ => (up, (byte)0, down),
            };
        }

        // ---------- frame/geometry helpers ----------

        private static void SetLed(byte[] frame, int led, (byte R, byte G, byte B) c)
        {
            var off = led * 3;
            frame[off] = c.R;
            frame[off + 1] = c.G;
            frame[off + 2] = c.B;
        }

        private static byte[] FromRing((byte R, byte G, byte B)[] ring)
        {
            var frame = new byte[LedCount * 3];
            for (var i = 0; i < LedCount; i++)
            {
                SetLed(frame, i, ring[i]);
            }
            return frame;
        }

        /// <summary>31-entry strip mirrored onto the two bezel halves (anchors LED 47 / LED 17).</summary>
        private static byte[] Map31((byte R, byte G, byte B)[] strip)
        {
            var frame = new byte[LedCount * 3];
            for (var k = 0; k < 13; k++) SetLed(frame, k + 47, strip[k]);
            for (var k = 0; k < 18; k++) SetLed(frame, k, strip[k + 13]);
            for (var k = 0; k < 31; k++) SetLed(frame, 47 - k, strip[k]);
            return frame;
        }

        /// <summary>16-entry strip mirrored onto four bezel quadrants.</summary>
        private static byte[] Map16((byte R, byte G, byte B)[] strip)
        {
            var frame = new byte[LedCount * 3];
            for (var k = 0; k < 13; k++) SetLed(frame, k + 47, strip[k]);
            for (var k = 0; k < 3; k++) SetLed(frame, k, strip[k + 13]);
            for (var k = 0; k < 16; k++) SetLed(frame, 17 - k, strip[k]);
            for (var k = 0; k < 16; k++) SetLed(frame, 47 - k, strip[k]);
            for (var k = 0; k < 16; k++) SetLed(frame, 17 + k, strip[k]);
            return frame;
        }

        /// <summary>32-entry strip across two quadrant pairs (used by Mixing).</summary>
        private static byte[] Map32((byte R, byte G, byte B)[] strip)
        {
            var frame = new byte[LedCount * 3];
            for (var k = 0; k < 13; k++) SetLed(frame, k + 47, strip[k]);
            for (var k = 0; k < 3; k++) SetLed(frame, k, strip[k + 13]);
            for (var k = 0; k < 16; k++) SetLed(frame, 17 - k, strip[31 - k]);
            for (var k = 0; k < 16; k++) SetLed(frame, 47 - k, strip[k]);
            for (var k = 0; k < 16; k++) SetLed(frame, 17 + k, strip[31 - k]);
            return frame;
        }

        /// <summary>25-entry strip mirrored onto two halves with 7-LED corner blocks.</summary>
        private static byte[] Map25((byte R, byte G, byte B)[] strip)
        {
            var frame = new byte[LedCount * 3];
            for (var k = 0; k < 7; k++) SetLed(frame, k + 44, strip[0]);
            for (var k = 0; k < 9; k++) SetLed(frame, k + 51, strip[k + 1]);
            for (var k = 0; k < 14; k++) SetLed(frame, k, strip[k + 10]);
            for (var k = 0; k < 7; k++) SetLed(frame, k + 14, strip[24]);
            for (var k = 0; k < 23; k++) SetLed(frame, 43 - k, strip[k + 1]);
            return frame;
        }

        private static (byte R, byte G, byte B)[] NewStrip(int length) => new (byte R, byte G, byte B)[length];

        // ---------- effects ----------

        private static void BuildRainbow(List<byte[]> frames, int bright, bool reverse)
        {
            // 30-position color wheel scrolled around the ring, tiled twice.
            for (var shift = 0; shift < 30; shift++)
            {
                var ring = NewStrip(LedCount);
                for (var j = 0; j < 30; j++)
                {
                    var color = Scale(Wheel((j + shift) / 30.0), bright);
                    var pos = reverse ? j : 30 - j - 1;
                    ring[pos] = color;
                    ring[pos + 30] = color;
                }
                frames.Add(FromRing(ring));
            }
        }

        private static void BuildWave(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright, bool reverse)
        {
            // 12-LED intensity envelope scrolled and tiled 5x, cycling 6 colors.
            byte[] envelope = { 0, 0, 32, 64, 128, 255, 128, 64, 32, 0, 0, 0 };

            for (var c = 0; c < 6; c++)
            {
                var color = ColorAt(colors, c);
                for (var repeat = 0; repeat < 4; repeat++)
                {
                    for (var shift = 0; shift < 12; shift++)
                    {
                        var ring = NewStrip(LedCount);
                        for (var j = 0; j < 12; j++)
                        {
                            var level = envelope[(j + shift) % 12];
                            var value = Scale(Scale(color, level), bright);
                            var pos = reverse ? j : 12 - j - 1;
                            for (var tile = 0; tile < 5; tile++)
                            {
                                ring[tile * 12 + pos] = value;
                            }
                        }
                        frames.Add(FromRing(ring));
                    }
                }
            }
        }

        private static void BuildStatic(List<byte[]> frames, (byte R, byte G, byte B) color, int bright)
        {
            var ring = NewStrip(LedCount);
            var value = Scale(color, bright);
            for (var i = 0; i < LedCount; i++) ring[i] = value;
            frames.Add(FromRing(ring));
        }

        private static void BuildBreathing(List<byte[]> frames, (byte R, byte G, byte B) color, int bright)
        {
            // 85 steps up, 85 steps down, uniform.
            for (var phase = 0; phase < 2; phase++)
            {
                for (var j = 0; j < 85; j++)
                {
                    var level = (byte)((j * 3) & 0xFF);
                    if (phase == 1) level = (byte)(255 - level);
                    var value = Scale(Scale(color, level), bright);
                    var ring = NewStrip(LedCount);
                    for (var i = 0; i < LedCount; i++) ring[i] = value;
                    frames.Add(FromRing(ring));
                }
            }
        }

        private static void BuildRainbowMorph(List<byte[]> frames, int bright)
        {
            // Uniform color walking the wheel; 127 frames per cycle.
            for (var j = 0; j < 127; j++)
            {
                var value = Scale(Wheel(j / 127.0), bright);
                var ring = NewStrip(LedCount);
                for (var i = 0; i < LedCount; i++) ring[i] = value;
                frames.Add(FromRing(ring));
            }
        }

        private static void BuildPaint(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // Each color paints over the previous one, serpentine direction.
            for (var c = 0; c < 6; c++)
            {
                var current = Scale(ColorAt(colors, c), bright);
                var previous = Scale(ColorAt(colors, (c + 5) % 6), bright);
                for (var j = 0; j < 31; j++)
                {
                    var strip = NewStrip(31);
                    for (var k = 0; k < 31; k++)
                    {
                        var color = k <= j ? current : previous;
                        var pos = (c % 2 == 1) ? 31 - k - 1 : k;
                        strip[pos] = color;
                    }
                    frames.Add(Map31(strip));
                }
            }
        }

        private static void BuildRunway(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // 4-LED block of color 2 sliding over a color 1 background, both directions.
            var background = Scale(ColorAt(colors, 0), bright);
            var block = Scale(ColorAt(colors, 1), bright);

            for (var dir = 0; dir < 2; dir++)
            {
                for (var j = 0; j < 35; j++)
                {
                    var strip = NewStrip(31);
                    for (var k = 0; k < 31; k++)
                    {
                        var color = (k <= j && k + 4 > j) ? block : background;
                        var pos = dir == 1 ? 31 - k - 1 : k;
                        strip[pos] = color;
                    }
                    frames.Add(Map31(strip));
                }
            }
        }

        private static void BuildTide(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // Each color floods over the previous one across the quadrants.
            for (var c = 0; c < 6; c++)
            {
                var current = Scale(ColorAt(colors, c), bright);
                var previous = Scale(ColorAt(colors, (c + 5) % 6), bright);
                for (var j = 0; j < 16; j++)
                {
                    var strip = NewStrip(16);
                    for (var k = 0; k < 16; k++)
                    {
                        strip[k] = k <= j ? current : previous;
                    }
                    frames.Add(Map16(strip));
                }
            }
        }

        private static void BuildBlowUp(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // Fill up per quadrant, then a long uniform fade-out.
            for (var c = 0; c < 6; c++)
            {
                var color = ColorAt(colors, c);
                var value = Scale(color, bright);

                for (var j = 0; j < 16; j++)
                {
                    var strip = NewStrip(16);
                    for (var k = 0; k <= j; k++)
                    {
                        strip[15 - k] = value;
                    }
                    frames.Add(Map16(strip));
                }

                for (var j = 0; j < 35; j++)
                {
                    var level = j < 25 ? (25 - j - 1) * 10 : 0;
                    var faded = Scale(Scale(color, level), bright);
                    var ring = NewStrip(LedCount);
                    for (var i = 0; i < LedCount; i++) ring[i] = faded;
                    frames.Add(FromRing(ring));
                }
            }
        }

        private static void BuildMeteor(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright, bool reverse)
        {
            // 5-LED meteor head with ramped brightness travelling each half.
            byte[] head = { 16, 32, 64, 128, 255 };

            for (var c = 0; c < 6; c++)
            {
                var color = ColorAt(colors, c);
                for (var j = 0; j < 30; j++)
                {
                    var strip = NewStrip(25);
                    var headIndex = 0;
                    for (var k = 0; k < 25; k++)
                    {
                        var value = default((byte R, byte G, byte B));
                        if (k <= j && k + 5 > j && headIndex < head.Length)
                        {
                            value = Scale(Scale(color, head[headIndex]), bright);
                            headIndex++;
                        }
                        var pos = reverse ? 25 - k - 1 : k;
                        strip[pos] = value;
                    }
                    frames.Add(Map25(strip));
                }
            }
        }

        private static void BuildSnooker(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // 3-LED ball bouncing back and forth across the quadrants.
            for (var c = 0; c < 6; c++)
            {
                var value = Scale(ColorAt(colors, c), bright);
                for (var dir = 0; dir < 2; dir++)
                {
                    for (var j = 1; j < 15; j++)
                    {
                        var strip = NewStrip(16);
                        for (var k = 0; k < 16; k++)
                        {
                            if (k <= j && k + 3 > j)
                            {
                                var pos = dir == 1 ? 16 - k - 1 : k;
                                strip[pos] = value;
                            }
                        }
                        frames.Add(Map16(strip));
                    }
                }
            }
        }

        private static void BuildMixing(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // Two colors approach from opposite ends, then their blend floods in.
            var c0 = ColorAt(colors, 0);
            var c1 = ColorAt(colors, 1);

            var blend = (
                R: (byte)Math.Min(255, c0.R + c1.R),
                G: (byte)Math.Min(255, c0.G + c1.G),
                B: (byte)Math.Min(255, c0.B + c1.B));
            if (blend.R + blend.G + blend.B > 536)
            {
                blend = (Scale(blend.R, 178), Scale(blend.G, 178), Scale(blend.B, 178));
            }

            var v0 = Scale(c0, bright);
            var v1 = Scale(c1, bright);
            var vb = Scale(blend, bright);

            // Phase 1: two 3-LED pulses travelling toward each other on a 32-strip.
            for (var j = 0; j < 16; j++)
            {
                var strip = NewStrip(32);
                for (var k = 0; k < 16; k++)
                {
                    if (k <= j && k + 3 > j)
                    {
                        strip[k] = v0;
                        strip[32 - k - 1] = v1;
                    }
                }
                frames.Add(Map32(strip));
            }

            // Phase 2: blended color floods from both ends inward.
            for (var j = 0; j < 16; j++)
            {
                var strip = NewStrip(32);
                for (var k = 0; k <= j; k++)
                {
                    strip[16 - k - 1] = vb;
                    strip[16 + k] = vb;
                }
                frames.Add(Map32(strip));
            }
        }

        private static void BuildPingPong(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // 4-LED pulse bouncing across each half.
            for (var c = 0; c < 6; c++)
            {
                var value = Scale(ColorAt(colors, c), bright);
                for (var dir = 0; dir < 2; dir++)
                {
                    for (var j = 0; j < 29; j++)
                    {
                        var strip = NewStrip(25);
                        for (var k = 0; k < 25; k++)
                        {
                            if (k <= j && k + 4 > j)
                            {
                                var pos = dir == 1 ? 25 - k - 1 : k;
                                strip[pos] = value;
                            }
                        }
                        frames.Add(Map25(strip));
                    }
                }
            }
        }

        private static void BuildBulletStack(List<byte[]> frames, int bright, bool reverse)
        {
            // Shots travel along a shrinking track and stack at the end.
            var paletteIndex = 0;
            for (var stacked = 0; stacked < 25; stacked++)
            {
                paletteIndex = (paletteIndex + 1) % Palette7.Length;
                var shot = Scale(Palette7[paletteIndex], bright);
                var previous = Scale(Palette7[(paletteIndex + Palette7.Length - 1) % Palette7.Length], bright);
                var track = 25 - stacked;

                for (var j = 0; j < track; j++)
                {
                    var strip = NewStrip(25);
                    for (var k = 0; k < stacked; k++)
                    {
                        var pos = track + k;
                        if (reverse) pos = 25 - pos - 1;
                        strip[pos] = previous;
                    }
                    var shotPos = reverse ? 25 - j - 1 : j;
                    strip[shotPos] = shot;
                    frames.Add(Map25(strip));
                }
            }
        }

        private static void BuildTwinkle(List<byte[]> frames, int bright)
        {
            // Each LED twinkles with a random palette color and random phase.
            var random = new Random();
            var colorPick = new int[LedCount];
            var phase = new int[LedCount];
            var period = new int[LedCount];
            for (var i = 0; i < LedCount; i++)
            {
                colorPick[i] = random.Next(Palette7.Length);
                phase[i] = random.Next(200);
                period[i] = 30 + random.Next(50);
            }

            for (var j = 0; j < 200; j++)
            {
                var ring = NewStrip(LedCount);
                for (var i = 0; i < LedCount; i++)
                {
                    var t = (j + phase[i]) % period[i];
                    var half = period[i] / 2;
                    var level = t < half
                        ? t * 255 / Math.Max(1, half)
                        : (period[i] - t) * 255 / Math.Max(1, period[i] - half);
                    ring[i] = Scale(Scale(Palette7[colorPick[i]], level), bright);
                }
                frames.Add(FromRing(ring));
            }
        }

        private static void BuildRiver(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright, bool reverse)
        {
            // Repeating 6-LED pattern (4x color 1, 2x color 2) flowing around the ring.
            byte[] pattern = { 0, 0, 0, 0, 1, 1 };

            for (var shift = 0; shift < 6; shift++)
            {
                var ring = NewStrip(LedCount);
                for (var j = 0; j < 6; j++)
                {
                    var color = ColorAt(colors, pattern[(j + shift) % 6]);
                    var value = Scale(color, bright);
                    var pos = reverse ? j : 6 - j - 1;
                    for (var tile = 0; tile < 10; tile++)
                    {
                        ring[tile * 6 + pos] = value;
                    }
                }
                frames.Add(FromRing(ring));
            }
        }

        private static void BuildHourglass(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // Sand fills from both ends to the middle, holds, then drains away.
            for (var c = 0; c < 4; c++)
            {
                var color = ColorAt(colors, c);
                var value = Scale(color, bright);
                var dim1 = Scale(Scale(color, 20), bright);
                var dim2 = Scale(Scale(color, 10), bright);

                // Fill phase: both ends grow toward the center.
                for (var j = 0; j < 16; j++)
                {
                    var strip = NewStrip(31);
                    for (var k = 0; k <= j; k++)
                    {
                        strip[k] = value;
                        strip[30 - k] = value;
                    }
                    frames.Add(Map31(strip));
                }

                // Second pass with the center grain visible.
                for (var j = 0; j < 16; j++)
                {
                    var strip = NewStrip(31);
                    strip[15] = value;
                    for (var k = 0; k <= j; k++)
                    {
                        strip[k] = value;
                        strip[30 - k] = value;
                    }
                    frames.Add(Map31(strip));
                }

                // Drain phase: filled ends recede with a dim tail.
                for (var j = 15; j >= 0; j--)
                {
                    var strip = NewStrip(31);
                    for (var k = 0; k < j; k++)
                    {
                        strip[k] = value;
                        strip[30 - k] = value;
                    }
                    if (j < 15)
                    {
                        strip[j] = dim1;
                        strip[30 - j] = dim1;
                        if (j + 1 < 15)
                        {
                            strip[j + 1] = dim2;
                            strip[29 - j] = dim2;
                        }
                    }
                    frames.Add(Map31(strip));
                }

                // Uniform afterglow fade.
                foreach (var level in new[] { 20, 10, 0 })
                {
                    var faded = Scale(Scale(color, level), bright);
                    var ring = NewStrip(LedCount);
                    for (var i = 0; i < LedCount; i++) ring[i] = faded;
                    frames.Add(FromRing(ring));
                    frames.Add(FromRing(ring));
                }
            }
        }

        private static void BuildElectricCurrent(List<byte[]> frames, IReadOnlyList<(byte R, byte G, byte B)> colors, int bright)
        {
            // A bolt grows from both ends with a white-hot base, then retracts.
            var white = Scale(((byte)255, (byte)255, (byte)255), bright);

            for (var c = 0; c < 4; c++)
            {
                var value = Scale(ColorAt(colors, c), bright);

                void AddBolt(int length, int repeats = 1)
                {
                    var strip = NewStrip(31);
                    var whiteLength = Math.Max(0, length - 6);
                    for (var k = 0; k < length && k < 16; k++)
                    {
                        var color = k < whiteLength ? white : value;
                        strip[k] = color;
                        strip[30 - k] = color;
                    }
                    var frame = Map31(strip);
                    for (var r = 0; r < repeats; r++) frames.Add(frame);
                }

                // Grow with an initial hold (the vendor lingers on the first spark).
                AddBolt(1, 5);
                for (var length = 2; length <= 15; length++) AddBolt(length);

                // Crackle at full extension.
                AddBolt(15, 2);
                frames.Add(new byte[LedCount * 3]);
                AddBolt(15, 2);
                frames.Add(new byte[LedCount * 3]);
                AddBolt(15, 2);

                // Retract.
                for (var length = 14; length >= 1; length--) AddBolt(length);
                frames.Add(new byte[LedCount * 3]);
            }
        }

        private static void BuildRainbowWave(List<byte[]> frames, int bright, bool reverse)
        {
            // Smooth 60-position wheel scrolling at double speed through a
            // repeating 5-off / 7-on mask.
            byte[] mask = { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1 };

            for (var shift = 0; shift < 60; shift++)
            {
                var ring = NewStrip(LedCount);
                for (var j = 0; j < 60; j++)
                {
                    var visible = mask[(shift + j) % 12] != 0;
                    var value = visible
                        ? Scale(Wheel(((shift + 2 * j) % 60) / 60.0), bright)
                        : default;
                    var pos = reverse ? j : 60 - j - 1;
                    ring[pos] = value;
                }
                frames.Add(FromRing(ring));
            }
        }
    }
}
