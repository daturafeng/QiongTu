using System.Net;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ArtifactServerTests
{
    [TestMethod]
    public async Task ServesOnlyRegisteredFilesWithTheSessionBearerToken()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var artifactRoot = Path.Combine(testRoot, "artifacts");
            Directory.CreateDirectory(artifactRoot);
            await File.WriteAllTextAsync(Path.Combine(artifactRoot, "tileset.json"), "{\"asset\":{\"version\":\"1.1\"}}");
            await File.WriteAllTextAsync(Path.Combine(testRoot, "outside.txt"), "not-public");

            var registry = new ArtifactRootRegistry();
            registry.RegisterTrustedRoot("result", artifactRoot);
            await using var server = new ArtifactServer(registry);
            await server.StartAsync(CancellationToken.None);
            var session = server.CreateSession();

            Assert.StartsWith("http://127.0.0.1:", session.BaseUrl);
            using var client = new HttpClient();
            var target = $"{session.BaseUrl}/artifacts/result/tileset.json";
            using var unauthorized = await client.GetAsync(target);
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token");
            using var wrongToken = await client.GetAsync(target);
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrongToken.StatusCode);

            client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
            using var allowed = await client.GetAsync(target);
            Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
            StringAssert.Contains(await allowed.Content.ReadAsStringAsync(), "1.1");

            using var traversal = await client.GetAsync($"{session.BaseUrl}/artifacts/result/%2e%2e/outside.txt");
            Assert.AreNotEqual(HttpStatusCode.OK, traversal.StatusCode);
            Assert.IsFalse(registry.TryResolveFile("result", "../outside.txt", out _));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-artifact-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
