using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QiongTu.Contracts;

namespace QiongTu.Control;

public static class RuntimeDiscovery
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string CreatePipeName()
    {
        var userBytes = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName));
        var userScope = Convert.ToHexString(userBytes.AsSpan(0, 6)).ToLowerInvariant();
        var instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return $"qiongtu-control-v1-{userScope}-{instance}";
    }

    public static async Task WriteAtomicallyAsync(
        string path,
        ControlDiscovery discovery,
        CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(discovery, SerializerOptions);
        await File.WriteAllTextAsync(tempPath, json, Utf8WithoutBom, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    public static void DeleteIfOwned(string path, int processId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var discovery = JsonSerializer.Deserialize<ControlDiscovery>(File.ReadAllText(path), SerializerOptions);
            if (discovery?.ProcessId == processId)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A concurrently starting instance owns the replacement discovery file.
        }
        catch (JsonException)
        {
            // Never delete a discovery file that cannot be proven to belong to this process.
        }
    }
}
