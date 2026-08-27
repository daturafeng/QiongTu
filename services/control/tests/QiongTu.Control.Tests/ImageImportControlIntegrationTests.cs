using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportControlIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ImageImportMethodsReplayConflictPagingPrivacyAndResumeUseRealPipe()
    {
        await using var scope = await ImageImportPipeScope.StartAsync();
        scope.SeedProjectDatasetVersion("dataset-version-import", "dji_supported");
        var sourceRoot = scope.CreateSourceRoot("source-a");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "DJI_0001.JPG"), "content-one");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "DJI_0002.JPG"), "content-two");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "DJI_0003.JPG"), "content-three");

        var startParameters = new
        {
            datasetVersionId = "dataset-version-import",
            sourceRootPath = sourceRoot
        };
        using var startedResponse = await scope.SendAsync(ControlMethods.ImageImportStart, "image-import-start", startParameters);
        using var replayResponse = await scope.SendAsync(ControlMethods.ImageImportStart, "image-import-start", startParameters);
        var started = Ok(startedResponse).GetProperty("result");
        var replay = Ok(replayResponse).GetProperty("result");
        var sessionId = started.GetProperty("importSessionId").GetString()!;

        Assert.AreEqual(sessionId, replay.GetProperty("importSessionId").GetString());
        Assert.AreEqual(1L, scope.Scalar<long>("SELECT count(*) FROM image_import_sessions;"));

        var otherSourceRoot = scope.CreateSourceRoot("source-b");
        await File.WriteAllTextAsync(Path.Combine(otherSourceRoot, "DJI_9999.JPG"), "other");
        using var conflict = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-start",
            new
            {
                datasetVersionId = "dataset-version-import",
                sourceRootPath = otherSourceRoot
            });
        Error(conflict, "idempotency_conflict");

        await scope.WaitForSessionStatusAsync(sessionId, "completed");

        using var getResponse = await scope.SendAsync(
            ControlMethods.ImageImportGet,
            "image-import-get",
            new { importSessionId = sessionId });
        var get = Ok(getResponse).GetProperty("result");
        Assert.AreEqual(sessionId, get.GetProperty("importSessionId").GetString());
        Assert.AreEqual("completed", get.GetProperty("status").GetString());

        using var resumeResponse = await scope.SendAsync(
            ControlMethods.ImageImportResume,
            "image-import-resume",
            new { importSessionId = sessionId });
        Assert.AreEqual("completed", Ok(resumeResponse).GetProperty("result").GetProperty("status").GetString());

        using var listResponse = await scope.SendAsync(
            ControlMethods.ImageImportList,
            "image-import-list",
            new { datasetVersionId = "dataset-version-import", pageSize = 10, cursor = (string?)null });
        var sessions = Ok(listResponse).GetProperty("result").GetProperty("items");
        Assert.AreEqual(1, sessions.GetArrayLength());
        Assert.AreEqual(sessionId, sessions[0].GetProperty("importSessionId").GetString());

        using var firstPageResponse = await scope.SendAsync(
            ControlMethods.ImageImportEntryList,
            "image-import-entry-list-1",
            new { importSessionId = sessionId, pageSize = 2, cursor = (string?)null });
        var firstPage = Ok(firstPageResponse).GetProperty("result");
        Assert.AreEqual(2, firstPage.GetProperty("items").GetArrayLength());
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

        using var secondPageResponse = await scope.SendAsync(
            ControlMethods.ImageImportEntryList,
            "image-import-entry-list-2",
            new { importSessionId = sessionId, pageSize = 2, cursor });
        var secondPage = Ok(secondPageResponse).GetProperty("result");
        Assert.AreEqual(1, secondPage.GetProperty("items").GetArrayLength());

        AssertPrivacy(getResponse);
        AssertPrivacy(firstPageResponse);
        AssertPrivacy(secondPageResponse);
        Assert.DoesNotContain(sourceRoot, getResponse.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourceRoot, firstPageResponse.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", firstPageResponse.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("sha256/", firstPageResponse.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentHash", firstPageResponse.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
    }

    [TestMethod]
    public async Task ImageImportCancelMethodUsesRealPipe()
    {
        await using var scope = await ImageImportPipeScope.StartAsync();
        scope.SeedProjectDatasetVersion("dataset-version-pending", "pending");
        var sourceRoot = scope.CreateSourceRoot("source-cancel");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "DJI_0001.JPG"), "pending-source");

        using var startedResponse = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-cancel-start",
            new
            {
                datasetVersionId = "dataset-version-pending",
                sourceRootPath = sourceRoot
            });
        var sessionId = Ok(startedResponse).GetProperty("result").GetProperty("importSessionId").GetString()!;

        using var cancelledResponse = await scope.SendAsync(
            ControlMethods.ImageImportCancel,
            "image-import-cancel",
            new { importSessionId = sessionId });
        var cancelled = Ok(cancelledResponse).GetProperty("result");

        Assert.AreEqual("cancelled", cancelled.GetProperty("status").GetString());
        Assert.AreEqual(sessionId, cancelled.GetProperty("importSessionId").GetString());
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind='source_image';"));
    }

    [TestMethod]
    public async Task StopIfIdleRejectsWhileImageImportIsQueuedOrActive()
    {
        await using var scope = await ImageImportPipeScope.StartAsync();
        scope.SeedProjectDatasetVersion("dataset-version-busy", "dji_supported");
        var sourceRoot = scope.CreateSourceRoot("source-busy");
        for (var index = 0; index < 1200; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, $"DJI_{index:D4}.JPG"),
                "busy-import-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var startedResponse = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-busy-start",
            new
            {
                datasetVersionId = "dataset-version-busy",
                sourceRootPath = sourceRoot
            });
        var sessionId = Ok(startedResponse).GetProperty("result").GetProperty("importSessionId").GetString()!;

        using var stopResponse = await scope.SendAsync(ControlMethods.StopIfIdle, "stop-while-import-active", new { });
        Error(stopResponse, "control_busy");

        using var cancelResponse = await scope.SendAsync(
            ControlMethods.ImageImportCancel,
            "image-import-busy-cancel",
            new { importSessionId = sessionId });
        Assert.AreEqual("cancelled", Ok(cancelResponse).GetProperty("result").GetProperty("status").GetString());
    }

    private static JsonElement Ok(JsonDocument document)
    {
        Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetRawText());
        return document.RootElement;
    }

    private static void Error(JsonDocument document, string expectedCode)
    {
        Assert.IsFalse(document.RootElement.GetProperty("ok").GetBoolean(), document.RootElement.GetRawText());
        Assert.AreEqual(expectedCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertPrivacy(JsonDocument document)
    {
        var raw = document.RootElement.GetRawText();
        Assert.DoesNotContain("fileObjectId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stageReceiptId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantineId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceLocatorManifest", raw, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ImageImportPipeScope : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly NamedPipeControlServer _server;
        private readonly BusinessDatabase _database;
        private readonly ImageImportCoordinator _imageImports;

        private ImageImportPipeScope(
            string root,
            string pipeName,
            BusinessDatabase database,
            WorkerSupervisor workers,
            ArtifactServer artifactServer,
            NamedPipeControlServer server,
            ImageImportCoordinator imageImports)
        {
            _root = root;
            PipeName = pipeName;
            _database = database;
            _workers = workers;
            _artifactServer = artifactServer;
            _server = server;
            _imageImports = imageImports;
        }

        private string PipeName { get; }

        public static async Task<ImageImportPipeScope> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-pipe-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(Path.Combine(root, "control"));
            var runtimeStore = new WorkerRuntimeStore(paths.RuntimeDatabase);
            runtimeStore.Initialize();
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var businessCatalog = new BusinessCatalog(database);
            var imageImportCatalog = new ImageImportCatalog(database);
            var sourceSecurity = new ImageImportSourceSecurity(
                Path.Combine(paths.StateDirectory, "image-import-locators"),
                new TestProtector(),
                () => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            var sourceDiscovery = new ImageImportSourceDiscovery(sourceSecurity);
            var objectStore = new ContentAddressedObjectStore(paths.ObjectDirectory);
            var imageImports = new ImageImportCoordinator(
                imageImportCatalog,
                sourceSecurity,
                sourceDiscovery,
                objectStore);

            var registry = new WorkerRegistry();
            var capabilities = new ProcessingCapabilityService(registry, paths);
            var workers = new WorkerSupervisor(registry, runtimeStore, paths.LogDirectory, capabilities);
            var roots = new ArtifactRootRegistry();
            roots.RegisterTrustedRoot("objects", objectStore.PublishedDirectory);
            var artifactServer = new ArtifactServer(roots);
            await artifactServer.StartAsync(CancellationToken.None);
            var pipeName = RuntimeDiscovery.CreatePipeName();
            var dispatcher = new ControlRequestDispatcher(
                pipeName,
                DateTimeOffset.UtcNow,
                artifactServer,
                workers,
                businessCatalog,
                capabilities,
                requestStop: () => { },
                imageImports,
                imageImportCatalog,
                paths);
            var server = new NamedPipeControlServer(pipeName, dispatcher);
            server.Start();
            return new ImageImportPipeScope(root, pipeName, database, workers, artifactServer, server, imageImports);
        }

        public string CreateSourceRoot(string name)
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public async Task WaitForSessionStatusAsync(string importSessionId, string status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!timeout.IsCancellationRequested)
            {
                using var response = await SendAsync(
                    ControlMethods.ImageImportGet,
                    "image-import-poll-" + Guid.NewGuid().ToString("N"),
                    new { importSessionId });
                var result = Ok(response).GetProperty("result");
                if (string.Equals(result.GetProperty("status").GetString(), status, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(25, timeout.Token);
            }

            Assert.Fail($"Image import session {importSessionId} did not reach {status}.");
        }

        public void SeedProjectDatasetVersion(string datasetVersionId, string sourceEligibilityState)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO projects(project_id,name,spatial_configuration_state,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('project-import','Project','pending','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-import','project-import','Dataset','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc,sealed_at_utc)
                VALUES($dataset_version_id,'dataset-import',
                    (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-import'),
                    'draft',$source_eligibility_state,'not_run','2026-08-24T00:00:00Z',NULL);
                """;
            command.Parameters.AddWithValue("$dataset_version_id", datasetVersionId);
            command.Parameters.AddWithValue("$source_eligibility_state", sourceEligibilityState);
            command.ExecuteNonQuery();
        }

        public async Task<JsonDocument> SendAsync(string method, string requestId, object? parameters)
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new { apiVersion = ContractVersions.ControlApiV1, requestId, method, parameters },
                JsonOptions));
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNotNull(line);
            return JsonDocument.Parse(line);
        }

        public T Scalar<T>(string sql)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            await _imageImports.DisposeAsync();
            await _artifactServer.DisposeAsync();
            _workers.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-image-import-pipe-test-protector");

        public byte[] Protect(byte[] plaintext)
        {
            using var hmac = new HMACSHA256(Secret);
            var tag = hmac.ComputeHash(plaintext);
            var protectedData = new byte[tag.Length + plaintext.Length];
            Buffer.BlockCopy(tag, 0, protectedData, 0, tag.Length);
            for (var index = 0; index < plaintext.Length; index++)
            {
                protectedData[tag.Length + index] = (byte)(plaintext[index] ^ 0x5a);
            }

            return protectedData;
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            if (protectedData.Length < 32)
            {
                throw new CryptographicException("protected payload too short");
            }

            var plaintext = new byte[protectedData.Length - 32];
            for (var index = 0; index < plaintext.Length; index++)
            {
                plaintext[index] = (byte)(protectedData[32 + index] ^ 0x5a);
            }

            using var hmac = new HMACSHA256(Secret);
            var expected = hmac.ComputeHash(plaintext);
            if (!CryptographicOperations.FixedTimeEquals(expected, protectedData.AsSpan(0, 32)))
            {
                throw new CryptographicException("protected payload authentication failed");
            }

            return plaintext;
        }
    }
}
