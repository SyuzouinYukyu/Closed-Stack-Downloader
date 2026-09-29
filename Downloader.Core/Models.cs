using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Downloader.Core;

public sealed record UrlEntry(Uri Uri, string Original);
public sealed record UrlList(IReadOnlyList<UrlEntry> Entries, int Duplicates);
public sealed class ChecksumRecord
{
    public required string Name { get; init; }
    public long? Size { get; set; }
    public string? Sha512 { get; set; }
    public string? Blake3 { get; set; }
    public bool HasHash => Sha512 is not null || Blake3 is not null;
}
public sealed record DownloadItem(UrlEntry Url, string FileName, ChecksumRecord? Checksum);
public sealed class DownloadStatus
{
    public required DownloadItem Item { get; init; }
    public string State { get; set; } = "待機";
    public string Detail { get; set; } = "";
    public long Bytes { get; set; }
    public long? Total { get; set; }
    public double BytesPerSecond { get; set; }
    public int Retries { get; set; }
    public int? HttpStatus { get; set; }
    public string? FinalUrl { get; set; }
    public string? ResolvedName { get; set; }
}
public static class Inputs
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static string CleanKey(string key) => new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    static string Basename(string name) => name.Replace('\\', '/').Split('/').Last().Normalize(NormalizationForm.FormC);
    public static UrlList ReadUrls(string path) => ParseUrls(File.ReadAllText(path, StrictUtf8));
    public static UrlList ParseUrls(string content)
    {
        var list = new List<UrlEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal); int duplicates = 0;
        var lines = content.TrimStart('\uFEFF').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string s = lines[i].Trim();
            if (s.Length == 0 || s.StartsWith('#') || s.StartsWith(';')) continue;
            if (s.Any(char.IsControl) || !Uri.TryCreate(s, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.UserInfo.Length > 0)
                throw new FormatException($"URLリスト {i + 1}行: HTTP(S)の有効な絶対URLではありません。");
            var b = new UriBuilder(uri) { Fragment = "" }; uri = b.Uri;
            string key = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
            if (!seen.Add(key)) { duplicates++; continue; }
            list.Add(new(uri, s));
        }
        if (list.Count == 0) throw new FormatException("URLがありません。");
        return new(list, duplicates);
    }
    public static string ValidateReferer(string value)
    {
        if (value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length > 0)
            throw new FormatException("RefererはHTTP(S)の絶対URLを指定してください。改行や認証情報は使用できません。");
        return value;
    }
    public static string ValidateDestinationDrop(IReadOnlyList<string> paths)
    {
        if (paths.Count != 1) throw new FormatException("保存先フォルダは1つだけ指定してください。");
        if (!Directory.Exists(paths[0])) throw new FormatException("存在するフォルダを指定してください。ファイルは保存先に指定できません。");
        return Path.GetFullPath(paths[0]);
    }
    public static List<ChecksumRecord> ReadChecksums(IEnumerable<string> paths)
    {
        var all = new List<ChecksumRecord>();
        foreach (var path in paths)
        {
            string content = File.ReadAllText(path, StrictUtf8).TrimStart('\uFEFF');
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".manifest") all.AddRange(ParseManifest(content, path));
            else if (ext == ".sha512" || ext == ".blake3") all.AddRange(ParseHashList(content, path, ext == ".sha512"));
            else throw new FormatException($"未対応のChecksum形式: {path}");
        }
        var merged = new Dictionary<string, ChecksumRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in all)
        {
            string key = r.Name.Replace('\\', '/').Normalize(NormalizationForm.FormC);
            if (!merged.TryGetValue(key, out var prior)) { merged.Add(key, r); continue; }
            if (prior.Size is not null && r.Size is not null && prior.Size != r.Size ||
                prior.Sha512 is not null && r.Sha512 is not null && !prior.Sha512.Equals(r.Sha512, StringComparison.OrdinalIgnoreCase) ||
                prior.Blake3 is not null && r.Blake3 is not null && !prior.Blake3.Equals(r.Blake3, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Checksum矛盾: {r.Name}");
            prior.Size ??= r.Size; prior.Sha512 ??= r.Sha512; prior.Blake3 ??= r.Blake3;
        }
        return merged.Values.ToList();
    }
    static IEnumerable<ChecksumRecord> ParseHashList(string content, string path, bool sha)
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string s = lines[i].Trim(); if (s.Length == 0 || s.StartsWith('#') || s.StartsWith(';')) continue;
            var match = Regex.Match(s, sha ? @"^([a-fA-F0-9]{128}) [ *](.+)$" : @"^([a-fA-F0-9]{64}) [ *](.+)$");
            string name, hash;
            if (match.Success) { hash = match.Groups[1].Value; name = match.Groups[2].Value; }
            else
            {
                match = Regex.Match(s, sha ? @"^SHA512 \((.+)\) = ([a-fA-F0-9]{128})$" : @"^BLAKE3 \((.+)\) = ([a-fA-F0-9]{64})$", RegexOptions.IgnoreCase);
                if (!match.Success) throw new FormatException($"{path} {i + 1}行: Hash形式が不正です。");
                name = match.Groups[1].Value; hash = match.Groups[2].Value;
            }
            if (string.IsNullOrWhiteSpace(name)) throw new FormatException($"{path} {i + 1}行: ファイル名がありません。");
            yield return new ChecksumRecord { Name = name, Sha512 = sha ? hash : null, Blake3 = sha ? null : hash };
        }
    }
    static IEnumerable<ChecksumRecord> ParseManifest(string content, string path)
    {
        using var doc = JsonDocument.Parse(content);
        var records = new List<ChecksumRecord>();
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) Visit(child); return; }
            if (element.ValueKind != JsonValueKind.Object) return;
            var map = element.EnumerateObject().ToDictionary(p => CleanKey(p.Name), p => p.Value, StringComparer.OrdinalIgnoreCase);
            string? GetString(params string[] keys) { foreach (var key in keys) if (map.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString(); return null; }
            var name = GetString("name", "filename", "file", "path");
            string? sha = GetString("sha512"), b3 = GetString("blake3");
            long? size = null;
            foreach (var key in new[] { "size", "length", "bytes" })
                if (map.TryGetValue(key, out var v)) { if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) size = n; else if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n)) size = n; break; }
            if (name is not null && (size is not null || sha is not null || b3 is not null))
            {
                if (size < 0) throw new FormatException($"{path}: 負のSize: {name}");
                ValidateHash(sha, 128, path, name); ValidateHash(b3, 64, path, name);
                records.Add(new() { Name = name, Size = size, Sha512 = sha, Blake3 = b3 });
            }
            else foreach (var value in map.Values) Visit(value);
        }
        Visit(doc.RootElement);
        if (records.Count == 0) throw new FormatException($"{path}: ファイル項目がありません。");
        return records;
    }
    static void ValidateHash(string? hash, int length, string path, string name)
    {
        if (hash is not null && (hash.Length != length || !hash.All(Uri.IsHexDigit))) throw new FormatException($"{path}: {name} のHashが不正です。");
    }
    public static List<DownloadItem> Plan(UrlList urls, IReadOnlyList<ChecksumRecord> records)
    {
        var plan = new List<DownloadItem>(); var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls.Entries)
        {
            string pathName = Uri.UnescapeDataString(url.Uri.AbsolutePath.Split('/').Last());
            string? queryName = QueryName(url.Uri);
            var candidates = new[] { pathName, queryName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Basename(x!)).Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = records.Where(r => candidates.Contains(Basename(r.Name), StringComparer.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) throw new FormatException($"Checksum対応が曖昧: {url.Original}");
            var record = matches.SingleOrDefault();
            string raw = record?.Name ?? (!string.IsNullOrWhiteSpace(pathName) ? pathName : queryName ?? "download_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Uri.AbsoluteUri)))[..16]);
            string file = SafeFilename(raw);
            if (!filenames.Add(file)) throw new FormatException($"保存ファイル名が衝突: {file}");
            plan.Add(new(url, file, record));
        }
        return plan;
    }
    public static string? QueryName(Uri uri)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = pair.Split('=', 2);
            if (halves.Length == 2 && (halves[0].Equals("file", StringComparison.OrdinalIgnoreCase) || halves[0].Equals("filename", StringComparison.OrdinalIgnoreCase)))
                return Uri.UnescapeDataString(halves[1].Replace('+', ' '));
        }
        return null;
    }
    public static string SafeFilename(string raw)
    {
        string s = Basename(raw).TrimEnd(' ', '.');
        var invalid = new HashSet<char>("<>:\"/\\|?*");
        s = new string(s.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (s.Length == 0 || s is "." or "..") throw new FormatException("ファイル名が不正です。");
        string stem = s.Split('.')[0];
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) s = "_" + s;
        return s;
    }
}
