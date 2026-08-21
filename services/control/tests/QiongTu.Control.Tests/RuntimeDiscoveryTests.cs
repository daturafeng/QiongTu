using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class RuntimeDiscoveryTests
{
    [TestMethod]
    public async Task DiscoveryFileContainsNoArtifactCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-discovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "control.json");
        try
        {
            var discovery = new ControlDiscovery(
                ContractVersions.ControlApiV1,
                "named-pipe",
                Environment.ProcessId,
                RuntimeDiscovery.CreatePipeName(),
                DateTimeOffset.UtcNow);
            await RuntimeDiscovery.WriteAtomicallyAsync(path, discovery, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            var json = await File.ReadAllTextAsync(path);
            var restored = JsonSerializer.Deserialize<ControlDiscovery>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.IsNotNull(restored);
            Assert.AreEqual(discovery.PipeName, restored.PipeName);
            Assert.IsFalse(
                bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf,
                "The discovery boundary must be UTF-8 without BOM so Node JSON.parse can read it.");
            Assert.IsFalse(json.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains("artifact", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
