namespace Worklapse.Video;

/// <summary>
/// The parts of an AV1 bitstream an MP4 file needs: the OBUs (open bitstream units) of a compressed frame, and the
/// fields of the sequence header that describe the stream (AV1 specification, sections 5.3 and 5.5).
/// </summary>
public static class Av1Obu
{
    public const int SequenceHeader = 1;
    public const int TemporalDelimiter = 2;

    /// <summary>One OBU of a frame: its type, and where the whole of it (header included) lies in the frame.</summary>
    public readonly record struct Unit(int Type, int Offset, int Length);

    /// <summary>
    /// Splits a compressed frame (libaom's "low overhead" format: every OBU has its size) into its OBUs.
    /// </summary>
    public static List<Unit> Split(ReadOnlySpan<byte> frame)
    {
        var units = new List<Unit>();
        var position = 0;
        while (position < frame.Length)
        {
            var header = frame[position];
            var type = (header >> 3) & 0xF;
            var hasExtension = (header & 0x4) != 0;
            var hasSize = (header & 0x2) != 0;
            var payloadStart = position + 1 + (hasExtension ? 1 : 0);
            int payloadLength;
            if (hasSize)
            {
                var (size, sizeBytes) = ReadLeb128(frame, payloadStart);
                payloadStart += sizeBytes;
                payloadLength = checked((int)size);
            }
            else
            {
                payloadLength = frame.Length - payloadStart; // the last OBU may leave its size out
            }
            if (payloadStart + payloadLength > frame.Length)
                throw new InvalidDataException("An AV1 OBU runs past the end of its frame.");
            units.Add(new Unit(type, position, payloadStart + payloadLength - position));
            position = payloadStart + payloadLength;
        }
        return units;
    }

    /// <summary>The payload of an OBU (what follows its header and size).</summary>
    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame, Unit unit)
    {
        var obu = frame.Slice(unit.Offset, unit.Length);
        var start = 1 + ((obu[0] & 0x4) != 0 ? 1 : 0);
        if ((obu[0] & 0x2) != 0)
            start += ReadLeb128(obu, start).Bytes;
        return obu[start..];
    }

    private static (ulong Value, int Bytes) ReadLeb128(ReadOnlySpan<byte> data, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            if (offset + i >= data.Length)
                throw new InvalidDataException("An AV1 OBU size is cut short.");
            var b = data[offset + i];
            value |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
                return (value, i + 1);
        }
        throw new InvalidDataException("An AV1 OBU size is longer than 8 bytes.");
    }
}

