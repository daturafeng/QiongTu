using System.Diagnostics;
using QiongTu.Contracts;

namespace QiongTu.Control.Tests;

[TestClass]
public sealed class PositioningAuxProbeTests
{
    [TestMethod]
    public async Task AnalyzeMrkAsync_VerifiesFormalCasAndRunsFixedIsolatedProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-positioning-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new ContentAddressedObjectStore(Path.Combine(root, "objects"));
            var stage = await store.StageAsync(new MemoryStream(SourcePreflightSyntheticFixture.DjiMrk(1)));
            var published = await store.PublishAsync(stage);
            var observedArguments = new List<string>();
            var client = new IsolatedPositioningAuxProbeClient(
                new ImageCasProbeOptions(Timeout: TimeSpan.FromSeconds(20)),
                () => CreateDevelopmentProbeStartInfo(observedArguments));

            var result = await client.AnalyzeMrkAsync(store, published, 1, CancellationToken.None);

            Assert.AreEqual("parsed", result.ParseState, string.Join(',', result.ReasonCodes));
            Assert.AreEqual("passed", result.QualityState);
            Assert.AreEqual("contiguous", result.SequenceState);
            CollectionAssert.AreEqual(new[] { ImageProbeProtocol.StdioArgument }, observedArguments);
            Assert.IsFalse(result.Privacy.PathsIncluded);
            Assert.IsFalse(result.Privacy.RawMetadataIncluded);
            Assert.IsFalse(result.Privacy.CoordinatesIncluded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ValidateResult_AcceptsParsedWarningWithoutPrivateEvidence()
    {
        var result = ParsedResult() with
        {
            QualityState = "warning",
            RtkQualityState = "mixed_q"
        };

        IsolatedPositioningAuxProbeClient.ValidateResult(result);
    }

    [TestMethod]
    public void ValidateResult_RejectsCoordinateOrRawEvidenceFlags()
    {
        var valid = ParsedResult();

        Assert.Throws<ImageCasProbeException>(() =>
            IsolatedPositioningAuxProbeClient.ValidateResult(valid with
            {
                Privacy = valid.Privacy with { CoordinatesIncluded = true }
            }));
        Assert.Throws<ImageCasProbeException>(() =>
            IsolatedPositioningAuxProbeClient.ValidateResult(valid with
            {
                Privacy = valid.Privacy with { RawMetadataIncluded = true }
            }));
    }

    [TestMethod]
    public void ValidateResult_RejectsUnknownReasonAndForgedInventory()
    {
        var valid = ParsedResult();
        var failed = valid with
        {
            ParseState = "failed",
            QualityState = "failed",
            SequenceState = "failed",
            CoverageState = "failed",
            StandardDeviationState = "failed",
            RtkQualityState = "failed",
            CanonicalInventoryHash = "unavailable",
            ReasonCodes = ["unexpected_worker_reason"]
        };

        Assert.Throws<ImageCasProbeException>(() =>
            IsolatedPositioningAuxProbeClient.ValidateResult(failed));
        Assert.Throws<ImageCasProbeException>(() =>
            IsolatedPositioningAuxProbeClient.ValidateResult(valid with
            {
                CanonicalInventoryHash = new string('f', 63) + "G"
            }));
    }

    private static ImageProbeCasPositioningAuxResult ParsedResult() =>
        new(
            ImageProbeProtocol.CasPositioningAuxV1,
            ImageProbeProtocol.CasPositioningAuxProfile,
            "parsed",
            "passed",
            "positioning_aux",
            "mrk",
            "contiguous",
            "complete",
            "non_negative",
            "all_q50",
            new string('a', 64),
            [],
            new ImageProbeCasPositioningAuxParserIdentity(
                "qiongtu.cas-positioning-aux",
                "1.0.0",
                ImageProbeProtocol.DjiMrkParserV1,
                ImageProbeProtocol.DjiMrkQualityPolicyV1),
            new ImageProbePrivacy(false, false, false, false, false, false, false, false));

    private static ProcessStartInfo CreateDevelopmentProbeStartInfo(List<string> observedArguments)
    {
        var executablePath = Path.Combine(
            FindRepositoryRoot(),
            "services",
            "image-probe",
            "src",
            "QiongTu.ImageProbe",
            "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0",
            "win-x64",
            "QiongTu.ImageProbe.exe");
        var startInfo = new ProcessStartInfo { FileName = executablePath };
        startInfo.ArgumentList.Add(ImageProbeProtocol.StdioArgument);
        observedArguments.Add(ImageProbeProtocol.StdioArgument);
        return startInfo;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be located.");
    }
}
