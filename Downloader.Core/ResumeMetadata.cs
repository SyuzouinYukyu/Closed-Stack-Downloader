using System.Net.Http.Headers;
using System.Text.Json;

namespace Downloader.Core;

public sealed class ResumeMetadata
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public int Version { get; set; } = 1;
    public required string SourceUrl { get; set; }
    public required string FinalUrl { get; set; }
    public string? StrongETag { get; set; }
    public long? TotalLength { get; set; }
    public string? ExpectedSha512 { get; set; }
    public string? ExpectedBlake3 { get; set; }

    public static ResumeMetadata? TryLoad(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ResumeMetadata>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void SaveAtomic(string path)
    {
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public bool Matches(DownloadItem item, long partLength)
    {
        if (Version != 1 || partLength < 0 || TotalLength < 0 || TotalLength < partLength) return false;
        if (!SameHttpUrl(SourceUrl, item.Url.Uri.AbsoluteUri)) return false;
        if (!ValidHttpUrl(FinalUrl)) return false;
        if (!SameHash(ExpectedSha512, item.Checksum?.Sha512) || !SameHash(ExpectedBlake3, item.Checksum?.Blake3)) return false;
        if (TotalLength is long total && item.Checksum?.Size is long expected && total != expected) return false;
        if (StrongETag is not null && !IsStrongETag(StrongETag)) return false;
        return true;
    }

    public static bool IsStrongETag(string value) =>
        EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak;

    static bool SameHash(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    static bool ValidHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";

    public static bool SameHttpUrl(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        if (a.Scheme is not ("http" or "https") || b.Scheme is not ("http" or "https")) return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
            a.Port == b.Port &&
            string.Equals(a.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped),
                b.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), StringComparison.Ordinal);
    }
}
