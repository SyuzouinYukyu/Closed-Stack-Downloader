using Downloader.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public class CoreTests
{
    [Fact] public void UrlAndSafety()
    {
        var x = Inputs.ParseUrls("\uFEFF# note\nhttps://EXAMPLE.com/a?q=1#frag\n\n; note\nhttps://example.com/a?q=1\nhttps://example.com/a?q=2\n");
        Assert.Equal(2, x.Entries.Count); Assert.Equal(1, x.Duplicates);
        Assert.Equal("?q=1", x.Entries[0].Uri.Query); Assert.Equal("", x.Entries[0].Uri.Fragment);
        Assert.Throws<FormatException>(() => Inputs.ParseUrls("https://example.com/a\nftp://example.com/a"));
        Assert.Equal("evil.exe", Inputs.SafeFilename("../../evil.exe")); Assert.Equal("_CON.txt", Inputs.SafeFilename("CON.txt"));
        Assert.Throws<FormatException>(() => Inputs.ValidateReferer("https://example.com/\r\nInjected: x"));
        string dir=Temp(); try { string file=Path.Combine(dir,"file.txt"); File.WriteAllText(file,"x"); Assert.Equal(Path.GetFullPath(dir),Inputs.ValidateDestinationDrop([dir])); Assert.Throws<FormatException>(()=>Inputs.ValidateDestinationDrop([file])); Assert.Throws<FormatException>(()=>Inputs.ValidateDestinationDrop([dir,dir])); Assert.Throws<FormatException>(()=>Inputs.ValidateDestinationDrop([Path.Combine(dir,"missing")])); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public void ChecksumsAndConflict()
    {
        string dir = Temp(); try
        {
            var manifest = Path.Combine(dir, "a.manifest"); var sha = Path.Combine(dir, "a.sha512"); var b3 = Path.Combine(dir, "a.blake3");
            File.WriteAllText(manifest, "{\"files\":[{\"name\":\"f.bin\",\"size\":3,\"sha-512\":\"" + new string('a',128) + "\",\"BLAKE3\":\"" + new string('b',64) + "\"}]}");
            File.WriteAllText(sha, new string('a',128) + " *f.bin\n"); File.WriteAllText(b3, new string('b',64) + "  f.bin\n");
            var records = Inputs.ReadChecksums([manifest,sha,b3]); Assert.Single(records); Assert.Equal(3, records[0].Size);
            File.WriteAllText(sha, new string('c',128) + "  f.bin\n"); Assert.Throws<FormatException>(() => Inputs.ReadChecksums([manifest,sha]));
            File.WriteAllText(sha, "zzz  f.bin"); Assert.Throws<FormatException>(() => Inputs.ReadChecksums([sha]));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact] public void ConfigRoundtrip()
    {
        string dir = Temp(); try { string p = Path.Combine(dir,"a.toml"); var a = new Settings { UrlList="u", Checksums=["a","b"], Destination="d", Referers=["https://example.com/"], Referer="https://example.com/", Concurrency=4, Retries=2, TimeoutSeconds=45, AllowNoChecksum=false, FontSize=15 }; a.Save(p); var b=Settings.Load(p,out var warning); Assert.Null(warning); Assert.Equal(a.UrlList,b.UrlList); Assert.Equal(a.Checksums,b.Checksums); Assert.Equal(a.Destination,b.Destination); Assert.Equal(a.Referers,b.Referers); Assert.Equal(a.Referer,b.Referer); Assert.Equal(a.Concurrency,b.Concurrency); Assert.Equal(a.Retries,b.Retries); Assert.Equal(a.TimeoutSeconds,b.TimeoutSeconds); Assert.Equal(a.AllowNoChecksum,b.AllowNoChecksum); Assert.Equal(a.FontSize,b.FontSize); }
        finally { Directory.Delete(dir,true); }
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task LocalTransfer(bool existingPart, bool ignoreRange)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x',8192));
        using var server = new LocalServer(request =>
        {
            if (request.GetValueOrDefault("Referer") != "https://example.test/") return new Reply(403, [], []);
            long from = 0; var headers = new Dictionary<string,string>(); int status = 200;
            if (existingPart && !ignoreRange && request.GetValueOrDefault("Range") is string h && h.StartsWith("bytes=")) { from=long.Parse(h[6..^1]); status=206; headers["Content-Range"]=$"bytes {from}-{bytes.Length-1}/{bytes.Length}"; }
            return new Reply(status,headers,bytes[(int)from..]);
        });
        string dir=Temp(); try
        {
            var url=Inputs.ParseUrls(server.Url+"file.bin").Entries[0];
            var record = Record("file.bin",bytes);
            var item=new DownloadItem(url,"file.bin",record); if (existingPart) { string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,bytes[..100]); WriteMeta(part,item,null,bytes.Length); }
            var status=new DownloadStatus { Item=item }; using var transfer=new Transfer();
            await transfer.RunAsync(status,dir,"https://example.test/",0,10,null,CancellationToken.None);
            Assert.Equal("検証済み",status.State); Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(dir,"file.bin")));
            Assert.False(File.Exists(Path.Combine(dir,"file.bin.part")));
            if (existingPart && !ignoreRange) Assert.Contains("bytes=100-",server.Ranges);
        }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task ExistingSkipsNetworkAndBadHashStaysPart()
    {
        byte[] bytes=Encoding.UTF8.GetBytes("hello world"); using var server=new LocalServer(_ => new Reply(200,[],bytes));
        string dir=Temp(); try
        {
            var url=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var good=Record("file.bin",bytes); File.WriteAllBytes(Path.Combine(dir,"file.bin"),bytes);
            using var transfer=new Transfer(); var status=new DownloadStatus { Item=new(url,"file.bin",good) };
            await transfer.RunAsync(status,dir,null,0,10,null,CancellationToken.None); Assert.Equal("検証済み・既存",status.State); Assert.Equal(0,server.Count);
            File.Delete(Path.Combine(dir,"file.bin")); var bad=Record("file.bin",bytes); bad.Sha512=new string('0',128);
            status=new() { Item=new(url,"file.bin",bad) }; await transfer.RunAsync(status,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal("Checksum不一致",status.State); Assert.False(File.Exists(Path.Combine(dir,"file.bin")));
        }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task RejectMissingReferer()
    {
        using var server=new LocalServer(_ => new Reply(403,[],[]));
        string dir=Temp(); try { var u=Inputs.ParseUrls(server.Url+"a").Entries[0]; using var t=new Transfer(); var s=new DownloadStatus { Item=new(u,"a",null) }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None); Assert.Equal("アクセス拒否",s.State); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task Range416VerifiesCompletePart()
    {
        byte[] bytes=Encoding.UTF8.GetBytes("already complete");
        using var server=new LocalServer(_ => new Reply(416,[],[])); string dir=Temp();
        try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",Record("file.bin",bytes)); string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,bytes); WriteMeta(part,item,null,bytes.Length);
            using var t=new Transfer(); var s=new DownloadStatus { Item=item };
            await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None); Assert.Equal("検証済み",s.State); Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); Assert.Single(server.Ranges); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task ContentDispositionIsSafeAndCancelKeepsPart()
    {
        byte[] bytes=new byte[1024*512]; RandomNumberGenerator.Fill(bytes);
        using var server=new LocalServer(_ => new Reply(200,new() { ["Content-Disposition"]="attachment; filename=\"../../safe.bin\"" },bytes));
        string dir=Temp(); try { var u=Inputs.ParseUrls(server.Url+"get").Entries[0]; using var t=new Transfer(); var s=new DownloadStatus { Item=new(u,"get",null) };
            await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None); Assert.Equal("完了（未照合）",s.State); Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(dir,"safe.bin")));
            using var canceled=new CancellationTokenSource(); canceled.Cancel(); var s2=new DownloadStatus { Item=new(u,"other",null) }; File.WriteAllBytes(Path.Combine(dir,"other.part"),bytes[..100]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>t.RunAsync(s2,dir,null,0,10,null,canceled.Token)); Assert.Equal(100,new FileInfo(Path.Combine(dir,"other.part")).Length); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task MismatchRetryStartsFresh()
    {
        byte[] good=Encoding.UTF8.GetBytes("correct"), bad=Encoding.UTF8.GetBytes("corrupt"); int calls=0;
        using var server=new LocalServer(_ => new Reply(200,[],Interlocked.Increment(ref calls)==1 ? bad : good));
        string dir=Temp(); try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var s=new DownloadStatus { Item=new(u,"file.bin",Record("file.bin",good)) };
            using var t=new Transfer(); await t.RunAsync(s,dir,null,1,10,null,CancellationToken.None);
            Assert.Equal("検証済み",s.State); Assert.Equal(2,calls); Assert.Equal(good,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); Assert.All(server.Ranges,r=>Assert.Equal("",r)); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task CancelDuringTransferThenResume()
    {
        byte[] bytes=new byte[2*1024*1024]; RandomNumberGenerator.Fill(bytes);
        using var server=new LocalServer(request => { long from=0; var headers=new Dictionary<string,string>(); int status=200;
            if (request.GetValueOrDefault("Range") is string h && h.StartsWith("bytes=")) { from=long.Parse(h[6..^1]); status=206; headers["Content-Range"]=$"bytes {from}-{bytes.Length-1}/{bytes.Length}"; }
            return new Reply(status,headers,bytes[(int)from..],true); });
        string dir=Temp(); try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",Record("file.bin",bytes));
            using var t=new Transfer(); using var cts=new CancellationTokenSource(); var s=new DownloadStatus { Item=item };
            var run=t.RunAsync(s,dir,null,0,10,null,cts.Token);
            for(int i=0;i<100 && s.Bytes<32768 && !run.IsCompleted;i++) await Task.Delay(20);
            Assert.True(s.Bytes>0); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);
            string part=Path.Combine(dir,"file.bin.part"); long partial=new FileInfo(part).Length; Assert.InRange(partial,1,bytes.Length-1); Assert.True(File.Exists(part+".meta"));
            var resumed=new DownloadStatus { Item=item }; await t.RunAsync(resumed,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal("検証済み",resumed.State); Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); Assert.Contains($"bytes={partial}-",server.Ranges); Assert.False(File.Exists(part+".meta")); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task UnsafeNoHashAndSizeOnlyPartsRestartFresh()
    {
        foreach (bool sizeOnly in new[] { false, true })
        {
            byte[] current=Encoding.ASCII.GetBytes("BBBBBB"); using var server=new LocalServer(_=>new Reply(200,[],current)); string dir=Temp();
            try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; ChecksumRecord? checksum=sizeOnly ? new() { Name="file.bin", Size=current.Length } : null; var item=new DownloadItem(u,"file.bin",checksum);
                string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAA")); if(sizeOnly) WriteMeta(part,item,null,current.Length);
                using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
                Assert.Equal(current,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); Assert.Equal("",Assert.Single(server.Ranges)); Assert.False(File.Exists(part+".meta")); }
            finally { Directory.Delete(dir,true); }
        }
    }
    [Fact] public async Task StrongETagResumeSendsIfRange()
    {
        byte[] whole=Encoding.ASCII.GetBytes("ABCDEF"); using var server=new LocalServer(request => new Reply(206,new() { ["Content-Range"]="bytes 3-5/6", ["ETag"]="\"v1\"" },whole[3..])); string dir=Temp();
        try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",null); string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,whole[..3]); WriteMeta(part,item,"\"v1\"",whole.Length);
            using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal(whole,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); var request=Assert.Single(server.Requests); Assert.Equal("bytes=3-",request["Range"]); Assert.Equal("\"v1\"",request["If-Range"]); Assert.False(File.Exists(part+".meta")); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task ChangedETagFullResponseReplacesPart()
    {
        byte[] current=Encoding.ASCII.GetBytes("BBBBBB"); using var server=new LocalServer(_=>new Reply(200,new() { ["ETag"]="\"v2\"" },current)); string dir=Temp();
        try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",null); string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAA")); WriteMeta(part,item,"\"v1\"",current.Length);
            using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal(current,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); var request=Assert.Single(server.Requests); Assert.Equal("\"v1\"",request["If-Range"]); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task Invalid206ValidatorForcesFresh()
    {
        byte[] current=Encoding.ASCII.GetBytes("BBBBBB"); int calls=0; using var server=new LocalServer(_ => Interlocked.Increment(ref calls)==1
            ? new Reply(206,new() { ["Content-Range"]="bytes 3-5/6", ["ETag"]="\"v2\"" },current[3..])
            : new Reply(200,new() { ["ETag"]="\"v2\"" },current)); string dir=Temp();
        try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",null); string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAA")); WriteMeta(part,item,"\"v1\"",current.Length);
            using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal(2,calls); Assert.Equal(current,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); Assert.Equal("bytes=3-",server.Ranges[0]); Assert.Equal("",server.Ranges[1]); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task Range416SizeOnlyForcesFresh()
    {
        byte[] current=Encoding.ASCII.GetBytes("BBBBBB"); int calls=0; using var server=new LocalServer(_ => Interlocked.Increment(ref calls)==1 ? new Reply(416,[],[]) : new Reply(200,[],current)); string dir=Temp();
        try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",new ChecksumRecord { Name="file.bin",Size=current.Length }); string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAAAAA")); WriteMeta(part,item,"\"v1\"",current.Length);
            using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal(2,calls); Assert.Equal("完了（サイズ確認のみ）",s.State); Assert.Equal(current,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task MissingOrCorruptMetadataForcesFresh()
    {
        Assert.True(ResumeMetadata.IsStrongETag("\"v1\"")); Assert.False(ResumeMetadata.IsStrongETag("W/\"v1\""));
        foreach (int metadataCase in Enumerable.Range(0,5))
        {
            byte[] current=Encoding.ASCII.GetBytes("BBBBBB"); using var server=new LocalServer(_=>new Reply(200,[],current)); string dir=Temp();
            try { var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",Record("file.bin",current)); string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAA"));
                if(metadataCase==1) File.WriteAllText(part+".meta","{broken");
                else if(metadataCase>=2) new ResumeMetadata { Version=metadataCase==4 ? 99 : 1, SourceUrl=metadataCase==2 ? server.Url+"other.bin" : u.Uri.AbsoluteUri,
                    FinalUrl=u.Uri.AbsoluteUri, TotalLength=current.Length, ExpectedSha512=metadataCase==3 ? new string('0',128) : item.Checksum!.Sha512, ExpectedBlake3=item.Checksum!.Blake3 }.SaveAtomic(part+".meta");
                using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
                Assert.Equal(current,File.ReadAllBytes(Path.Combine(dir,"file.bin"))); Assert.Equal("",Assert.Single(server.Ranges)); }
            finally { Directory.Delete(dir,true); }
        }
    }
    [Fact] public async Task RedirectedFinalUrlMismatchForcesFreshEvenWithStrongHash()
    {
        byte[] current=Encoding.ASCII.GetBytes("BBBBBB");
        using var server=new LocalServer(request =>
        {
            string requestLine=request.GetValueOrDefault(":RequestLine") ?? "";
            if(requestLine.StartsWith("GET /source ",StringComparison.Ordinal))
                return new Reply(302,new() { ["Location"]="/new" },[]);
            return request.ContainsKey("Range")
                ? new Reply(206,new() { ["Content-Range"]="bytes 3-5/6", ["ETag"]="\"same\"" },current[3..])
                : new Reply(200,new() { ["ETag"]="\"same\"" },current);
        });
        string dir=Temp(); try
        {
            var u=Inputs.ParseUrls(server.Url+"source").Entries[0]; var item=new DownloadItem(u,"file.bin",Record("file.bin",current));
            string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAA"));
            new ResumeMetadata { SourceUrl=u.Uri.AbsoluteUri, FinalUrl=server.Url+"old", StrongETag="\"same\"", TotalLength=current.Length,
                ExpectedSha512=item.Checksum!.Sha512, ExpectedBlake3=item.Checksum.Blake3 }.SaveAtomic(part+".meta");
            using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal("検証済み",s.State); Assert.Equal(current,File.ReadAllBytes(Path.Combine(dir,"file.bin")));
            Assert.Equal(2,server.Requests.Count(x=>(x.GetValueOrDefault(":RequestLine") ?? "").StartsWith("GET /source ",StringComparison.Ordinal)));
            var finalRequests=server.Requests.Where(x=>(x.GetValueOrDefault(":RequestLine") ?? "").StartsWith("GET /new ",StringComparison.Ordinal)).ToList();
            Assert.Equal(2,finalRequests.Count); Assert.Equal("bytes=3-",finalRequests[0]["Range"]); Assert.False(finalRequests[1].ContainsKey("Range"));
        }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public async Task Invalid206WithZeroRetriesStopsAfterResumeAndFresh()
    {
        byte[] current=Encoding.ASCII.GetBytes("BBBBBB");
        using var server=new LocalServer(_=>new Reply(206,new() { ["Content-Range"]="bytes 2-5/6", ["ETag"]="\"same\"" },current[2..]));
        string dir=Temp(); try
        {
            var u=Inputs.ParseUrls(server.Url+"file.bin").Entries[0]; var item=new DownloadItem(u,"file.bin",Record("file.bin",current));
            string part=Path.Combine(dir,"file.bin.part"); File.WriteAllBytes(part,Encoding.ASCII.GetBytes("AAA")); WriteMeta(part,item,"\"same\"",current.Length);
            using var t=new Transfer(); var s=new DownloadStatus { Item=item }; await t.RunAsync(s,dir,null,0,10,null,CancellationToken.None);
            Assert.Equal(2,server.Count); Assert.Equal("失敗",s.State); Assert.NotEqual("再試行待ち",s.State);
            Assert.False(File.Exists(part)); Assert.False(File.Exists(part+".meta"));
        }
        finally { Directory.Delete(dir,true); }
    }
    [Fact] public void RefererDefaultsAndHistoryRoundtrip()
    {
        var fresh=new Settings(); Assert.Empty(fresh.Referers); Assert.Equal("",fresh.Referer); Assert.False(fresh.AllowNoChecksum);
        fresh.RememberReferer("https://EXAMPLE.test/path?q=1"); fresh.RememberReferer("https://example.test/path?q=1"); Assert.Single(fresh.Referers);
        string dir=Temp(); try { string path=Path.Combine(dir,"settings.toml"); fresh.Save(path); var loaded=Settings.Load(path,out var warning); Assert.Null(warning); Assert.Single(loaded.Referers); Assert.Equal("https://example.test/path?q=1",loaded.Referer);
            File.WriteAllText(path,"allow_no_checksum = true\nreferers = [\"https://saved.example/\"]\nreferer = \"https://saved.example/\"\n"); loaded=Settings.Load(path,out warning); Assert.Null(warning); Assert.True(loaded.AllowNoChecksum); Assert.Equal(["https://saved.example/"],loaded.Referers); }
        finally { Directory.Delete(dir,true); }
    }
    static ChecksumRecord Record(string name, byte[] data)
    {
        using var h=Blake3.Hasher.New(); h.Update(data);
        return new() { Name=name, Size=data.Length, Sha512=Convert.ToHexString(SHA512.HashData(data)), Blake3=h.Finalize().ToString() };
    }
    static void WriteMeta(string part, DownloadItem item, string? etag, long? total) => new ResumeMetadata
    {
        SourceUrl=item.Url.Uri.AbsoluteUri, FinalUrl=item.Url.Uri.AbsoluteUri, StrongETag=etag, TotalLength=total,
        ExpectedSha512=item.Checksum?.Sha512, ExpectedBlake3=item.Checksum?.Blake3
    }.SaveAtomic(part+".meta");
    static string Temp() { string p=Path.Combine(Path.GetTempPath(),"CSD_TEST_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
    sealed record Reply(int Status, Dictionary<string,string> Headers, byte[] Body, bool Slow=false);
    sealed class LocalServer : IDisposable
    {
        readonly TcpListener listener; readonly Task loop;
        public int Count; public List<string> Ranges=[]; public List<Dictionary<string,string>> Requests=[];
        public string Url { get; }
        public LocalServer(Func<Dictionary<string,string>,Reply> respond)
        {
            listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); int port=((IPEndPoint)listener.LocalEndpoint).Port;
            Url=$"http://127.0.0.1:{port}/";
            loop=Task.Run(async () => { while(true) { TcpClient c; try { c=await listener.AcceptTcpClientAsync(); } catch { break; } _=Task.Run(async () =>
                { using(c) { try { var stream=c.GetStream(); var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true); var request=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); string? line=await reader.ReadLineAsync();
                    if(line is not null) request[":RequestLine"]=line;
                    while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync())) { int p=line.IndexOf(':'); if(p>0) request[line[..p]]=line[(p+1)..].Trim(); }
                    Interlocked.Increment(ref Count); lock(Ranges) { Ranges.Add(request.GetValueOrDefault("Range") ?? ""); Requests.Add(new(request,StringComparer.OrdinalIgnoreCase)); } var reply=respond(request);
                    string head=$"HTTP/1.1 {reply.Status} Test\r\nContent-Length: {reply.Body.Length}\r\nConnection: close\r\n" + string.Concat(reply.Headers.Select(x=>$"{x.Key}: {x.Value}\r\n")) + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    if(reply.Slow) { for(int i=0;i<reply.Body.Length;i+=4096) { await stream.WriteAsync(reply.Body.AsMemory(i,Math.Min(4096,reply.Body.Length-i))); await Task.Delay(2); } }
                    else await stream.WriteAsync(reply.Body);
                    await stream.FlushAsync(); } catch { } } }); } });
        }
        public void Dispose() { listener.Stop(); try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { } }
    }
}
