using System.Security.Cryptography;
using System.Reflection;
using ImageMagick;
using ImageMagick.Configuration;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

internal static class CasImageAnalyzer
{
    private const string ProductParser = "qiongtu.cas-image";
    private const string ProductParserVersion = "1.0.0";
    private const string NativeDecoder = "magick.net-q16-x64";
    private const string RequiredNativeDecoderVersion = "14.16.0";

    public static ImageProbeCasImageResult Analyze(ImageProbeCasImageRequestHeader header)
    {
        try
        {
            using var stream = FormalCasObject.OpenAndVerify(header);
            var structure = CasImageStructureParser.Parse(stream);
            using var runtime = MagickProbeRuntime.Create();
            var informationalVersion = typeof(MagickNET).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            if (!IsRequiredNativeDecoderVersion(informationalVersion))
            {
                return Blocked("native_decoder_version_mismatch", structure.Container);
            }

            var decoded = DecodeAndCrossCheck(stream, structure);
            return new ImageProbeCasImageResult(
                ImageProbeProtocol.CasImageV1,
                ImageProbeProtocol.CasImageProfile,
                "completed",
                "source_image",
                structure.Container,
                "validated",
                "decoded",
                decoded,
                [],
                ParserIdentity(),
                EmptyPrivacy());
        }
        catch (CasImageStructureException exception)
        {
            return Blocked(exception.Code);
        }
        catch (MagickPolicyErrorException)
        {
            return Blocked("native_policy_blocked");
        }
        catch (MagickResourceLimitErrorException)
        {
            return Blocked("native_resource_limit_exceeded");
        }
        catch (MagickException)
        {
            return Blocked("native_decode_failed");
        }
        catch (UnauthorizedAccessException)
        {
            return Blocked("formal_object_unavailable");
        }
        catch (IOException)
        {
            return Blocked("formal_object_unavailable");
        }
        catch (CryptographicException)
        {
            return Blocked("formal_object_integrity_failed");
        }
        catch (OverflowException)
        {
            return Blocked("structure_arithmetic_overflow");
        }
    }

    public static ImageProbeCasImageResult Failed(string reasonCode) =>
        CreateResult("failed", "unknown", "unavailable", "not_run", reasonCode);

    internal static IDisposable CreateNativeRuntimeForTests() => MagickProbeRuntime.Create();

    internal static bool IsRequiredNativeDecoderVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return false;
        }

        var metadataIndex = informationalVersion.IndexOf('+');
        var semanticVersion = metadataIndex >= 0
            ? informationalVersion[..metadataIndex]
            : informationalVersion;
        return string.Equals(semanticVersion, RequiredNativeDecoderVersion, StringComparison.Ordinal);
    }

    private static ImageProbeCasImageResult Blocked(string reasonCode, string container = "unknown") =>
        CreateResult("blocked", container, "blocked", "not_decoded", reasonCode);

    private static ImageProbeCasImageResult CreateResult(
        string status,
        string container,
        string structureState,
        string decodeState,
        string reasonCode) =>
        new(
            ImageProbeProtocol.CasImageV1,
            ImageProbeProtocol.CasImageProfile,
            status,
            "source_image",
            container,
            structureState,
            decodeState,
            [],
            [reasonCode],
            ParserIdentity(),
            EmptyPrivacy());

    private static ImageProbeCasImageParserIdentity ParserIdentity() =>
        new(ProductParser, ProductParserVersion, NativeDecoder, RequiredNativeDecoderVersion);

    private static ImageProbePrivacy EmptyPrivacy() =>
        new(false, false, false, false, false, false, false, false);

    private static IReadOnlyList<ImageProbeCasImageFrame> DecodeAndCrossCheck(
        Stream stream,
        CasImageStructure structure)
    {
        return structure.Container switch
        {
            "jpeg" or "mpo" => DecodeJpegFrames(stream, structure),
            "tiff" => DecodeTiffPages(stream, structure),
            _ => throw new CasImageStructureException("unsupported_image_container")
        };
    }

    private static IReadOnlyList<ImageProbeCasImageFrame> DecodeJpegFrames(
        Stream stream,
        CasImageStructure structure)
    {
        var frames = new List<ImageProbeCasImageFrame>(structure.Frames.Count);
        foreach (var frame in structure.Frames)
        {
            using var slice = new StreamSlice(stream, frame.ByteOffset, frame.ByteLength);
            var settings = CreateReadSettings(MagickFormat.Jpeg, oneFrameOnly: true);
            using var image = new MagickImage(slice, settings);
            var decoded = CrossCheckFrame(frame, image);
            frames.Add(decoded);
        }

        return frames;
    }

    private static IReadOnlyList<ImageProbeCasImageFrame> DecodeTiffPages(
        Stream stream,
        CasImageStructure structure)
    {
        stream.Position = 0;
        var settings = CreateReadSettings(MagickFormat.Tiff, oneFrameOnly: false);
        using var images = new MagickImageCollection();
        images.Read(stream, settings);
        if (images.Count != structure.Frames.Count ||
            images.Count > ImageProbeProtocol.MaximumCasFrameCount)
        {
            throw new CasImageStructureException("parser_decoder_frame_count_disagreement");
        }

        var frames = new List<ImageProbeCasImageFrame>(images.Count);
        for (var index = 0; index < images.Count; index++)
        {
            frames.Add(CrossCheckFrame(structure.Frames[index], images[index]));
        }

        return frames;
    }

    private static MagickReadSettings CreateReadSettings(MagickFormat format, bool oneFrameOnly) =>
        new()
        {
            Format = format,
            FrameIndex = oneFrameOnly ? 0u : null,
            FrameCount = oneFrameOnly ? 1u : null,
            SyncImageWithExifProfile = false,
            SyncImageWithTiffProperties = false
        };

    private static ImageProbeCasImageFrame CrossCheckFrame(
        CasImageFrameStructure structure,
        IMagickImage<ushort> image)
    {
        var width = checked((int)image.Width);
        var height = checked((int)image.Height);
        var depth = checked((int)image.Depth);
        var orientation = (int)image.Orientation;
        if (width != structure.Width || height != structure.Height ||
            depth != structure.BitsPerChannel ||
            (structure.Orientation is not null && orientation != 0 && orientation != structure.Orientation))
        {
            throw new CasImageStructureException("parser_decoder_dimension_disagreement");
        }

        return new ImageProbeCasImageFrame(
            structure.FrameIndex,
            structure.FrameKind,
            structure.ByteOffset,
            structure.ByteLength,
            width,
            height,
            depth,
            structure.Orientation,
            "decoded");
    }

    private sealed class MagickProbeRuntime : IDisposable
    {
        private const string Policy = """
            <?xml version="1.0" encoding="UTF-8"?>
            <policymap>
              <policy domain="resource" name="memory" value="512MiB" />
              <policy domain="resource" name="map" value="1GiB" />
              <policy domain="resource" name="disk" value="4GiB" />
              <policy domain="resource" name="area" value="1000MP" />
              <policy domain="resource" name="width" value="100000" />
              <policy domain="resource" name="height" value="100000" />
              <policy domain="resource" name="list-length" value="256" />
              <policy domain="resource" name="thread" value="2" />
              <policy domain="resource" name="time" value="55" />
              <policy domain="coder" rights="none" pattern="*" />
              <policy domain="coder" rights="read" pattern="JPEG" />
              <policy domain="coder" rights="read" pattern="JPG" />
              <policy domain="coder" rights="read" pattern="MPO" />
              <policy domain="coder" rights="read" pattern="TIFF" />
              <policy domain="coder" rights="read" pattern="TIF" />
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="filter" rights="none" pattern="*" />
              <policy domain="path" rights="none" pattern="@*" />
            </policymap>
            """;

        private readonly string _root;

        private MagickProbeRuntime(string root)
        {
            _root = root;
        }

        public static MagickProbeRuntime Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "QiongTu", "image-probe", Guid.NewGuid().ToString("N"));
            var temp = Path.Combine(root, "temp");
            var configPath = Path.Combine(root, "config");
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(configPath);
            var configuration = ConfigurationFiles.Default;
            configuration.Policy.Data = Policy;
            configuration.Delegates.Data = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><delegatemap />";
            MagickNET.Initialize(configuration, configPath);
            MagickNET.SetTempDirectory(temp);
            MagickNET.SetEnvironmentVariable("MAGICK_TEMPORARY_PATH", temp);
            ResourceLimits.Memory = 512UL * 1024 * 1024;
            ResourceLimits.Disk = 4UL * 1024 * 1024 * 1024;
            ResourceLimits.Area = 1_000_000_000;
            ResourceLimits.Width = 100_000;
            ResourceLimits.Height = 100_000;
            ResourceLimits.ListLength = ImageProbeProtocol.MaximumCasFrameCount;
            ResourceLimits.Thread = 2;
            ResourceLimits.Time = 55;
            ResourceLimits.MaxMemoryRequest = 512UL * 1024 * 1024;
            ResourceLimits.MaxProfileSize = ImageProbeProtocol.MaximumCasMetadataBytes;
            return new MagickProbeRuntime(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The short-lived worker exits immediately; Windows releases any remaining native files.
            }
        }
    }

    private sealed class StreamSlice : Stream
    {
        private readonly Stream _source;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public StreamSlice(Stream source, long start, long length)
        {
            if (!source.CanRead || !source.CanSeek || start < 0 || length < 0 || start > source.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(start));
            }

            _source = source;
            _start = start;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length)
            {
                return 0;
            }

            _source.Position = checked(_start + _position);
            var read = _source.Read(buffer[..(int)Math.Min(buffer.Length, _length - _position)]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (next < 0 || next > _length)
            {
                throw new IOException("The decoder attempted to seek outside the verified image frame.");
            }

            _position = next;
            return next;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
