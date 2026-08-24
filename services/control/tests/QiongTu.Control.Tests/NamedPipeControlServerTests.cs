using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class NamedPipeControlServerTests
{
    [TestMethod]
    public void OversizedResponseIsReplacedWithBoundedStructuredFailure()
    {
        var response = new ControlResponse(
            ContractVersions.ControlApiV1,
            "request-1",
            true,
            new { payload = new string('界', NamedPipeControlServer.MaximumResponseBytes) },
            null);

        var json = NamedPipeControlServer.SerializeBoundedResponse(response);

        Assert.IsLessThanOrEqualTo(NamedPipeControlServer.MaximumResponseBytes, Encoding.UTF8.GetByteCount(json));
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual("request-1", document.RootElement.GetProperty("requestId").GetString());
        Assert.AreEqual(
            "response_too_large",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public void NormalResponseIsPreserved()
    {
        var response = new ControlResponse(
            ContractVersions.ControlApiV1,
            "request-2",
            true,
            new { value = 42 },
            null);

        var json = NamedPipeControlServer.SerializeBoundedResponse(response);

        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(42, document.RootElement.GetProperty("result").GetProperty("value").GetInt32());
    }
}
