using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class ImageImportBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task ImageImportTinyCatalogPayloadReturnsBoundedResponseTooLargeWithoutPathLeak()
    {
        await using var scope = await BoundaryPipeScope.StartAsync(maximumImageImportResponseBytes: 96);
        scope.SeedProjectDatasetVersion("dataset-version-tiny-limit", "dji_supported");
        var sourceRoot = scope.CreateSourceRoot("source-tiny-limit");
        var sourceFile = Path.Combine(sourceRoot, "IMG_0001.JPG");
        await File.WriteAllTextAsync(sourceFile, "tiny-payload-boundary");

        using var response = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-tiny-response",
            new
            {
                datasetVersionId = "dataset-version-tiny-limit",
                sourceRootPath = sourceRoot
            });

        Error(response, "response_too_large");
        var raw = response.RootElement.GetRawText();
        Assert.IsLessThanOrEqualTo(NamedPipeControlServer.MaximumResponseBytes, Encoding.UTF8.GetByteCount(raw));
        AssertNoSourcePathLeak(raw, sourceRoot, "IMG_0001.JPG");
        Assert.DoesNotContain("stageReceipt", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantine", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256/", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task ImageImportDatabaseApiProtectedManifestAndNoImportLogsDoNotLeakPrivateBoundaries()
    {
        await using var scope = await BoundaryPipeScope.StartAsync();
        scope.SeedProjectDatasetVersion("dataset-version-boundary", "dji_supported");
        var sourceRoot = scope.CreateSourceRoot("source-boundary");
        var nested = Path.Combine(sourceRoot, "private-owner-path");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "IMG_BOUNDARY_0001.JPG"), "boundary-one");
        await File.WriteAllTextAsync(Path.Combine(nested, "IMG_BOUNDARY_0002.JPG"), "boundary-two");

        using var startResponse = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-boundary-start",
            new
            {
                datasetVersionId = "dataset-version-boundary",
                sourceRootPath = sourceRoot
            });
        var sessionId = Ok(startResponse).GetProperty("result").GetProperty("importSessionId").GetString()!;
        await scope.WaitForSessionStatusAsync(sessionId, "completed");

        using var getResponse = await scope.SendAsync(
            ControlMethods.ImageImportGet,
            "image-import-boundary-get",
            new { importSessionId = sessionId });
        using var entriesResponse = await scope.SendAsync(
            ControlMethods.ImageImportEntryList,
            "image-import-boundary-entries",
            new { importSessionId = sessionId, pageSize = 50, cursor = (string?)null });

        var protectedManifestText = scope.ReadLocatorStorageText();
        var importLogText = scope.ReadImportLogText();
        Assert.AreEqual(
            string.Empty,
            importLogText,
            "3.1 currently emits no dedicated image-import logs; this boundary asserts no import log files are created.");

        foreach (var raw in new[]
        {
            startResponse.RootElement.GetRawText(),
            getResponse.RootElement.GetRawText(),
            entriesResponse.RootElement.GetRawText(),
            protectedManifestText,
            importLogText
        })
        {
            AssertNoSourcePathLeak(raw, sourceRoot, Path.Combine("private-owner-path", "IMG_BOUNDARY_0001.JPG"));
            AssertNoSourcePathLeak(raw, sourceRoot, Path.Combine("private-owner-path", "IMG_BOUNDARY_0002.JPG"));
            Assert.DoesNotContain("stageReceiptId", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("quarantineId", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expectedObjectKey", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fileObjectId", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contentHash", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sha256/", raw, StringComparison.OrdinalIgnoreCase);
        }

        var businessText = scope.ReadBusinessTextColumns();
        AssertNoSourcePathLeak(businessText, sourceRoot, Path.Combine("private-owner-path", "IMG_BOUNDARY_0001.JPG"));
        AssertNoSourcePathLeak(businessText, sourceRoot, Path.Combine("private-owner-path", "IMG_BOUNDARY_0002.JPG"));
    }

    [TestMethod]
    public async Task ImageImport31CreatesNoImageAuxiliaryParseRowsOrRecognitionConclusions()
    {
        await using var scope = await BoundaryPipeScope.StartAsync();
        scope.SeedProjectDatasetVersion("dataset-version-no-parse", "dji_supported");
        var sourceRoot = scope.CreateSourceRoot("source-no-parse");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "IMG_0001.JPG"), "not-a-decoded-image-yet");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "IMG_0002.MPO"), "not-a-decoded-container-yet");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "notes.MRK"), "sidecar is outside 3.1 candidate parsing");

        using var startResponse = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-no-parse-start",
            new
            {
                datasetVersionId = "dataset-version-no-parse",
                sourceRootPath = sourceRoot
            });
        var sessionId = Ok(startResponse).GetProperty("result").GetProperty("importSessionId").GetString()!;
        await scope.WaitForSessionStatusAsync(sessionId, "completed");

        using var getResponse = await scope.SendAsync(
            ControlMethods.ImageImportGet,
            "image-import-no-parse-get",
            new { importSessionId = sessionId });
        using var entriesResponse = await scope.SendAsync(
            ControlMethods.ImageImportEntryList,
            "image-import-no-parse-entries",
            new { importSessionId = sessionId, pageSize = 50, cursor = (string?)null });

        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM images;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_frames;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM image_metadata_fields;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_files;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM positioning_aux_usage;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM processing_jobs WHERE job_type='ingestion_qc';"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM job_events;"));
        Assert.AreEqual(0L, scope.Scalar<long>("SELECT count(*) FROM file_objects WHERE object_kind IN ('normalized_image_frame','positioning_aux','input_manifest','quality_report','log');"));

        var raw = getResponse.RootElement.GetRawText() + entriesResponse.RootElement.GetRawText() + scope.ReadBusinessTextColumns();
        foreach (var forbidden in new[]
        {
            "contentContainer",
            "primaryFrame",
            "frameRole",
            "manufacturer",
            "cameraModel",
            "lensModel",
            "exif",
            "xmp",
            "rtk",
            "sidecar",
            "metadataState",
            "parseState",
            "parsedSummary",
            "sourceEvidence"
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [TestMethod]
    public async Task ImageImportStartWithManyCandidatesReturnsBoundedSessionAndEntriesOnlyViaPages()
    {
        await using var scope = await BoundaryPipeScope.StartAsync();
        scope.SeedProjectDatasetVersion("dataset-version-many", "dji_supported");
        var sourceRoot = scope.CreateSourceRoot("source-many");
        for (var index = 0; index < 75; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, $"IMG_{index:D4}.JPG"),
                "many-candidate-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var startResponse = await scope.SendAsync(
            ControlMethods.ImageImportStart,
            "image-import-many-start",
            new
            {
                datasetVersionId = "dataset-version-many",
                sourceRootPath = sourceRoot
            });
        var startRaw = startResponse.RootElement.GetRawText();
        var start = Ok(startResponse).GetProperty("result");
        var sessionId = start.GetProperty("importSessionId").GetString()!;

        Assert.IsLessThanOrEqualTo(NamedPipeControlServer.MaximumResponseBytes, Encoding.UTF8.GetByteCount(startRaw));
        Assert.AreEqual(75, start.GetProperty("totalEntryCount").GetInt32());
        Assert.DoesNotContain("\"items\"", startRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IMG_0000.JPG", startRaw, StringComparison.Ordinal);

        using var firstPageResponse = await scope.SendAsync(
            ControlMethods.ImageImportEntryList,
            "image-import-many-page-1",
            new { importSessionId = sessionId, pageSize = 50, cursor = (string?)null });
        var firstPageRaw = firstPageResponse.RootElement.GetRawText();
        var firstPage = Ok(firstPageResponse).GetProperty("result");

        Assert.IsLessThanOrEqualTo(NamedPipeControlServer.MaximumResponseBytes, Encoding.UTF8.GetByteCount(firstPageRaw));
        Assert.AreEqual(50, firstPage.GetProperty("items").GetArrayLength());
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(cursor));

        using var secondPageResponse = await scope.SendAsync(
            ControlMethods.ImageImportEntryList,
            "image-import-many-page-2",
            new { importSessionId = sessionId, pageSize = 50, cursor });
        var secondPage = Ok(secondPageResponse).GetProperty("result");

        Assert.AreEqual(25, secondPage.GetProperty("items").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
        AssertPrivacy(firstPageRaw);
        AssertPrivacy(secondPageResponse.RootElement.GetRawText());
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

    private static void AssertPrivacy(string raw)
    {
        Assert.DoesNotContain("fileObjectId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stageReceiptId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quarantineId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceLocatorManifest", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedObjectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"objectKey\"", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoSourcePathLeak(string raw, string sourceRoot, string relativePath)
    {
        var absolute = Path.Combine(sourceRoot, relativePath);
        Assert.DoesNotContain(sourceRoot, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourceRoot), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(absolute, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(absolute), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(relativePath, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(relativePath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", raw, StringComparison.Ordinal);
    }

    private sealed class BoundaryPipeScope : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WorkerSupervisor _workers;
        private readonly ArtifactServer _artifactServer;
        private readonly NamedPipeControlServer _server;
        private readonly BusinessDatabase _database;
        private readonly ImageImportCoordinator _imageImports;

        private BoundaryPipeScope(
            string root,
            ControlDataPaths paths,
            string pipeName,
            BusinessDatabase database,
            WorkerSupervisor workers,
            ArtifactServer artifactServer,
            NamedPipeControlServer server,
            ImageImportCoordinator imageImports)
        {
            _root = root;
            Paths = paths;
            PipeName = pipeName;
            _database = database;
            _workers = workers;
            _artifactServer = artifactServer;
            _server = server;
            _imageImports = imageImports;
        }

        private string PipeName { get; }

        private ControlDataPaths Paths { get; }

        public static async Task<BoundaryPipeScope> StartAsync(int? maximumImageImportResponseBytes = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"qiongtu-image-import-boundary-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = ControlDataPaths.Create(Path.Combine(root, "control"));
            var runtimeStore = new WorkerRuntimeStore(paths.RuntimeDatabase);
            runtimeStore.Initialize();
            var database = new BusinessDatabase(paths.BusinessDatabase);
            database.Initialize();
            var businessCatalog = new BusinessCatalog(database);
            var imageImportCatalog = maximumImageImportResponseBytes is null
                ? new ImageImportCatalog(database)
                : new ImageImportCatalog(database, maximumImageImportResponseBytes.Value);
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
            return new BoundaryPipeScope(root, paths, pipeName, database, workers, artifactServer, server, imageImports);
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
                    "image-import-boundary-poll-" + Guid.NewGuid().ToString("N"),
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
                VALUES('project-import-boundary','Project','pending','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT OR IGNORE INTO datasets(dataset_id,project_id,name,lifecycle_state,created_at_utc,updated_at_utc)
                VALUES('dataset-import-boundary','project-import-boundary','Dataset','active','2026-08-24T00:00:00Z','2026-08-24T00:00:00Z');
                INSERT INTO dataset_versions(dataset_version_id,dataset_id,version_number,lifecycle_state,source_eligibility_state,quality_gate_state,created_at_utc,sealed_at_utc)
                VALUES($dataset_version_id,'dataset-import-boundary',
                    (SELECT COALESCE(MAX(version_number),0)+1 FROM dataset_versions WHERE dataset_id='dataset-import-boundary'),
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

        public string ReadBusinessTextColumns()
        {
            var builder = new StringBuilder();
            using var connection = _database.OpenConnection();
            var tables = new[] { "image_import_sessions", "image_import_entries", "file_objects", "dataset_versions" };
            foreach (var table in tables)
            {
                var textColumns = TextColumns(connection, table);
                if (textColumns.Count == 0)
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT " + string.Join(", ", textColumns) + " FROM " + table + ";";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        if (!reader.IsDBNull(index))
                        {
                            builder.AppendLine(reader.GetString(index));
                        }
                    }
                }
            }

            return builder.ToString();
        }

        public string ReadLocatorStorageText()
        {
            var locatorDirectory = Path.Combine(Paths.StateDirectory, "image-import-locators");
            if (!Directory.Exists(locatorDirectory))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(locatorDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                builder.AppendLine(File.ReadAllText(file));
            }

            return builder.ToString();
        }

        public string ReadImportLogText()
        {
            if (!Directory.Exists(Paths.LogDirectory))
            {
                return string.Empty;
            }

            var files = Directory.EnumerateFiles(Paths.LogDirectory, "*image-import*", SearchOption.AllDirectories).ToArray();
            if (files.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                builder.AppendLine(File.ReadAllText(file));
            }

            return builder.ToString();
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

        private static IReadOnlyList<string> TextColumns(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('" + table.Replace("'", "''", StringComparison.Ordinal) + "');";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                var type = reader.GetString(2);
                if (type.Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                {
                    columns.Add(name);
                }
            }

            return columns;
        }
    }

    private sealed class TestProtector : IImageImportSecretProtector
    {
        private static readonly byte[] Secret = Encoding.UTF8.GetBytes("qiongtu-image-import-boundary-test-protector");

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
