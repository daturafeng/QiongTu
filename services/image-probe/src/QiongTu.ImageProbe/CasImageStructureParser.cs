using System.Buffers.Binary;

namespace QiongTu.ImageProbe;

internal sealed class CasImageStructureException : IOException
{
    public CasImageStructureException(string code)
        : base("The image container structure is invalid or unsupported.")
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed record CasImageFrameStructure(
    int FrameIndex,
    string FrameKind,
    long ByteOffset,
    long ByteLength,
    int Width,
    int Height,
    int BitsPerChannel,
    int? Orientation);

internal sealed record CasImageStructure(
    string Container,
    IReadOnlyList<CasImageFrameStructure> Frames);

internal static class CasImageStructureParser
{
    private const ushort TiffTagImageWidth = 256;
    private const ushort TiffTagImageHeight = 257;
    private const ushort TiffTagBitsPerSample = 258;
    private const ushort TiffTagCompression = 259;
    private const ushort TiffTagPhotometric = 262;
    private const ushort TiffTagStripOffsets = 273;
    private const ushort TiffTagOrientation = 274;
    private const ushort TiffTagStripByteCounts = 279;
    private const ushort TiffTagTileOffsets = 324;
    private const ushort TiffTagTileByteCounts = 325;
    private const ushort MpfTagNumberOfImages = 0xb001;
    private const ushort MpfTagEntries = 0xb002;

    public static CasImageStructure Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("CAS image parsing requires a readable seekable stream.", nameof(stream));
        }

        if (stream.Length < 4)
        {
            throw new CasImageStructureException("unsupported_image_container");
        }

        Span<byte> signature = stackalloc byte[4];
        ReadExactlyAt(stream, 0, signature, "container_header_truncated");
        if (signature[0] == 0xff && signature[1] == 0xd8)
        {
            return ParseJpegOrMpo(stream);
        }

        if ((signature[0] == (byte)'I' && signature[1] == (byte)'I') ||
            (signature[0] == (byte)'M' && signature[1] == (byte)'M'))
        {
            return ParseTiff(stream, signature);
        }

