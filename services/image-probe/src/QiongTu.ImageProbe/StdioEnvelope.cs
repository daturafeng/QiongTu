using System.Buffers;
using System.Text;
using QiongTu.Contracts;

namespace QiongTu.ImageProbe;

internal sealed class ImageProbeProtocolException : IOException
{
    public ImageProbeProtocolException(string code)
        : base("The image probe protocol request is invalid.")
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class StdioEnvelope
{
    public static async Task<byte[]> ReadHeaderLineAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maximumBytes + 1);
        try
        {
            var length = 0;
            while (length <= maximumBytes)
            {
                var read = await input.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
                if (read == 0)
                {
                    throw new ImageProbeProtocolException("header_terminated_early");
                }

                if (buffer[length] == (byte)'\n')
                {
                    var contentLength = length > 0 && buffer[length - 1] == (byte)'\r'
                        ? length - 1
                        : length;
                    if (contentLength == 0)
                    {
                        throw new ImageProbeProtocolException("empty_header");
                    }

                    return buffer.AsSpan(0, contentLength).ToArray();
                }

                length++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        throw new ImageProbeProtocolException("header_too_large");
    }

    public static void ValidateHeader(ImageProbeRequestHeader header)
    {
        if (!string.Equals(header.SchemaVersion, ImageProbeProtocol.SourcePreflightV1, StringComparison.Ordinal) ||
            !string.Equals(header.Profile, ImageProbeProtocol.SourcePreflightProfile, StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("unsupported_protocol");
        }

        if (header.CandidateKind is not ("image_candidate" or "positioning_aux_candidate"))
        {
            throw new ImageProbeProtocolException("invalid_candidate_kind");
        }

        if (header.PayloadByteLength is <= 0 or > ImageProbeProtocol.MaximumPayloadBytes)
        {
            throw new ImageProbeProtocolException("payload_size_out_of_range");
        }

        if (header.FormatHint is not null &&
            (header.FormatHint.Length is 0 or > 16 ||
             header.FormatHint.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
        {
            throw new ImageProbeProtocolException("invalid_format_hint");
        }

        if (header.AssociationItemCount is < 1 or > 100_000 ||
            (header.CandidateKind == "image_candidate" && header.AssociationItemCount is not null))
        {
            throw new ImageProbeProtocolException("invalid_association_item_count");
        }
    }

    public static void ValidateCasHeader(ImageProbeCasImageRequestHeader header)
    {
        if (!string.Equals(header.SchemaVersion, ImageProbeProtocol.CasImageV1, StringComparison.Ordinal) ||
            !string.Equals(header.Profile, ImageProbeProtocol.CasImageProfile, StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("unsupported_protocol");
        }

        if (header.ObjectKind != "source_image")
        {
            throw new ImageProbeProtocolException("invalid_object_kind");
        }

        if (header.ExpectedByteLength is <= 0 or > ImageProbeProtocol.MaximumCasObjectBytes)
        {
            throw new ImageProbeProtocolException("object_size_out_of_range");
        }

        if (string.IsNullOrEmpty(header.FormalObjectRoot) ||
            header.FormalObjectRoot.Length > 32_767 ||
            header.FormalObjectRoot.IndexOf('\0') >= 0 ||
            !Path.IsPathFullyQualified(header.FormalObjectRoot) ||
            header.FormalObjectRoot.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("formal_object_root_invalid");
        }

        if (!IsLowercaseSha256(header.ExpectedSha256))
        {
            throw new ImageProbeProtocolException("expected_hash_invalid");
        }

        var expectedObjectKey = $"sha256/{header.ExpectedSha256[..2]}/{header.ExpectedSha256}";
        if (!string.Equals(header.ObjectKey, expectedObjectKey, StringComparison.Ordinal))
        {
            throw new ImageProbeProtocolException("object_key_invalid");
        }
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static async Task ReadExactlyAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await input.ReadAsync(destination[offset..], cancellationToken);
            if (read == 0)
            {
                throw new ImageProbeProtocolException("payload_terminated_early");
            }

            offset += read;
        }
    }

    public static async Task<bool> HasTrailingDataAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        return await input.ReadAsync(buffer, cancellationToken) != 0;
    }
}
