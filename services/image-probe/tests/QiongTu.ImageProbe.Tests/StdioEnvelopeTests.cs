using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe.Tests;

[TestClass]
public sealed class StdioEnvelopeTests
{
    [TestMethod]
    public async Task ReadHeaderAndPayload_UsesExactBoundaries()
    {
        var header = new ImageProbeRequestHeader(
            ImageProbeProtocol.SourcePreflightV1,
            ImageProbeProtocol.SourcePreflightProfile,
            "image_candidate",
            null,
            null,
            4);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var stream = new MemoryStream();
        stream.Write(headerBytes);
        stream.WriteByte((byte)'\n');
        stream.Write([1, 2, 3, 4]);
        stream.Position = 0;

        var readHeader = await StdioEnvelope.ReadHeaderLineAsync(
            stream,
            ImageProbeProtocol.MaximumHeaderBytes,
            CancellationToken.None);
        var parsed = JsonSerializer.Deserialize<ImageProbeRequestHeader>(
            readHeader,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(parsed);
        StdioEnvelope.ValidateHeader(parsed);
        var payload = new byte[4];
        await StdioEnvelope.ReadExactlyAsync(stream, payload, CancellationToken.None);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, payload);
        Assert.IsFalse(await StdioEnvelope.HasTrailingDataAsync(stream, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadHeaderLine_RejectsOversizedHeader()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 32) + "\n"));

        var exception = await Assert.ThrowsAsync<ImageProbeProtocolException>(() =>
            StdioEnvelope.ReadHeaderLineAsync(stream, 8, CancellationToken.None));

        Assert.AreEqual("header_too_large", exception.Code);
    }

    [TestMethod]
    public void ValidateHeader_RejectsOversizedPayload()
    {
        var header = new ImageProbeRequestHeader(
            ImageProbeProtocol.SourcePreflightV1,
            ImageProbeProtocol.SourcePreflightProfile,
            "image_candidate",
            null,
            null,
            ImageProbeProtocol.MaximumPayloadBytes + 1);

        var exception = Assert.Throws<ImageProbeProtocolException>(() =>
            StdioEnvelope.ValidateHeader(header));

        Assert.AreEqual("payload_size_out_of_range", exception.Code);
    }
}
