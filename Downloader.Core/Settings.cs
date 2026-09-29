using Tomlyn;
using Tomlyn.Model;

namespace Downloader.Core;

public sealed class Settings
{
    public string UrlList { get; set; } = "";
    public List<string> Checksums { get; set; } = [];
    public string Destination { get; set; } = "";
    public bool UseReferer { get; set; } = true;
    public List<string> Referers { get; set; } = [];
    public string Referer { get; set; } = "";
    public int Concurrency { get; set; } = 2;
    public int Retries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 30;
    public bool AllowNoChecksum { get; set; }
    public int FontSize { get; set; } = 12;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 1350;
    public int Height { get; set; } = 850;
    public List<int> ColumnWidths { get; set; } = [];
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Closed_Stack_Downloader.toml");
    public static Settings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new();
        try
        {
            var t = Toml.ToModel(File.ReadAllText(path));
            var s = new Settings();
            string Str(string k, string d) => t.TryGetValue(k, out var v) && v is string x ? x : d;
            int Int(string k, int d) => t.TryGetValue(k, out var v) && v is long x && x >= int.MinValue && x <= int.MaxValue ? (int)x : d;
            bool Bool(string k, bool d) => t.TryGetValue(k, out var v) && v is bool x ? x : d;
            List<string> Strings(string k, List<string> d) => t.TryGetValue(k, out var v) && v is TomlArray a ? a.OfType<string>().ToList() : d;
            s.UrlList = Str("url_list", s.UrlList); s.Checksums = Strings("checksums", s.Checksums);
            s.Destination = Str("destination", s.Destination); s.UseReferer = Bool("use_referer", s.UseReferer);
            s.Referers = Strings("referers", s.Referers);
            s.Referer = Str("referer", s.Referer); s.Concurrency = Math.Clamp(Int("concurrency", 2), 1, 6);
            s.Retries = Math.Clamp(Int("retries", 3), 0, 10); s.TimeoutSeconds = Math.Clamp(Int("timeout_seconds", 30), 5, 600);
            s.AllowNoChecksum = Bool("allow_no_checksum", false); s.FontSize = Math.Clamp(Int("font_size", 12), 9, 16);
            s.X = Int("x", -1); s.Y = Int("y", -1); s.Width = Math.Max(800, Int("width", 1350)); s.Height = Math.Max(600, Int("height", 850));
            if (t.TryGetValue("column_widths", out var widths) && widths is TomlArray wa) s.ColumnWidths = wa.OfType<long>().Select(x => (int)Math.Clamp(x, 20, 5000)).ToList();
            return s;
        }
        catch (Exception ex) { warning = "設定ファイルを読み込めません。既定値を使用します: " + ex.Message; return new(); }
    }
    public void RememberReferer(string value)
    {
        string key = RefererKey(value);
        Referers.RemoveAll(existing => RefererKey(existing).Equals(key, StringComparison.Ordinal));
        Referers.Insert(0, value);
        Referer = value;
    }
    public static bool SameReferer(string left, string right) =>
        RefererKey(left).Equals(RefererKey(right), StringComparison.Ordinal);
    static string RefererKey(string value)
    {
        try
        {
            var uri = new Uri(Inputs.ValidateReferer(value), UriKind.Absolute);
            return "uri:" + uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
        }
        catch (FormatException)
        {
            return "raw:" + value;
        }
    }
    public void Save(string path)
    {
        var model = new TomlTable
        {
            ["url_list"] = UrlList, ["checksums"] = Array(Checksums), ["destination"] = Destination,
            ["use_referer"] = UseReferer, ["referers"] = Array(Referers), ["referer"] = Referer,
            ["concurrency"] = Concurrency, ["retries"] = Retries, ["timeout_seconds"] = TimeoutSeconds,
            ["allow_no_checksum"] = AllowNoChecksum, ["font_size"] = FontSize, ["x"] = X, ["y"] = Y,
            ["width"] = Width, ["height"] = Height, ["column_widths"] = Array(ColumnWidths)
        };
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Toml.FromModel(model));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
    static TomlArray Array<T>(IEnumerable<T> source) { var a = new TomlArray(); foreach (var x in source) a.Add(x!); return a; }
}
