using System.Text;

namespace QiongTu.Control.Tests;

internal static class SourcePreflightSyntheticFixture
{
    public const string PrivateDeviceMarker = "SyntheticPrivateDevice-DoNotPersist";
    public const string PrivateCoordinateMarker = "30.12345678";

    public static byte[] BareJpeg() => [0xff, 0xd8, 0xff, 0xd9];

    public static byte[] DjiExif() =>
        WithSegments(ExifSegment("DJI", PrivateDeviceMarker));

    public static byte[] OtherManufacturerExif() =>
        WithSegments(ExifSegment("Other Camera Corp", PrivateDeviceMarker));

    public static byte[] ConflictingManufacturerAndDjiXmp() =>
        WithSegments(
            ExifSegment("Other Camera Corp", PrivateDeviceMarker),
            DjiXmpSegment());

    public static byte[] UnreadableExifAndDjiXmp() =>
        WithSegments(MalformedExifSegment(), DjiXmpSegment());

    public static byte[] DjiXmp() => WithSegments(DjiXmpSegment());

    public static byte[] DjiEvidenceExceedingInputLimit(int maximumPayloadBytes)
    {
        var prefix = DjiXmp();
        var payload = new byte[checked(maximumPayloadBytes + 1)];
        prefix.CopyTo(payload, 0);
        return payload;
    }

    public static byte[] DjiMrk(int imageCount, bool duplicateFirstSequence = false)
    {
        var lines = new List<string>(imageCount);
        for (var index = 1; index <= imageCount; index++)
        {
            var sequence = duplicateFirstSequence && index == imageCount ? 1 : index;
            lines.Add(
                $"{sequence}\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t{PrivateCoordinateMarker},Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q");
        }

        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
    }

    public static byte[] Rinex() => Encoding.UTF8.GetBytes(
        "     3.04           OBSERVATION DATA    M                   RINEX VERSION / TYPE\r\n" +
        "                                                            END OF HEADER\r\n");

    public static byte[] Rtcm3() => [0xd3, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00];

    private static byte[] WithSegments(params byte[][] segments)
    {
        using var stream = new MemoryStream();
        stream.Write([0xff, 0xd8]);
        foreach (var segment in segments)
        {
            stream.Write(segment);
        }

        stream.Write([0xff, 0xd9]);
        return stream.ToArray();
    }

    private static byte[] ExifSegment(string make, string model)
    {
        var makeBytes = NullTerminatedAscii(make);
        var modelBytes = NullTerminatedAscii(model);
        const int ifdOffset = 8;
        const int entryCount = 2;
        var dataOffset = ifdOffset + 2 + (entryCount * 12) + 4;
        using var tiff = new MemoryStream();
        tiff.Write("II"u8);
        WriteUInt16(tiff, 42);
        WriteUInt32(tiff, ifdOffset);
        WriteUInt16(tiff, entryCount);
        WriteAsciiEntry(tiff, 0x010f, makeBytes, dataOffset);
        WriteAsciiEntry(tiff, 0x0110, modelBytes, dataOffset + makeBytes.Length);
        WriteUInt32(tiff, 0);
        tiff.Write(makeBytes);
        tiff.Write(modelBytes);
        return AppSegment(0xe1, Combine("Exif\0\0"u8.ToArray(), tiff.ToArray()));
    }

    private static byte[] DjiXmpSegment()
    {
        var identifier = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
        var xml = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:drone-dji=\"http://www.dji.com/drone-dji/1.0/\" drone-dji:ProductName=\"" +
            PrivateDeviceMarker + "\" drone-dji:GpsLatitude=\"" + PrivateCoordinateMarker +
            "\" drone-dji:GimbalYawDegree=\"1\"/></rdf:RDF></x:xmpmeta>");
        return AppSegment(0xe1, Combine(identifier, xml));
    }

    private static byte[] MalformedExifSegment() =>
        AppSegment(0xe1, Combine(
            "Exif\0\0"u8.ToArray(),
            new byte[] { (byte)'I', (byte)'I', 42, 0, 0xff, 0xff, 0xff, 0x7f }));

    private static byte[] AppSegment(byte marker, byte[] payload)
    {
        var segmentLength = checked(payload.Length + 2);
        return Combine(
            [0xff, marker, (byte)(segmentLength >> 8), (byte)segmentLength],
            payload);
    }

    private static byte[] NullTerminatedAscii(string value) =>
        Encoding.ASCII.GetBytes(value + "\0");

    private static void WriteAsciiEntry(Stream stream, ushort tag, byte[] value, int dataOffset)
    {
        WriteUInt16(stream, tag);
        WriteUInt16(stream, 2);
        WriteUInt32(stream, value.Length);
        if (value.Length <= 4)
        {
            stream.Write(value);
            for (var index = value.Length; index < 4; index++)
            {
                stream.WriteByte(0);
            }
        }
        else
        {
            WriteUInt32(stream, dataOffset);
        }
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteUInt32(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static byte[] Combine(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
