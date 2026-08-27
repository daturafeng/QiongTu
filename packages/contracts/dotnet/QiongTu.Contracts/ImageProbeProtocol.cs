namespace QiongTu.Contracts;

public static class ImageProbeProtocol
{
    public const string SourcePreflightV1 = "qiongtu.image-probe.source-preflight.v1";
    public const string SourcePreflightProfile = "source-preflight.v1";
    public const string StdioArgument = "--stdio";
    public const int MaximumHeaderBytes = 4 * 1024;
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;
    public const int MaximumMetadataBytes = MaximumPayloadBytes;
    public const int MaximumOutputBytes = 32 * 1024;
    public const int MaximumEvidenceKinds = 16;
    public const int MaximumReasonCodes = 16;
}

public sealed record ImageProbeRequestHeader(
    string SchemaVersion,
    string Profile,
    string CandidateKind,
    string? FormatHint,
    int? AssociationItemCount,
    int PayloadByteLength);

public sealed record ImageProbeParserIdentity(
    string ProductParser,
    string ProductParserVersion,
    string MetadataExtractorVersion);

public sealed record ImageProbePrivacy(
    bool PathsIncluded,
    bool LocatorsIncluded,
    bool ContentHashesIncluded,
    bool ObjectKeysIncluded,
    bool RawMetadataIncluded,
    bool SerialNumbersIncluded,
    bool CoordinatesIncluded,
    bool OwnerSampleStatisticsIncluded);

public sealed record ImageProbeSourcePreflightResult(
    string SchemaVersion,
    string Profile,
    string Status,
    string CandidateKind,
    string ContainerHint,
    string EvidenceState,
    IReadOnlyList<string> EvidenceKinds,
    IReadOnlyList<string> ReasonCodes,
    ImageProbeParserIdentity Parser,
    ImageProbePrivacy Privacy);
