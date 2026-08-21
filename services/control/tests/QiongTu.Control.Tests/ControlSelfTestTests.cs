using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ControlSelfTestTests
{
    [TestMethod]
    public void SelfTestDeclaresNamedPipeBoundaryAndNoLanBinding()
    {
        var result = Program.CreateSelfTestResult();

        Assert.AreEqual(ContractVersions.ControlApiV1, result.ApiVersion);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual("named-pipe", result.Boundary.EndpointKind);
        Assert.IsFalse(result.Boundary.LanBindingAllowed);
        Assert.HasCount(6, result.Checks);
    }
}
