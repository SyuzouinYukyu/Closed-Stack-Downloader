using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace Downloader.Core;

public sealed class Transfer : IDisposable
{
    readonly HttpClient client;
    readonly ConcurrentDictionary<string,string> names = new(StringComparer.OrdinalIgnoreCase);
    public Transfer(IEnumerable<DownloadItem>? planned = null)
    {
        client = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        if (planned is not null) foreach (var item in planned) names.TryAdd(item.FileName, item.Url.Uri.AbsoluteUri);
    }
    public void Dispose() => client.Dispose();
    public async Task RunAsync(DownloadStatus status, string directory, string? referer, int retries, int timeoutSeconds, Action<DownloadStatus>? progress, CancellationToken cancel)
    {
        var item = status.Item; string final = Path.Combine(directory, item.FileName), part = final + ".part", meta = part + ".meta";
        if (item.Checksum is null && File.Exists(final)) { status.State = "既存（Checksumなし・未照合）"; progress?.Invoke(status); return; }
        if (item.Checksum is not null && File.Exists(final))
        {
            status.State = "検証中"; progress?.Invoke(status);
            if (await VerifyAsync(final, item.Checksum, cancel)) { status.State = item.Checksum.HasHash ? "検証済み・既存" : "完了（サイズ確認のみ）"; progress?.Invoke(status); return; }
        }
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            cancel.ThrowIfCancellationRequested(); status.Retries = attempt;
            try
            {
                await LogicalAttemptAsync(status, part, meta, final, referer, timeoutSeconds, progress, cancel);
                return;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { status.State = "中止"; progress?.Invoke(status); throw; }
            catch (TransferFailure ex)
            {
                status.State = ex.State; status.Detail = ex.Message; progress?.Invoke(status);
                if (ex.Fresh) DeleteFragments(part, meta);
                if (!ex.Retry || attempt == retries)
                {
                    if (status.State == "再試行待ち") { status.State = "失敗"; progress?.Invoke(status); }
                    return;
                }
            }
            catch (OperationCanceledException) { status.Detail = "タイムアウト"; if (attempt == retries) { status.State = "失敗"; progress?.Invoke(status); return; } }
            catch (HttpRequestException ex) { status.Detail = ex.Message; if (attempt == retries) { status.State = "失敗"; progress?.Invoke(status); return; } }
            catch (IOException ex)
            {
                bool disk = ex.HResult == unchecked((int)0x80070070) || ex.HResult == unchecked((int)0x80070027);
                status.State = "失敗"; status.Detail = disk ? "空き容量不足の可能性: " + ex.Message : ex.Message; progress?.Invoke(status);
                if (disk || attempt == retries) return;
            }
            status.State = "再試行待ち"; progress?.Invoke(status);
            try { await Task.Delay(TimeSpan.FromSeconds(new[] { 2, 5, 10 }[Math.Min(attempt, 2)]), cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                status.State = "中止"; progress?.Invoke(status); throw;
            }
        }
    }
    async Task LogicalAttemptAsync(DownloadStatus status, string part, string metaPath, string final, string? referer, int timeout, Action<DownloadStatus>? progress, CancellationToken cancel)
    {
        bool hasStrongHash = status.Item.Checksum?.HasHash == true;
        long offset = 0;
        ResumeMetadata? metadata = null;
        if (File.Exists(part))
        {
            long candidateLength = new FileInfo(part).Length;
            metadata = File.Exists(metaPath) ? ResumeMetadata.TryLoad(metaPath) : null;
            bool valid = metadata?.Matches(status.Item, candidateLength) == true &&
                (hasStrongHash || metadata.StrongETag is not null) &&
                (status.Item.Checksum?.Size is not long expected || candidateLength <= expected);
            if (valid) offset = candidateLength;
            else { DeleteFragments(part, metaPath); metadata = null; }
        }
        else if (File.Exists(metaPath)) File.Delete(metaPath);
        if (offset == 0)
        {
            await DownloadOnceAsync(status, part, metaPath, final, referer, timeout, progress, cancel, 0, null);
            return;
        }
        try
        {
            await DownloadOnceAsync(status, part, metaPath, final, referer, timeout, progress, cancel, offset, metadata);
        }
        catch (FreshFallbackRequired ex)
        {
            DeleteFragments(part, metaPath);
            status.Detail = ex.Message;
            status.Bytes = 0;
            status.Total = null;
            progress?.Invoke(status);
            await DownloadOnceAsync(status, part, metaPath, final, referer, timeout, progress, cancel, 0, null);
        }
    }
    async Task DownloadOnceAsync(DownloadStatus status, string part, string metaPath, string final, string? referer, int timeout, Action<DownloadStatus>? progress, CancellationToken cancel, long offset, ResumeMetadata? metadata)
    {
        bool hasStrongHash = status.Item.Checksum?.HasHash == true;
        bool isResume = offset > 0;
        status.State = "ダウンロード中"; status.Detail = ""; status.Bytes = offset; progress?.Invoke(status);
        using var response = await GetAsync(status.Item.Url.Uri, offset, metadata?.StrongETag, referer, timeout, cancel);
        status.HttpStatus = (int)response.StatusCode; status.FinalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (isResume && (status.FinalUrl is null || metadata is null ||
            !ResumeMetadata.SameHttpUrl(metadata.FinalUrl, status.FinalUrl)))
            throw new FreshFallbackRequired("FinalUrlが一致しないため先頭から再取得します");
        if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new TransferFailure("失敗", "非identity Content-Encodingを拒否しました", false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && isResume)
        {
            if (hasStrongHash && await VerifyAsync(part, status.Item.Checksum!, cancel))
            {
                FinalizeFile(part, final); DeleteIfExists(metaPath); status.State = "検証済み"; progress?.Invoke(status); return;
            }
            throw new FreshFallbackRequired("416のため先頭から再取得します");
        }
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.PartialContent)
        {
            int code = (int)response.StatusCode;
            throw new TransferFailure(code is 401 or 403 ? "アクセス拒否" : code is 404 or 410 ? "リンク切れ" : "失敗", $"HTTP {code}", code is 408 or 429 or 500 or 502 or 503 or 504);
        }
        if (status.Item.Checksum is null)
        {
            var disposition = response.Content.Headers.ContentDisposition;
            string? proposed = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
            if (!string.IsNullOrWhiteSpace(proposed))
            {
                string resolved = Inputs.SafeFilename(proposed);
                string owner = names.GetOrAdd(resolved, status.Item.Url.Uri.AbsoluteUri);
                if (owner != status.Item.Url.Uri.AbsoluteUri)
                    throw new TransferFailure("失敗", $"保存ファイル名が衝突: {resolved}", false);
                final = Path.Combine(Path.GetDirectoryName(final)!, resolved);
                status.ResolvedName = resolved;
                if (File.Exists(final)) { DeleteFragments(part, metaPath); status.State = "既存（Checksumなし・未照合）"; progress?.Invoke(status); return; }
            }
        }
        bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.To is null || range.To < offset || range.Length is null || range.To >= range.Length)
            {
                if (isResume) throw new FreshFallbackRequired("Content-Rangeが不正なため先頭から再取得します");
                throw new TransferFailure("失敗", "Content-Rangeが不正です", true, true);
            }
            if (status.Item.Checksum?.Size is long e && range.Length != e) throw new TransferFailure("失敗", "Manifest SizeとContent-Rangeが矛盾", false);
            string? responseETag = StrongETag(response);
            if (isResume && !hasStrongHash && (responseETag is null || metadata?.StrongETag is null ||
                !responseETag.Equals(metadata.StrongETag, StringComparison.Ordinal)))
                throw new FreshFallbackRequired("Resume Validatorが一致しないため先頭から再取得します");
            status.Total = range.Length;
        }
        else
        {
            offset = 0; status.Bytes = 0; status.Total = response.Content.Headers.ContentLength;
            if (status.Item.Checksum?.Size is long e && status.Total is long actual && e != actual)
                throw new TransferFailure("失敗", "Manifest SizeとContent-Lengthが矛盾", false);
        }
        var newMetadata = new ResumeMetadata
        {
            SourceUrl = status.Item.Url.Uri.AbsoluteUri,
            FinalUrl = status.FinalUrl ?? status.Item.Url.Uri.AbsoluteUri,
            StrongETag = StrongETag(response),
            TotalLength = status.Total ?? status.Item.Checksum?.Size,
            ExpectedSha512 = status.Item.Checksum?.Sha512,
            ExpectedBlake3 = status.Item.Checksum?.Blake3
        };
        newMetadata.SaveAtomic(metaPath);
        long received = 0;
        {
            using var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var stream = await response.Content.ReadAsStreamAsync(cancel);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(262144); var watch = Stopwatch.StartNew(); long lastBytes = status.Bytes; long lastMs = 0;
            try
            {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel); idle.CancelAfter(TimeSpan.FromSeconds(timeout));
                int n = await stream.ReadAsync(buffer.AsMemory(0, 262144), idle.Token);
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), cancel); received += n; status.Bytes += n;
                if (watch.ElapsedMilliseconds - lastMs >= 300)
                {
                    double current = (status.Bytes - lastBytes) * 1000.0 / Math.Max(1, watch.ElapsedMilliseconds - lastMs);
                    status.BytesPerSecond = status.BytesPerSecond == 0 ? current : status.BytesPerSecond * .7 + current * .3;
                    lastBytes = status.Bytes; lastMs = watch.ElapsedMilliseconds; progress?.Invoke(status);
                }
            }
                await file.FlushAsync(cancel);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        progress?.Invoke(status);
        if (response.Content.Headers.ContentLength is long length && received != length) throw new TransferFailure("再試行待ち", "Responseが途中で終了しました", true);
        if (status.Total is long total && status.Bytes != total) throw new TransferFailure("再試行待ち", "受信サイズが不足しています", true);
        if (status.Item.Checksum?.Size is long size && status.Bytes != size) throw new TransferFailure("Checksum不一致", "Size不一致", true, true);
        status.State = "検証中"; progress?.Invoke(status);
        if (status.Item.Checksum is not null && !await VerifyAsync(part, status.Item.Checksum, cancel)) throw new TransferFailure("Checksum不一致", "Hash不一致", true, true);
        FinalizeFile(part, final);
        DeleteIfExists(metaPath);
        status.State = status.Item.Checksum is null ? "完了（未照合）" : status.Item.Checksum.HasHash ? "検証済み" : "完了（サイズ確認のみ）";
        progress?.Invoke(status);
    }
    async Task<HttpResponseMessage> GetAsync(Uri start, long offset, string? strongETag, string? referer, int timeout, CancellationToken cancel)
    {
        Uri current = start; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int redirect = 0; redirect <= 10; redirect++)
        {
            if (!seen.Add(current.AbsoluteUri)) throw new TransferFailure("失敗", "Redirect loop", false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity"); request.Headers.UserAgent.ParseAdd("Closed_Stack_Downloader/1.0.2");
            if (referer is not null) request.Headers.TryAddWithoutValidation("Referer", referer);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (strongETag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(strongETag));
            }
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); headerTimeout.CancelAfter(TimeSpan.FromSeconds(timeout));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            int code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location; response.Dispose();
            if (location is null) throw new TransferFailure("失敗", "Redirect Locationがありません", false);
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (next.Scheme is not ("http" or "https")) throw new TransferFailure("失敗", "HTTP(S)以外へのRedirect", false);
            if (current.Scheme == "https" && next.Scheme == "http") throw new TransferFailure("失敗", "HTTPSからHTTPへのRedirectを拒否", false);
            current = next;
        }
        throw new TransferFailure("失敗", "Redirect回数が10を超えました", false);
    }
    static void FinalizeFile(string part, string final)
    {
        if (File.Exists(final)) File.Replace(part, final, null); else File.Move(part, final);
    }
    static string? StrongETag(HttpResponseMessage response)
    {
        var tag = response.Headers.ETag;
        return tag is not null && !tag.IsWeak ? tag.ToString() : null;
    }
    static void DeleteFragments(string part, string meta)
    {
        DeleteIfExists(part); DeleteIfExists(meta);
    }
    static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
    public static async Task<bool> VerifyAsync(string path, ChecksumRecord record, CancellationToken cancel)
    {
        if (record.Size is long size && new FileInfo(path).Length != size) return false;
        if (!record.HasHash) return true;
        using var sha = record.Sha512 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        using var b3 = record.Blake3 is null ? null : Blake3.Hasher.New();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(262144);
        try { int n; while ((n = await stream.ReadAsync(buffer.AsMemory(0, 262144), cancel)) > 0) { sha?.AppendData(buffer.AsSpan(0, n)); b3?.Update(buffer.AsSpan(0, n)); } }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return (record.Sha512 is null || Convert.ToHexString(sha!.GetHashAndReset()).Equals(record.Sha512, StringComparison.OrdinalIgnoreCase)) &&
            (record.Blake3 is null || b3!.Finalize().ToString().Equals(record.Blake3, StringComparison.OrdinalIgnoreCase));
    }
}
sealed class FreshFallbackRequired(string message) : Exception(message);
public sealed class TransferFailure(string state, string message, bool retry, bool fresh = false) : Exception(message)
{
    public string State { get; } = state;
    public bool Retry { get; } = retry;
    public bool Fresh { get; } = fresh;
}
