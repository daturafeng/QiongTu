using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe.Tests;

[TestClass]
public sealed class CasPositioningAuxAnalyzerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Analyze_ValidDjiMrkQ50_ReturnsParsedPassedWithCanonicalInventoryHash()
    {
        var payload = CreateMrk(
            Line(1, q: 50),
            Line(2, q: 50, latitude: 30.22345678, longitude: 120.22345678, gpsSeconds: 12346.123456));

        var result = AnalyzeFormalObject(payload, associationItemCount: 2, out var root, out var header);
        var json = JsonSerializer.Serialize(result, JsonOptions);

        Assert.AreEqual("parsed", result.ParseState, string.Join(',', result.ReasonCodes));
        Assert.AreEqual("passed", result.QualityState);
        Assert.AreEqual("contiguous", result.SequenceState);
        Assert.AreEqual("complete", result.CoverageState);
        Assert.AreEqual("non_negative", result.StandardDeviationState);
        Assert.AreEqual("all_q50", result.RtkQualityState);
        Assert.AreEqual("positioning_aux", result.ObjectKind);
        Assert.AreEqual("mrk", result.AuxiliaryType);
        Assert.AreEqual("qiongtu.cas-positioning-aux", result.Parser.ProductParser);
        Assert.AreEqual(ImageProbeProtocol.DjiMrkParserV1, result.Parser.AuxiliaryParserVersion);
        Assert.AreEqual(ImageProbeProtocol.DjiMrkQualityPolicyV1, result.Parser.QualityPolicyVersion);
        Assert.IsTrue(IsLowercaseSha256(result.CanonicalInventoryHash));
        Assert.AreNotEqual(header.ExpectedSha256, result.CanonicalInventoryHash);
        Assert.DoesNotContain(Encoding.UTF8.GetString(payload), json, StringComparison.Ordinal);
        Assert.DoesNotContain("30.12345678", json, StringComparison.Ordinal);
        Assert.DoesNotContain("120.12345678", json, StringComparison.Ordinal);
        Assert.DoesNotContain("12345.123456", json, StringComparison.Ordinal);
        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(header.ExpectedSha256, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(header.ObjectKey, json, StringComparison.OrdinalIgnoreCase);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_NonQ50OrMixedQuality_ReturnsWarningAfterStructurePasses()
    {
        var allFloat = AnalyzeFormalObject(
            CreateMrk(Line(1, q: 16), Line(2, q: 16, gpsSeconds: 12346.1)),
            associationItemCount: 2);
        var mixed = AnalyzeFormalObject(
            CreateMrk(Line(1, q: 50), Line(2, q: 16, gpsSeconds: 12346.1)),
            associationItemCount: 2);

        Assert.AreEqual("parsed", allFloat.ParseState);
        Assert.AreEqual("warning", allFloat.QualityState);
        Assert.AreEqual("non_q50", allFloat.RtkQualityState);
        Assert.AreEqual("parsed", mixed.ParseState);
        Assert.AreEqual("warning", mixed.QualityState);
        Assert.AreEqual("mixed_q", mixed.RtkQualityState);
    }

    [TestMethod]
    public void Analyze_CoverageMismatchFailsWithoutReturningRecordCount()
    {
        var result = AnalyzeFormalObject(CreateMrk(Line(1, q: 50)), associationItemCount: 2);
        var json = JsonSerializer.Serialize(result, JsonOptions);

        Assert.AreEqual("failed", result.ParseState);
        Assert.AreEqual("failed", result.QualityState);
        Assert.AreEqual("failed", result.CoverageState);
        Assert.AreEqual("unavailable", result.CanonicalInventoryHash);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), "mrk_coverage_mismatch");
        Assert.DoesNotContain("associationItemCount", json, StringComparison.Ordinal);
        AssertPrivacy(result);
    }

    [TestMethod]
    [DataRow("1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n1\t12346.123456\t[2200]\t1,N\t-2,E\t3,V\t30.22345678,Lat\t120.22345678,Lon\t100.223,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n", "mrk_sequence_duplicate")]
    [DataRow("1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n3\t12346.123456\t[2200]\t1,N\t-2,E\t3,V\t30.22345678,Lat\t120.22345678,Lon\t100.223,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n", "mrk_sequence_gap")]
    [DataRow("1\tInfinity\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t50,Q\r\n", "mrk_numeric_invalid")]
    [DataRow("1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t-0.001000,\t0.001000,\t0.002000\t50,Q\r\n", "mrk_standard_deviation_negative")]
    [DataRow("1\t12345.123456\t[2200]\t1,N\t-2,E\t3,V\t30.12345678,Lat\t120.12345678,Lon\t100.123,Ellh\t0.001000,\t0.001000\r\n", "mrk_field_count_invalid")]
    public void Analyze_InvalidMrkStructureFailsWithStableReason(string text, string reasonCode)
    {
        var result = AnalyzeFormalObject(Encoding.UTF8.GetBytes(text), associationItemCount: 2);

        Assert.AreEqual("failed", result.ParseState);
        Assert.AreEqual("failed", result.QualityState);
        Assert.AreEqual("unavailable", result.CanonicalInventoryHash);
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), reasonCode);
        AssertPrivacy(result);
    }

    [TestMethod]
    public void Analyze_InvalidUtf8AndOverlongLineFailBeforeParsing()
    {
        var invalidUtf8 = AnalyzeFormalObject([0xff, 0xfe], associationItemCount: 1);
        var overlongLine = AnalyzeFormalObject(
            Encoding.UTF8.GetBytes(new string('1', ImageProbeProtocol.MaximumPositioningAuxLineBytes + 1)),
            associationItemCount: 1);

        CollectionAssert.Contains(invalidUtf8.ReasonCodes.ToArray(), "mrk_utf8_invalid");
        CollectionAssert.Contains(overlongLine.ReasonCodes.ToArray(), "mrk_line_length_exceeded");
        Assert.AreEqual("failed", invalidUtf8.ParseState);
        Assert.AreEqual("failed", overlongLine.ParseState);
        AssertPrivacy(invalidUtf8);
        AssertPrivacy(overlongLine);
    }

    [TestMethod]
    public void Analyze_FormalObjectIntegrityFailureFailsWithoutObjectIdentity()
    {
        var original = CreateMrk(Line(1, q: 50));
        var root = CreateFormalObject(original, associationItemCount: 1, out var header);
        try
        {
            var objectPath = Path.Combine(root, header.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(objectPath, CreateMrk(Line(1, q: 16)));

            var result = CasPositioningAuxAnalyzer.Analyze(header);
            var json = JsonSerializer.Serialize(result, JsonOptions);

            Assert.AreEqual("failed", result.ParseState);
            CollectionAssert.Contains(result.ReasonCodes.ToArray(), "formal_object_integrity_failed");
            Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(header.ExpectedSha256, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(header.ObjectKey, json, StringComparison.OrdinalIgnoreCase);
            AssertPrivacy(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ValidatePositioningAuxHeader_RejectsNavObsRtkAndNonAuxObjects()
    {
        var hash = new string('a', 64);
        var nav = new ImageProbeCasPositioningAuxRequestHeader(
            ImageProbeProtocol.CasPositioningAuxV1,
            ImageProbeProtocol.CasPositioningAuxProfile,
            "positioning_aux",
            "nav",
            1,
            Path.GetFullPath(Path.GetTempPath()),
            $"sha256/aa/{hash}",
            hash,
            1);
        var sourceImage = nav with { ObjectKind = "source_image", AuxiliaryType = "mrk" };

        var navException = Assert.Throws<ImageProbeProtocolException>(() =>
            StdioEnvelope.ValidatePositioningAuxHeader(nav));
        var objectException = Assert.Throws<ImageProbeProtocolException>(() =>
            StdioEnvelope.ValidatePositioningAuxHeader(sourceImage));

        Assert.AreEqual("unsupported_auxiliary_type", navException.Code);
        Assert.AreEqual("invalid_object_kind", objectException.Code);
    }

    [TestMethod]
    public async Task Program_DispatchesCasPositioningAuxAndUsesFailureEnvelope()
    {
        var root = CreateFormalObject(CreateMrk(Line(1, q: 50)), associationItemCount: 1, out var header);
        try
        {
            var success = await InvokeProbeAsync(header);
            var unsupported = await InvokeProbeAsync(header with { AuxiliaryType = "rtk" });

            Assert.AreEqual(0, success.ExitCode, success.StandardError);
            Assert.AreEqual("parsed", success.Result.ParseState);
            Assert.AreEqual("passed", success.Result.QualityState);
            Assert.AreEqual(2, unsupported.ExitCode, unsupported.StandardError);
            Assert.AreEqual("failed", unsupported.Result.ParseState);
            Assert.AreEqual("unknown", unsupported.Result.AuxiliaryType);
            CollectionAssert.Contains(unsupported.Result.ReasonCodes.ToArray(), "unsupported_auxiliary_type");
            AssertPrivacy(success.Result);
            AssertPrivacy(unsupported.Result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ImageProbeCasPositioningAuxResult AnalyzeFormalObject(byte[] bytes, int associationItemCount) =>
        AnalyzeFormalObject(bytes, associationItemCount, out _, out _);

    private static ImageProbeCasPositioningAuxResult AnalyzeFormalObject(
        byte[] bytes,
        int associationItemCount,
        out string root,
        out ImageProbeCasPositioningAuxRequestHeader header)
    {
        root = CreateFormalObject(bytes, associationItemCount, out header);
        try
        {
            StdioEnvelope.ValidatePositioningAuxHeader(header);
            return CasPositioningAuxAnalyzer.Analyze(header);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFormalObject(
        byte[] bytes,
        int associationItemCount,
        out ImageProbeCasPositioningAuxRequestHeader header)
    {
        var root = Path.Combine(Path.GetTempPath(), $"qiongtu-positioning-aux-probe-{Guid.NewGuid():N}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var objectKey = $"sha256/{hash[..2]}/{hash}";
        var path = Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        header = new ImageProbeCasPositioningAuxRequestHeader(
            ImageProbeProtocol.CasPositioningAuxV1,
            ImageProbeProtocol.CasPositioningAuxProfile,
            "positioning_aux",
            "mrk",
            associationItemCount,
            root,
            objectKey,
            hash,
            bytes.LongLength);
        return root;
    }

    private static async Task<(int ExitCode, string StandardError, ImageProbeCasPositioningAuxResult Result)> InvokeProbeAsync(
        ImageProbeCasPositioningAuxRequestHeader header)
    {
        using var process = new Process();
        var executable = ResolveProbeExecutable();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = ImageProbeProtocol.StdioArgument,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.Start();
        await process.StandardInput.BaseStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));
        await process.StandardInput.WriteLineAsync();
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = JsonSerializer.Deserialize<ImageProbeCasPositioningAuxResult>(output, JsonOptions);
        Assert.IsNotNull(result, output);
        return (process.ExitCode, error, result);
    }

    private static string ResolveProbeExecutable()
    {
        var outputPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "QiongTu.ImageProbe",
            "bin",
            "Debug",
            "net10.0",
            "win-x64",
            "QiongTu.ImageProbe.exe"));
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var copiedAppHost = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        Assert.IsTrue(File.Exists(copiedAppHost), "QiongTu.ImageProbe executable was not found.");
        return copiedAppHost;
    }

    private static byte[] CreateMrk(params string[] lines) =>
        Encoding.UTF8.GetBytes(string.Concat(lines));

    private static string Line(
        int sequence,
        double q,
        double latitude = 30.12345678,
        double longitude = 120.12345678,
        double gpsSeconds = 12345.123456) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{sequence}\t{gpsSeconds:F6}\t[2200]\t1,N\t-2,E\t3,V\t{latitude:F8},Lat\t{longitude:F8},Lon\t100.123,Ellh\t0.001000,\t0.001000,\t0.002000\t{q:R},Q\r\n");

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AssertPrivacy(ImageProbeCasPositioningAuxResult result)
    {
        Assert.IsFalse(result.Privacy.PathsIncluded);
        Assert.IsFalse(result.Privacy.LocatorsIncluded);
        Assert.IsFalse(result.Privacy.ContentHashesIncluded);
        Assert.IsFalse(result.Privacy.ObjectKeysIncluded);
        Assert.IsFalse(result.Privacy.RawMetadataIncluded);
        Assert.IsFalse(result.Privacy.SerialNumbersIncluded);
        Assert.IsFalse(result.Privacy.CoordinatesIncluded);
        Assert.IsFalse(result.Privacy.OwnerSampleStatisticsIncluded);
    }
}
