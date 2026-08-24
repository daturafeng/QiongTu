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
        Assert.HasCount(9, result.Checks);
        Assert.IsTrue(result.Checks.Any(check => check.Contains("capability", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.Checks.Any(check => check.Contains("isolated child", StringComparison.OrdinalIgnoreCase)));
    }
}
