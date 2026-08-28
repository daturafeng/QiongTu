namespace QiongTu.Contracts;

public static class ImageProbeProtocol
{
    public const string SourcePreflightV1 = "qiongtu.image-probe.source-preflight.v1";
    public const string SourcePreflightProfile = "source-preflight.v1";
    public const string CasImageV1 = "qiongtu.image-probe.cas-image.v1";
    public const string CasImageProfile = "cas-image.v1";
    public const string StdioArgument = "--stdio";
    public const int MaximumHeaderBytes = 4 * 1024;
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;
    public const int MaximumMetadataBytes = MaximumPayloadBytes;
    public const int MaximumOutputBytes = 32 * 1024;
    public const int MaximumCasHeaderBytes = 64 * 1024;
    public const int MaximumCasOutputBytes = 64 * 1024;
    public const long MaximumCasObjectBytes = 16L * 1024 * 1024 * 1024;
    public const int MaximumCasMarkerCount = 256;
    public const int MaximumCasFrameCount = 256;
    public const int MaximumCasIfdEntryCount = 4 * 1024;
    public const long MaximumCasPixelsPerFrame = 1_000_000_000;
    public const long MaximumCasTotalPixels = 2_000_000_000;
    public const int MaximumCasMetadataBytes = 16 * 1024 * 1024;
    public const int MaximumEvidenceKinds = 16;
    public const int MaximumReasonCodes = 16;
}

public sealed record ImageProbeDispatchHeader(
    string SchemaVersion,
    string Profile);

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

public sealed record ImageProbeCasImageRequestHeader(
    string SchemaVersion,
    string Profile,
    string ObjectKind,
    string FormalObjectRoot,
    string ObjectKey,
    string ExpectedSha256,
    long ExpectedByteLength);

public sealed record ImageProbeCasImageFrame(
    int FrameIndex,
    string FrameKind,
    long ByteOffset,
    long ByteLength,
    int Width,
    int Height,
    int BitsPerChannel,
    int? Orientation,
    string DecodeState);

public sealed record ImageProbeCasImageParserIdentity(
    string ProductParser,
    string ProductParserVersion,
    string NativeDecoder,
    string NativeDecoderVersion);

public sealed record ImageProbeCasImageResult(
    string SchemaVersion,
    string Profile,
    string Status,
    string ObjectKind,
    string Container,
    string StructureState,
    string DecodeState,
    IReadOnlyList<ImageProbeCasImageFrame> Frames,
    IReadOnlyList<string> ReasonCodes,
    ImageProbeCasImageParserIdentity Parser,
    ImageProbePrivacy Privacy);