/// <summary>What an MP4 file's av1C and colr boxes say about a stream, read from its sequence header.</summary>
public sealed record Av1SequenceInfo(
    int Profile,
    int Level,
    int Tier,
    bool HighBitDepth,
    bool TwelveBit,
    bool Monochrome,
    bool SubsamplingX,
    bool SubsamplingY,
    int ChromaSamplePosition,
    int ColorPrimaries,
    int TransferCharacteristics,
    int MatrixCoefficients,
    bool FullRange)
{
    // Colour description values meaning "unspecified", and the combination that signals sRGB (4:4:4 RGB).
    private const int Unspecified = 2;
    private const int PrimariesBt709 = 1;
    private const int TransferSrgb = 13;
    private const int MatrixIdentity = 0;

    /// <summary>Reads the fields of a sequence header OBU's payload, as far as the colour configuration.</summary>
    public static Av1SequenceInfo Parse(ReadOnlySpan<byte> payload)
    {
        var bits = new BitReader(payload);
        var profile = bits.Read(3);
        bits.Read(1); // still_picture
        var reducedStillPictureHeader = bits.Read(1) == 1;

        int level, tier = 0;
        if (reducedStillPictureHeader)
        {
            level = bits.Read(5);
        }
        else
        {
            var decoderModelInfoPresent = false;
            var bufferDelayLength = 0;
            if (bits.Read(1) == 1) // timing_info_present_flag
            {
                bits.Read(32); // num_units_in_display_tick
                bits.Read(32); // time_scale
                if (bits.Read(1) == 1) // equal_picture_interval
                    bits.ReadUvlc(); // num_ticks_per_picture_minus_1
                decoderModelInfoPresent = bits.Read(1) == 1;
                if (decoderModelInfoPresent)
                {
                    bufferDelayLength = bits.Read(5) + 1;
                    bits.Read(32); // num_units_in_decoding_tick
                    bits.Read(5);  // buffer_removal_time_length_minus_1
                    bits.Read(5);  // frame_presentation_time_length_minus_1
                }
            }
            var initialDisplayDelayPresent = bits.Read(1) == 1;
            var operatingPoints = bits.Read(5) + 1;
            level = 0;
            for (var i = 0; i < operatingPoints; i++)
            {
                bits.Read(12); // operating_point_idc
                var opLevel = bits.Read(5);
                var opTier = opLevel > 7 ? bits.Read(1) : 0;
                if (i == 0)
                    (level, tier) = (opLevel, opTier);
                if (decoderModelInfoPresent && bits.Read(1) == 1)
                {
                    bits.Read(bufferDelayLength); // decoder_buffer_delay
                    bits.Read(bufferDelayLength); // encoder_buffer_delay
                    bits.Read(1);                 // low_delay_mode_flag
                }
                if (initialDisplayDelayPresent && bits.Read(1) == 1)
                    bits.Read(4); // initial_display_delay_minus_1
            }
        }

        var widthBits = bits.Read(4) + 1;
        var heightBits = bits.Read(4) + 1;
        bits.Read(widthBits);  // max_frame_width_minus_1
        bits.Read(heightBits); // max_frame_height_minus_1
        if (!reducedStillPictureHeader && bits.Read(1) == 1) // frame_id_numbers_present_flag
        {
            bits.Read(4); // delta_frame_id_length_minus_2
            bits.Read(3); // additional_frame_id_length_minus_1
        }
        bits.Read(1); // use_128x128_superblock
        bits.Read(1); // enable_filter_intra
        bits.Read(1); // enable_intra_edge_filter
        if (!reducedStillPictureHeader)
        {
            bits.Read(1); // enable_interintra_compound
            bits.Read(1); // enable_masked_compound
            bits.Read(1); // enable_warped_motion
            bits.Read(1); // enable_dual_filter
            var enableOrderHint = bits.Read(1) == 1;
            if (enableOrderHint)
            {
                bits.Read(1); // enable_jnt_comp
                bits.Read(1); // enable_ref_frame_mvs
            }
            var forceScreenContentTools = bits.Read(1) == 1 ? 2 : bits.Read(1); // seq_choose_screen_content_tools
            if (forceScreenContentTools > 0 && bits.Read(1) == 0) // seq_choose_integer_mv
                bits.Read(1); // seq_force_integer_mv
            if (enableOrderHint)
                bits.Read(3); // order_hint_bits_minus_1
        }
        bits.Read(1); // enable_superres
        bits.Read(1); // enable_cdef
        bits.Read(1); // enable_restoration

        // color_config()
        var highBitDepth = bits.Read(1) == 1;
        var twelveBit = profile == 2 && highBitDepth && bits.Read(1) == 1;
        var monochrome = profile != 1 && bits.Read(1) == 1;
        int primaries = Unspecified, transfer = Unspecified, matrix = Unspecified;
        if (bits.Read(1) == 1) // color_description_present_flag
        {
            primaries = bits.Read(8);
            transfer = bits.Read(8);
            matrix = bits.Read(8);
        }

        bool fullRange, subsamplingX, subsamplingY;
        var chromaSamplePosition = 0;
        if (monochrome)
        {
            fullRange = bits.Read(1) == 1;
            subsamplingX = subsamplingY = true;
        }
        else if (primaries == PrimariesBt709 && transfer == TransferSrgb && matrix == MatrixIdentity)
        {
            fullRange = true;
            subsamplingX = subsamplingY = false;
        }
        else
        {
            fullRange = bits.Read(1) == 1;
            if (profile == 0)
            {
                subsamplingX = subsamplingY = true;
            }
            else if (profile == 1)
            {
                subsamplingX = subsamplingY = false;
            }
            else if (twelveBit)
            {
                subsamplingX = bits.Read(1) == 1;
                subsamplingY = subsamplingX && bits.Read(1) == 1;
            }
            else
            {
                subsamplingX = true;
                subsamplingY = false;
            }
            if (subsamplingX && subsamplingY)
                chromaSamplePosition = bits.Read(2);
        }

        return new Av1SequenceInfo(profile, level, tier, highBitDepth, twelveBit, monochrome, subsamplingX, subsamplingY,
            chromaSamplePosition, primaries, transfer, matrix, fullRange);
    }

    /// <summary>Reads bits most significant first, as the AV1 specification's f(n).</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public int Read(int count)
        {
            long value = 0;
            for (var i = 0; i < count; i++)
            {
                var index = _position >> 3;
                if (index >= _data.Length)
                    throw new InvalidDataException("The AV1 sequence header is cut short.");
                value = (value << 1) | (uint)((_data[index] >> (7 - (_position & 7))) & 1);
                _position++;
            }
            return (int)value; // only 32-bit fields are wider than 31 bits, and their values aren't used
        }

        public void ReadUvlc()
        {
            var leadingZeros = 0;
            while (Read(1) == 0)
            {
                if (++leadingZeros >= 32)
                    return; // the largest value; nothing more to read
            }
            Read(leadingZeros);
        }
    }
}
