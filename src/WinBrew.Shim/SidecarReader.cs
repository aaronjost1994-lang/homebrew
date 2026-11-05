using System.Text.Json;

namespace WinBrew.Shim;

/// <summary>The four sidecar members the shim needs (D4; writer: WinBrew.Core.Shims.ShimSidecar).</summary>
internal sealed record SidecarData(string Target, string Package, string Version, string Subsystem);

/// <summary>
/// Reflection-free sidecar parsing for Native AOT. Reads bin\&lt;name&gt;.shim.json
/// with FileShare.Read | FileShare.Delete, reads it FULLY and closes the
/// handle before the caller starts the child (D4) — so File.Replace on the
/// sidecar succeeds while other shim instances run.
/// </summary>
internal static class SidecarReader
{
    public const int MaxSidecarBytes = 64 * 1024;

    public static byte[]? ReadAllShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxSidecarBytes)
            {
                return null;
            }

            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        } // handle closed here, before CreateProcessW (D4)
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static SidecarData? Parse(ReadOnlySpan<byte> utf8)
    {
        string? target = null, package = null, version = null, subsystem = null;
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read())
                {
                    return null;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    reader.Skip();
                    continue;
                }

                var value = reader.GetString();
                switch (name)
                {
                    case "target": target = value; break;
                    case "package": package = value; break;
                    case "version": version = value; break;
                    case "subsystem": subsystem = value; break;
                    default: break;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (target is null || package is null || version is null || subsystem is not ("console" or "gui"))
        {
            return null;
        }

        return new SidecarData(target, package, version, subsystem);
    }
}