        throw new CasImageStructureException("unsupported_image_container");
    }

    private static CasImageStructure ParseJpegOrMpo(Stream stream)
    {
        var first = ParseJpegFrame(stream, 0, stream.Length, allowTrailingBytes: true, collectMpf: true);
        if (first.Mpf is null)
        {
            if (first.EndOffset != stream.Length)
            {
                throw new CasImageStructureException("jpeg_trailing_data");
            }

            return new CasImageStructure(
                "jpeg",
                [new CasImageFrameStructure(0, "jpeg", 0, first.EndOffset, first.Width, first.Height, first.BitsPerChannel, first.Orientation)]);
        }

        var entries = ParseMpfEntries(stream, first.Mpf, stream.Length);
        var frames = new List<CasImageFrameStructure>(entries.Count);
        long totalPixels = 0;
        long totalMetadataBytes = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var parsed = ParseJpegFrame(
                stream,
                entry.Offset,
                checked(entry.Offset + entry.Length),
                allowTrailingBytes: false,
                collectMpf: false);
            frames.Add(new CasImageFrameStructure(
                index,
                index == 0 ? "mp_primary_image" : "mp_auxiliary_image",
                entry.Offset,
                entry.Length,
                parsed.Width,
                parsed.Height,
                parsed.BitsPerChannel,
                parsed.Orientation));
            totalPixels = checked(totalPixels + ((long)parsed.Width * parsed.Height));
            if (totalPixels > QiongTu.Contracts.ImageProbeProtocol.MaximumCasTotalPixels)
            {
                throw new CasImageStructureException("total_pixel_limit_exceeded");
            }

            totalMetadataBytes = checked(totalMetadataBytes + parsed.MetadataBytes);
            if (totalMetadataBytes > QiongTu.Contracts.ImageProbeProtocol.MaximumCasMetadataBytes)
            {
                throw new CasImageStructureException("jpeg_metadata_limit_exceeded");
            }
        }

        return new CasImageStructure("mpo", frames);
    }

    private static ParsedJpegFrame ParseJpegFrame(
        Stream stream,
        long startOffset,
        long exclusiveEnd,
        bool allowTrailingBytes,
        bool collectMpf)
    {
        if (startOffset < 0 || exclusiveEnd > stream.Length || exclusiveEnd - startOffset < 4)
        {
            throw new CasImageStructureException("jpeg_range_out_of_bounds");
        }

        Span<byte> soi = stackalloc byte[2];
        ReadExactlyAt(stream, startOffset, soi, "jpeg_truncated");
        if (soi[0] != 0xff || soi[1] != 0xd8)
        {
            throw new CasImageStructureException("jpeg_soi_missing");
        }

        var cursor = new BoundedStreamCursor(stream, startOffset + 2, exclusiveEnd);
        var markerCount = 1;
        var sawStartOfFrame = false;
        var sawStartOfScan = false;
        int width = 0;
        int height = 0;
        int bitsPerChannel = 0;
        long metadataBytes = 0;
        MpfSegment? mpf = null;
        int? orientation = null;
        byte? pendingMarker = null;
        Span<byte> sof = stackalloc byte[5];
        Span<byte> identifier = stackalloc byte[4];
        Span<byte> exifIdentifier = stackalloc byte[6];

        while (pendingMarker is not null || cursor.Position < exclusiveEnd)
        {
            byte marker;
            if (pendingMarker is not null)
            {
                marker = pendingMarker.Value;
                pendingMarker = null;
            }
            else
            {
                marker = ReadMarker(cursor);
            }

            markerCount++;
            if (markerCount > QiongTu.Contracts.ImageProbeProtocol.MaximumCasMarkerCount)
            {
                throw new CasImageStructureException("jpeg_marker_limit_exceeded");
            }

            if (marker == 0xd9)
            {
                if (!sawStartOfFrame || !sawStartOfScan)
                {
                    throw new CasImageStructureException("jpeg_required_marker_missing");
                }

                if (!allowTrailingBytes && cursor.Position != exclusiveEnd)
                {
                    throw new CasImageStructureException("jpeg_range_length_mismatch");
                }

                return new ParsedJpegFrame(
                    cursor.Position,
                    width,
                    height,
                    bitsPerChannel,
                    metadataBytes,
                    mpf,
                    orientation ?? 1);
            }

            if (marker == 0xdc)
            {
                throw new CasImageStructureException("jpeg_dnl_not_supported");
            }

            if (marker == 0xd8 || marker is >= 0xd0 and <= 0xd7 || marker == 0x01)
            {
                throw new CasImageStructureException("jpeg_marker_order_invalid");
            }

            var segmentLength = cursor.ReadUInt16BigEndian("jpeg_segment_truncated");
            if (segmentLength < 2)
            {
                throw new CasImageStructureException("jpeg_segment_length_invalid");
            }

            var dataOffset = cursor.Position;
            var dataLength = segmentLength - 2;
            var segmentEnd = checked(dataOffset + dataLength);
            if (segmentEnd > exclusiveEnd)
            {
                throw new CasImageStructureException("jpeg_segment_out_of_bounds");
            }

            if (marker is >= 0xe0 and <= 0xef || marker == 0xfe)
            {
                metadataBytes = checked(metadataBytes + dataLength);
                if (metadataBytes > QiongTu.Contracts.ImageProbeProtocol.MaximumCasMetadataBytes)
                {
                    throw new CasImageStructureException("jpeg_metadata_limit_exceeded");
                }
            }

            if (IsStartOfFrame(marker))
            {
                if (dataLength < 6)
                {
                    throw new CasImageStructureException("jpeg_sof_invalid");
                }

                ReadExactlyAt(stream, dataOffset, sof, "jpeg_sof_invalid");
                var candidateBits = sof[0];
                var candidateHeight = BinaryPrimitives.ReadUInt16BigEndian(sof[1..3]);
                var candidateWidth = BinaryPrimitives.ReadUInt16BigEndian(sof[3..5]);
                if (candidateHeight == 0)
                {
                    throw new CasImageStructureException("jpeg_dnl_not_supported");
                }

                if (candidateBits == 0 || candidateWidth == 0)
                {
                    throw new CasImageStructureException("jpeg_dimensions_invalid");
                }

                if (sawStartOfFrame &&
                    (width != candidateWidth || height != candidateHeight || bitsPerChannel != candidateBits))
                {
                    throw new CasImageStructureException("jpeg_sof_conflict");
                }

                width = candidateWidth;
                height = candidateHeight;
                bitsPerChannel = candidateBits;
                sawStartOfFrame = true;
                ValidatePixelBudget(width, height);
            }
            else if (marker == 0xe2 && collectMpf && dataLength >= 4)
            {
                ReadExactlyAt(stream, dataOffset, identifier, "jpeg_app2_truncated");
                if (identifier.SequenceEqual("MPF\0"u8))
                {
                    if (mpf is not null)
                    {
                        throw new CasImageStructureException("mpf_multiple_indexes");
                    }

                    mpf = new MpfSegment(dataOffset + 4, dataLength - 4);
                }
            }
            else if (marker == 0xe1 && dataLength >= 6)
            {
                ReadExactlyAt(stream, dataOffset, exifIdentifier, "jpeg_app1_truncated");
                if (exifIdentifier.SequenceEqual("Exif\0\0"u8))
                {
                    var candidateOrientation = ParseJpegExifOrientation(
                        stream,
                        checked(dataOffset + 6),
                        dataLength - 6);
                    if (orientation is not null &&
                        candidateOrientation is not null &&
                        orientation != candidateOrientation)
                    {
                        throw new CasImageStructureException("jpeg_orientation_conflict");
                    }

                    orientation ??= candidateOrientation;
                }
            }

            cursor.Position = segmentEnd;
            if (marker != 0xda)
            {
                continue;
            }

            if (!sawStartOfFrame)
            {
                throw new CasImageStructureException("jpeg_sof_missing");
            }

            sawStartOfScan = true;
            pendingMarker = ScanEntropyToMarker(cursor);
        }

        throw new CasImageStructureException("jpeg_eoi_missing");
    }

    private static int? ParseJpegExifOrientation(Stream stream, long tiffOffset, long tiffLength)
    {
        const string invalidCode = "jpeg_exif_orientation_invalid";
        if (tiffLength < 8)
        {
            throw new CasImageStructureException(invalidCode);
        }

        Span<byte> byteOrder = stackalloc byte[2];
        ReadExactlyAt(stream, tiffOffset, byteOrder, invalidCode);
        var littleEndian = byteOrder.SequenceEqual("II"u8)
            ? true
            : byteOrder.SequenceEqual("MM"u8)
                ? false
                : throw new CasImageStructureException(invalidCode);
        var reader = new TiffReader(stream, tiffOffset, tiffLength, littleEndian);
        if (reader.ReadUInt16(2, invalidCode) != 42)
        {
            throw new CasImageStructureException(invalidCode);
        }

        var ifdOffset = reader.ReadUInt32(4, invalidCode);
        var entryCount = reader.ReadUInt16(ifdOffset, invalidCode);
        if (entryCount > QiongTu.Contracts.ImageProbeProtocol.MaximumCasIfdEntryCount)
        {
            throw new CasImageStructureException(invalidCode);
        }

        reader.RequireRange(
            ifdOffset,
            checked(2L + (entryCount * 12L) + 4L),
            invalidCode);
        int? orientation = null;
        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = checked((long)ifdOffset + 2L + (index * 12L));
            if (reader.ReadUInt16(entryOffset, invalidCode) != TiffTagOrientation)
            {
                continue;
            }

            var type = reader.ReadUInt16(entryOffset + 2, invalidCode);
            var count = reader.ReadUInt32(entryOffset + 4, invalidCode);
            if (type != 3 || count != 1)
            {
                throw new CasImageStructureException(invalidCode);
            }

            var candidate = checked((int)reader.ReadUInt16(entryOffset + 8, invalidCode));
            if (candidate is < 1 or > 8)
            {
                throw new CasImageStructureException(invalidCode);
            }

            if (orientation is not null && orientation != candidate)
            {
                throw new CasImageStructureException("jpeg_orientation_conflict");
            }

            orientation = candidate;
        }

        return orientation;
    }

    private static byte ReadMarker(BoundedStreamCursor cursor)
    {
        if (cursor.ReadByte("jpeg_marker_truncated") != 0xff)
        {
            throw new CasImageStructureException("jpeg_marker_prefix_missing");
        }

        byte marker;
        do
        {
            marker = cursor.ReadByte("jpeg_marker_truncated");
        }
        while (marker == 0xff);

        if (marker == 0x00)
        {
            throw new CasImageStructureException("jpeg_stuffed_byte_outside_scan");
        }

        return marker;
    }

    private static byte ScanEntropyToMarker(BoundedStreamCursor cursor)
    {
        while (cursor.Position < cursor.ExclusiveEnd)
        {
            if (cursor.ReadByte("jpeg_scan_truncated") != 0xff)
            {
                continue;
            }

            byte next;
            do
            {
                next = cursor.ReadByte("jpeg_scan_truncated");
            }
            while (next == 0xff);

            if (next == 0x00 || next is >= 0xd0 and <= 0xd7)
            {
                continue;
            }

            return next;
        }

        throw new CasImageStructureException("jpeg_eoi_missing");
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
        0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf;

    private static IReadOnlyList<MpfEntry> ParseMpfEntries(Stream stream, MpfSegment segment, long objectLength)
    {
        Span<byte> byteOrder = stackalloc byte[2];
        ReadExactlyAt(stream, segment.Offset, byteOrder, "mpf_header_invalid");
        var littleEndian = byteOrder.SequenceEqual("II"u8)
            ? true
            : byteOrder.SequenceEqual("MM"u8)
                ? false
                : throw new CasImageStructureException("mpf_header_invalid");
        var tiff = new TiffReader(stream, segment.Offset, segment.Length, littleEndian);
        var magic = tiff.ReadUInt16(2, "mpf_header_invalid");
        if (magic != 42)
        {
            throw new CasImageStructureException("mpf_header_invalid");
        }

        var ifdOffset = tiff.ReadUInt32(4, "mpf_ifd_out_of_bounds");
        var count = tiff.ReadUInt16(ifdOffset, "mpf_ifd_out_of_bounds");
        if (count > QiongTu.Contracts.ImageProbeProtocol.MaximumCasIfdEntryCount)
        {
            throw new CasImageStructureException("mpf_ifd_entry_limit_exceeded");
        }

        uint? numberOfImages = null;
        uint? entriesOffset = null;
        uint? entriesByteCount = null;
        var sawVersion = false;
        Span<byte> version = stackalloc byte[4];
        for (var index = 0; index < count; index++)
        {
            var entryOffset = checked((long)ifdOffset + 2 + (index * 12L));
            var tag = tiff.ReadUInt16(entryOffset, "mpf_ifd_out_of_bounds");
            var type = tiff.ReadUInt16(entryOffset + 2, "mpf_ifd_out_of_bounds");
            var valueCount = tiff.ReadUInt32(entryOffset + 4, "mpf_ifd_out_of_bounds");
            var valueOrOffset = tiff.ReadUInt32(entryOffset + 8, "mpf_ifd_out_of_bounds");
            if (tag == 0xb000)
            {
                if (sawVersion || type != 7 || valueCount != 4)
                {
                    throw new CasImageStructureException("mpf_version_invalid");
                }

                tiff.ReadBytes(entryOffset + 8, version, "mpf_version_invalid");
                if (!version.SequenceEqual("0100"u8))
                {
                    throw new CasImageStructureException("mpf_version_invalid");
                }

                sawVersion = true;
            }
            else if (tag == MpfTagNumberOfImages)
            {
                if (numberOfImages is not null || type != 4 || valueCount != 1)
                {
                    throw new CasImageStructureException("mpf_image_count_invalid");
                }

                numberOfImages = valueOrOffset;
            }
            else if (tag == MpfTagEntries)
            {
                if (entriesOffset is not null || type != 7 || valueCount == 0 || valueCount % 16 != 0)
                {
                    throw new CasImageStructureException("mpf_entries_invalid");
                }

                entriesOffset = valueOrOffset;
                entriesByteCount = valueCount;
            }
        }

        if (!sawVersion || numberOfImages is null || entriesOffset is null || entriesByteCount is null ||
            numberOfImages is < 2 or > QiongTu.Contracts.ImageProbeProtocol.MaximumCasFrameCount ||
            entriesByteCount != numberOfImages * 16)
        {
            throw new CasImageStructureException("mpf_image_count_invalid");
        }

        var result = new List<MpfEntry>((int)numberOfImages.Value);
        for (var index = 0; index < numberOfImages; index++)
        {
            var offset = checked((long)entriesOffset.Value + (index * 16L));
            var attribute = tiff.ReadUInt32(offset, "mpf_entries_out_of_bounds");
            if (((attribute >> 24) & 0x07) != 0)
            {
                throw new CasImageStructureException("mpf_image_format_not_supported");
            }

            var mpType = attribute & 0x00ff_ffff;
            if (mpType is not (0x000000 or 0x010001 or 0x010002 or 0x020001 or
                0x020002 or 0x020003 or 0x030000))
            {
                throw new CasImageStructureException("mpf_type_not_supported");
            }

            var size = tiff.ReadUInt32(offset + 4, "mpf_entries_out_of_bounds");
            var relativeDataOffset = tiff.ReadUInt32(offset + 8, "mpf_entries_out_of_bounds");
            var dependentImage1 = tiff.ReadUInt16(offset + 12, "mpf_entries_out_of_bounds");
            var dependentImage2 = tiff.ReadUInt16(offset + 14, "mpf_entries_out_of_bounds");
            ValidateDependentImage(dependentImage1, checked((int)numberOfImages.Value), index);
            ValidateDependentImage(dependentImage2, checked((int)numberOfImages.Value), index);
            var absoluteDataOffset = index == 0 && relativeDataOffset == 0
                ? 0L
                : checked(segment.Offset + relativeDataOffset);
            if (index == 0 && relativeDataOffset != 0)
            {
                throw new CasImageStructureException("mpf_primary_offset_invalid");
            }

            if (size == 0 || absoluteDataOffset < 0 || absoluteDataOffset > objectLength - size)
            {
                throw new CasImageStructureException("mpf_range_out_of_bounds");
            }

            result.Add(new MpfEntry(absoluteDataOffset, size));
        }

        var ordered = result.OrderBy(entry => entry.Offset).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].Offset < checked(ordered[index - 1].Offset + ordered[index - 1].Length))
            {
                throw new CasImageStructureException("mpf_ranges_overlap");
            }
        }

        if (ordered[0].Offset != 0 ||
            checked(ordered[^1].Offset + ordered[^1].Length) != objectLength)
        {
            throw new CasImageStructureException("mpf_unreferenced_trailing_data");
        }

        return result;
    }

    private static void ValidateDependentImage(ushort dependentImage, int imageCount, int zeroBasedIndex)
    {
        if (dependentImage > imageCount || dependentImage == zeroBasedIndex + 1)
        {
            throw new CasImageStructureException("mpf_dependency_invalid");
        }
    }

    private static CasImageStructure ParseTiff(Stream stream, ReadOnlySpan<byte> signature)
    {
        var littleEndian = signature[0] == (byte)'I';
        var magic = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(signature[2..4])
            : BinaryPrimitives.ReadUInt16BigEndian(signature[2..4]);
        if (magic == 43)
        {
            throw new CasImageStructureException("bigtiff_not_supported");
        }

        if (magic != 42 || stream.Length < 8)
        {
            throw new CasImageStructureException("tiff_header_invalid");
        }

        var reader = new TiffReader(stream, 0, stream.Length, littleEndian);
        var nextIfd = reader.ReadUInt32(4, "tiff_ifd_out_of_bounds");
        var visited = new HashSet<uint>();
        var frames = new List<CasImageFrameStructure>();
        long totalPixels = 0;
        long metadataBytes = 8;

        while (nextIfd != 0)
        {
            if (frames.Count >= QiongTu.Contracts.ImageProbeProtocol.MaximumCasFrameCount)
            {
                throw new CasImageStructureException("tiff_page_limit_exceeded");
            }

            if (!visited.Add(nextIfd))
            {
                throw new CasImageStructureException("tiff_ifd_cycle");
            }

            var entryCount = reader.ReadUInt16(nextIfd, "tiff_ifd_out_of_bounds");
            if (entryCount > QiongTu.Contracts.ImageProbeProtocol.MaximumCasIfdEntryCount)
            {
                throw new CasImageStructureException("tiff_ifd_entry_limit_exceeded");
            }

            metadataBytes = checked(metadataBytes + 2 + (entryCount * 12L) + 4);
            if (metadataBytes > QiongTu.Contracts.ImageProbeProtocol.MaximumCasMetadataBytes)
            {
                throw new CasImageStructureException("tiff_metadata_limit_exceeded");
            }

            var values = new Dictionary<ushort, TiffValue>();
            for (var index = 0; index < entryCount; index++)
            {
                var entryOffset = checked((long)nextIfd + 2 + (index * 12L));
                var tag = reader.ReadUInt16(entryOffset, "tiff_ifd_out_of_bounds");
                var type = reader.ReadUInt16(entryOffset + 2, "tiff_ifd_out_of_bounds");
                var count = reader.ReadUInt32(entryOffset + 4, "tiff_ifd_out_of_bounds");
                var valueOffset = entryOffset + 8;
                var byteCount = checked((long)GetTiffTypeSize(type) * count);
                if (byteCount <= 0 || byteCount > QiongTu.Contracts.ImageProbeProtocol.MaximumCasMetadataBytes)
                {
                    throw new CasImageStructureException("tiff_metadata_limit_exceeded");
                }

                var dataOffset = byteCount <= 4
                    ? valueOffset
                    : reader.ReadUInt32(valueOffset, "tiff_value_out_of_bounds");
                reader.RequireRange(dataOffset, byteCount, "tiff_value_out_of_bounds");
                metadataBytes = checked(metadataBytes + (byteCount <= 4 ? 0 : byteCount));
                if (metadataBytes > QiongTu.Contracts.ImageProbeProtocol.MaximumCasMetadataBytes)
                {
                    throw new CasImageStructureException("tiff_metadata_limit_exceeded");
                }

                if (tag is not (TiffTagImageWidth or TiffTagImageHeight or TiffTagBitsPerSample or TiffTagCompression or
                    TiffTagPhotometric or TiffTagStripOffsets or TiffTagOrientation or
                    TiffTagStripByteCounts or TiffTagTileOffsets or TiffTagTileByteCounts))
                {
                    continue;
                }

                if (!values.TryAdd(tag, new TiffValue(type, count, dataOffset)))
                {
                    throw new CasImageStructureException("tiff_duplicate_tag");
                }
            }

            var width = ReadRequiredPositiveInt(values, TiffTagImageWidth, reader, "tiff_width_invalid");
            var height = ReadRequiredPositiveInt(values, TiffTagImageHeight, reader, "tiff_height_invalid");
            ValidatePixelBudget(width, height);
            totalPixels = checked(totalPixels + ((long)width * height));
            if (totalPixels > QiongTu.Contracts.ImageProbeProtocol.MaximumCasTotalPixels)
            {
                throw new CasImageStructureException("total_pixel_limit_exceeded");
            }

            var bits = values.TryGetValue(TiffTagBitsPerSample, out var bitsValue)
                ? checked((int)ReadUnsignedMaximum(bitsValue, reader, "tiff_bits_invalid"))
                : 1;
            if (bits is <= 0 or > 64)
            {
                throw new CasImageStructureException("tiff_bits_invalid");
            }

            var compression = values.TryGetValue(TiffTagCompression, out var compressionValue)
                ? ReadUnsignedScalar(compressionValue, reader, "tiff_compression_invalid")
                : 1;
            if (compression is not (1 or 5 or 7 or 8 or 32_773 or 32_946))
            {
                throw new CasImageStructureException("tiff_compression_not_supported");
            }

            var photometric = values.TryGetValue(TiffTagPhotometric, out var photometricValue)
                ? ReadUnsignedScalar(photometricValue, reader, "tiff_photometric_invalid")
                : 0;
            if (photometric is not (0 or 1 or 2 or 3 or 6))
            {
                throw new CasImageStructureException("tiff_photometric_not_supported");
            }

            var orientation = values.TryGetValue(TiffTagOrientation, out var orientationValue)
                ? checked((int)ReadUnsignedScalar(orientationValue, reader, "tiff_orientation_invalid"))
                : 1;
            if (orientation is < 1 or > 8)
            {
                throw new CasImageStructureException("tiff_orientation_invalid");
            }

            ValidateTiffDataRanges(values, reader);
            frames.Add(new CasImageFrameStructure(
                frames.Count,
                "tiff_page",
                nextIfd,
                0,
                width,
                height,
                bits,
                orientation));

            var nextOffsetLocation = checked((long)nextIfd + 2 + (entryCount * 12L));
            nextIfd = reader.ReadUInt32(nextOffsetLocation, "tiff_ifd_out_of_bounds");
        }

        if (frames.Count == 0)
        {
            throw new CasImageStructureException("tiff_page_missing");
        }

        return new CasImageStructure("tiff", frames);
    }

    private static void ValidateTiffDataRanges(IReadOnlyDictionary<ushort, TiffValue> values, TiffReader reader)
    {
        var hasStripOffsets = values.TryGetValue(TiffTagStripOffsets, out var stripOffsets);
        var hasStripCounts = values.TryGetValue(TiffTagStripByteCounts, out var stripCounts);
        var hasTileOffsets = values.TryGetValue(TiffTagTileOffsets, out var tileOffsets);
        var hasTileCounts = values.TryGetValue(TiffTagTileByteCounts, out var tileCounts);
        if (hasStripOffsets != hasStripCounts || hasTileOffsets != hasTileCounts)
        {
            throw new CasImageStructureException("tiff_pixel_ranges_invalid");
        }

        var hasStrips = hasStripOffsets && hasStripCounts;
        var hasTiles = hasTileOffsets && hasTileCounts;
        if (hasStrips == hasTiles)
        {
            throw new CasImageStructureException("tiff_pixel_ranges_invalid");
        }

        var offsets = hasStrips ? stripOffsets! : tileOffsets!;
        var counts = hasStrips ? stripCounts! : tileCounts!;
        if (offsets.Count != counts.Count || offsets.Count == 0 ||
            offsets.Count > QiongTu.Contracts.ImageProbeProtocol.MaximumCasIfdEntryCount)
        {
            throw new CasImageStructureException("tiff_pixel_ranges_invalid");
        }

        var ranges = new List<(long Offset, long Length)>((int)offsets.Count);
        for (uint index = 0; index < offsets.Count; index++)
        {
            var offset = checked((long)ReadUnsigned(offsets, reader, index, "tiff_pixel_ranges_invalid"));
            var length = checked((long)ReadUnsigned(counts, reader, index, "tiff_pixel_ranges_invalid"));
            if (length <= 0)
            {
                throw new CasImageStructureException("tiff_pixel_ranges_invalid");
            }

            reader.RequireRange(offset, length, "tiff_pixel_range_out_of_bounds");
            ranges.Add((offset, length));
        }

        ranges.Sort((left, right) => left.Offset.CompareTo(right.Offset));
        for (var index = 1; index < ranges.Count; index++)
        {
            if (ranges[index].Offset < checked(ranges[index - 1].Offset + ranges[index - 1].Length))
            {
                throw new CasImageStructureException("tiff_pixel_ranges_overlap");
            }
        }
    }

    private static int ReadRequiredPositiveInt(
        IReadOnlyDictionary<ushort, TiffValue> values,
        ushort tag,
        TiffReader reader,
        string code)
    {
        if (!values.TryGetValue(tag, out var value) || value.Count != 1)
        {
            throw new CasImageStructureException(code);
        }

        var result = checked((int)ReadUnsignedScalar(value, reader, code));
        return result > 0 ? result : throw new CasImageStructureException(code);
    }

    private static ulong ReadUnsignedScalar(TiffValue value, TiffReader reader, string code)
    {
        if (value.Count != 1)
        {
            throw new CasImageStructureException(code);
        }

        return ReadUnsigned(value, reader, 0, code);
    }

    private static ulong ReadUnsignedMaximum(TiffValue value, TiffReader reader, string code)
    {
        if (value.Count == 0 || value.Count > QiongTu.Contracts.ImageProbeProtocol.MaximumCasIfdEntryCount)
        {
            throw new CasImageStructureException(code);
        }

        ulong maximum = 0;
        for (uint index = 0; index < value.Count; index++)
        {
            maximum = Math.Max(maximum, ReadUnsigned(value, reader, index, code));
        }

        return maximum;
    }

    private static ulong ReadUnsigned(TiffValue value, TiffReader reader, uint index, string code)
    {
        if (index >= value.Count)
        {
            throw new CasImageStructureException(code);
        }

        return value.Type switch
        {
            1 => reader.ReadByte(checked(value.DataOffset + index), code),
            3 => reader.ReadUInt16(checked(value.DataOffset + (index * 2L)), code),
            4 => reader.ReadUInt32(checked(value.DataOffset + (index * 4L)), code),
            _ => throw new CasImageStructureException(code)
        };
    }

    private static int GetTiffTypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => throw new CasImageStructureException("tiff_field_type_invalid")
    };

    private static void ValidatePixelBudget(int width, int height)
    {
        var pixels = checked((long)width * height);
        if (pixels > QiongTu.Contracts.ImageProbeProtocol.MaximumCasPixelsPerFrame)
        {
            throw new CasImageStructureException("frame_pixel_limit_exceeded");
        }
    }

    private static void ReadExactlyAt(Stream stream, long offset, Span<byte> destination, string code)
    {
        if (offset < 0 || offset > stream.Length - destination.Length)
        {
            throw new CasImageStructureException(code);
        }

        stream.Position = offset;
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                throw new CasImageStructureException(code);
            }

            total += read;
        }
    }

    private sealed record ParsedJpegFrame(
        long EndOffset,
        int Width,
        int Height,
        int BitsPerChannel,
        long MetadataBytes,
        MpfSegment? Mpf,
        int Orientation);

    private sealed record MpfSegment(long Offset, long Length);

    private sealed record MpfEntry(long Offset, long Length);

    private sealed record TiffValue(ushort Type, uint Count, long DataOffset);

    private sealed class BoundedStreamCursor
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[128 * 1024];
        private long _position;
        private long _bufferStart = -1;
        private int _bufferLength;

        public BoundedStreamCursor(Stream stream, long start, long exclusiveEnd)
        {
            _stream = stream;
            _position = start;
            ExclusiveEnd = exclusiveEnd;
        }

        public long ExclusiveEnd { get; }

        public long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > ExclusiveEnd)
                {
                    throw new CasImageStructureException("jpeg_range_out_of_bounds");
                }

                _position = value;
            }
        }

        public byte ReadByte(string code)
        {
            if (_position >= ExclusiveEnd)
            {
                throw new CasImageStructureException(code);
            }

            if (_bufferStart < 0 || _position < _bufferStart || _position >= _bufferStart + _bufferLength)
            {
                Fill(code);
            }

            var value = _buffer[checked((int)(_position - _bufferStart))];
            _position++;
            return value;
        }

        public ushort ReadUInt16BigEndian(string code)
        {
            var high = ReadByte(code);
            var low = ReadByte(code);
            return (ushort)((high << 8) | low);
        }

        private void Fill(string code)
        {
            _stream.Position = _position;
            _bufferStart = _position;
            _bufferLength = 0;
            var maximum = checked((int)Math.Min(_buffer.Length, ExclusiveEnd - _position));
            while (_bufferLength < maximum)
            {
                var read = _stream.Read(_buffer, _bufferLength, maximum - _bufferLength);
                if (read == 0)
                {
                    break;
                }

                _bufferLength += read;
            }

            if (_bufferLength == 0)
            {
                throw new CasImageStructureException(code);
            }
        }
    }

    private sealed class TiffReader
    {
        private readonly Stream _stream;
        private readonly long _baseOffset;
        private readonly long _length;
        private readonly bool _littleEndian;

        public TiffReader(Stream stream, long baseOffset, long length, bool littleEndian)
        {
            _stream = stream;
            _baseOffset = baseOffset;
            _length = length;
            _littleEndian = littleEndian;
        }

        public byte ReadByte(long relativeOffset, string code)
        {
            Span<byte> value = stackalloc byte[1];
            Read(relativeOffset, value, code);
            return value[0];
        }

        public ushort ReadUInt16(long relativeOffset, string code)
        {
            Span<byte> value = stackalloc byte[2];
            Read(relativeOffset, value, code);
            return _littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(value)
                : BinaryPrimitives.ReadUInt16BigEndian(value);
        }

        public uint ReadUInt32(long relativeOffset, string code)
        {
            Span<byte> value = stackalloc byte[4];
            Read(relativeOffset, value, code);
            return _littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(value)
                : BinaryPrimitives.ReadUInt32BigEndian(value);
        }

        public void ReadBytes(long relativeOffset, Span<byte> destination, string code) =>
            Read(relativeOffset, destination, code);

        public void RequireRange(long relativeOffset, long length, string code)
        {
            if (relativeOffset < 0 || length < 0 || relativeOffset > _length - length)
            {
                throw new CasImageStructureException(code);
            }
        }

        private void Read(long relativeOffset, Span<byte> destination, string code)
        {
            RequireRange(relativeOffset, destination.Length, code);
            ReadExactlyAt(_stream, checked(_baseOffset + relativeOffset), destination, code);
        }
    }
}
