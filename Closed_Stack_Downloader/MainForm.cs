using Downloader.Core;
using System.Drawing;
using System.Text;

namespace Closed_Stack_Downloader;

public sealed class MainForm : Form
{
    readonly Settings settings;
    readonly TextBox urlBox = new() { Dock = DockStyle.Fill };
    readonly TextBox destBox = new() { Dock = DockStyle.Fill, AllowDrop = true };
    readonly ListBox checksumList = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, Height = 72, AllowDrop = true };
    readonly ComboBox refererBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    readonly RadioButton useReferer = new() { Text = "Refererを使用", AutoSize = true };
    readonly RadioButton noReferer = new() { Text = "送信しない", AutoSize = true };
    readonly NumericUpDown concurrency = new() { Minimum = 1, Maximum = 6, Width = 58 };
    readonly NumericUpDown retries = new() { Minimum = 0, Maximum = 10, Width = 58 };
    readonly NumericUpDown timeout = new() { Minimum = 5, Maximum = 600, Width = 65 };
    readonly CheckBox allowNoChecksum = new() { Text = "Checksum情報がないURLもダウンロードする", AutoSize = true };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, ReadOnly = true, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None };
    readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly ProgressBar bar = new() { Dock = DockStyle.Fill };
    readonly Button start = new() { Text = "ダウンロード開始", AutoSize = true };
    readonly Button pause = new() { Text = "一時停止", AutoSize = true };
    readonly Button resume = new() { Text = "再開", AutoSize = true };
    readonly Button stop = new() { Text = "中止", AutoSize = true };
    readonly Button retryFailed = new() { Text = "失敗のみ再試行", AutoSize = true };
    readonly List<DownloadStatus> statuses = [];
    CancellationTokenSource? running;
    Task? runTask;
    bool paused;
    int duplicates;
    public MainForm()
    {
        settings = Settings.Load(Settings.DefaultPath, out string? warning);
        Text = "Closed_Stack_Downloader v1.0.2"; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(850, 650); Size = new Size(settings.Width, settings.Height);
        if (settings.X >= 0 && settings.Y >= 0 && Screen.AllScreens.Any(s => s.WorkingArea.Contains(settings.X, settings.Y))) { StartPosition = FormStartPosition.Manual; Location = new(settings.X, settings.Y); }
        Font = new Font("Yu Gothic UI", settings.FontSize);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(8) };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(root);
        root.Controls.Add(PathRow("URLリスト", urlBox, () => PickFile(urlBox, "TXT|*.txt|すべて|*.*")), 0, 0);
        var checkPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        checkPanel.ColumnStyles.Add(new(SizeType.Absolute, 155)); checkPanel.ColumnStyles.Add(new(SizeType.Percent, 100)); checkPanel.ColumnStyles.Add(new(SizeType.AutoSize));
        checkPanel.Controls.Add(new Label { Text = "Checksumファイル", AutoSize = true }, 0, 0); checkPanel.Controls.Add(checksumList, 1, 0);
        var checkButtons = Flow(() => AddChecksums(), "追加", () => { foreach (var x in checksumList.SelectedItems.Cast<object>().ToArray()) checksumList.Items.Remove(x); }, "削除", () => checksumList.Items.Clear(), "すべて解除");
        checkPanel.Controls.Add(checkButtons, 2, 0); root.Controls.Add(checkPanel, 0, 1);
        root.Controls.Add(PathRow("保存先", destBox, () => { using var d = new FolderBrowserDialog(); if (d.ShowDialog(this) == DialogResult.OK) destBox.Text = d.SelectedPath; }), 0, 2);
        var refPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        refPanel.ColumnStyles.Add(new(SizeType.Absolute, 155)); refPanel.ColumnStyles.Add(new(SizeType.Percent, 100)); refPanel.ColumnStyles.Add(new(SizeType.AutoSize));
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; modes.Controls.Add(useReferer); modes.Controls.Add(noReferer);
        refPanel.Controls.Add(modes, 0, 0); refPanel.Controls.Add(refererBox, 1, 0);
        refPanel.Controls.Add(Flow(() => refererBox.Text = Clipboard.GetText(), "貼り付け", AddReferer, "追加", UpdateReferer, "更新", DeleteReferer, "削除"), 2, 0); root.Controls.Add(refPanel, 0, 3);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        options.Controls.AddRange([new Label { Text = "同時Download数", AutoSize = true }, concurrency, new Label { Text = "再試行回数", AutoSize = true }, retries,
            new Label { Text = "タイムアウト（秒）", AutoSize = true }, timeout, allowNoChecksum]); root.Controls.Add(options, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        actions.Controls.AddRange([start, pause, resume, stop, retryFailed]); root.Controls.Add(actions, 0, 5);
        foreach (var (name, width) in new[] { ("状態", 155), ("ファイル名", 190), ("サイズ", 110), ("ダウンロード済み", 125), ("進捗", 85), ("速度", 105), ("残り時間", 100), ("検証", 90), ("再試行", 75), ("HTTP", 70), ("URL", 300) })
        { var c = grid.Columns.Add(name, name); grid.Columns[c].Width = width; }
        grid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        for (int i = 0; i < Math.Min(settings.ColumnWidths.Count, grid.Columns.Count - 1); i++) grid.Columns[i].Width = settings.ColumnWidths[i];
        ApplyGridMetrics();
        root.Controls.Add(grid, 0, 6);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, AutoSize = true };
        footer.Controls.Add(bar, 0, 0); footer.Controls.Add(summary, 0, 1); root.Controls.Add(footer, 0, 7);
        urlBox.Text = settings.UrlList; destBox.Text = settings.Destination; checksumList.Items.AddRange(settings.Checksums.Cast<object>().ToArray());
        refererBox.Items.AddRange(settings.Referers.Cast<object>().ToArray()); refererBox.Text = settings.Referer;
        useReferer.Checked = settings.UseReferer; noReferer.Checked = !settings.UseReferer;
        concurrency.Value = settings.Concurrency; retries.Value = settings.Retries; timeout.Value = settings.TimeoutSeconds; allowNoChecksum.Checked = settings.AllowNoChecksum;
        urlBox.AllowDrop = true; urlBox.DragEnter += FileDragEnter; urlBox.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) urlBox.Text = files[0]; };
        checksumList.DragEnter += FileDragEnter; checksumList.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) foreach (var f in files) AddChecksum(f); };
        destBox.DragEnter += DestinationDragEnter; destBox.DragDrop += DestinationDragDrop;
        start.Click += async (_, _) => await BeginAsync(false); retryFailed.Click += async (_, _) => await BeginAsync(true);
        pause.Click += (_, _) => { paused = true; running?.Cancel(); SetButtons(); };
        resume.Click += async (_, _) => { paused = false; await BeginAsync(false, true); };
        stop.Click += (_, _) => { paused = false; running?.Cancel(); SetButtons(); };
        useReferer.CheckedChanged += (_, _) => refererBox.Enabled = useReferer.Checked;
        DpiChanged += (_, _) => BeginInvoke(ApplyGridMetrics);
        MouseWheel += ZoomWheel; foreach (Control c in Controls) HookWheel(c);
        FormClosing += OnFormClosing;
        SetButtons(); UpdateSummary();
        if (warning is not null) Shown += (_, _) => MessageBox.Show(this, warning, "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (settings.Checksums.Any(x => !File.Exists(x)) || settings.UrlList.Length > 0 && !File.Exists(settings.UrlList))
            Shown += (_, _) => MessageBox.Show(this, "前回指定したファイルが試つかりません。再指定してください。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    static void FileDragEnter(object? sender, DragEventArgs e) { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; }
    void HookWheel(Control c) { c.MouseWheel += ZoomWheel; foreach (Control child in c.Controls) HookWheel(child); }
    void ZoomWheel(object? sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0) return;
        int size = Math.Clamp((int)Font.Size + Math.Sign(e.Delta), 9, 16);
        if (Math.Abs(Font.Size - size) > 0.1f) { Font = new Font(Font.FontFamily, size); ApplyGridMetrics(); }
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }
    void ApplyGridMetrics()
    {
        grid.Font = Font; grid.DefaultCellStyle.Font = Font; grid.ColumnHeadersDefaultCellStyle.Font = Font;
        int scalePadding = Math.Max(8, DeviceDpi * 8 / 96);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = TextRenderer.MeasureText("ダウンロード済み", Font).Height + scalePadding;
        grid.RowTemplate.Height = TextRenderer.MeasureText("検証済み", Font).Height + scalePadding;
        foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (column.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill) continue;
            int minimum = TextRenderer.MeasureText(column.HeaderText, Font).Width + scalePadding * 2;
            column.MinimumWidth = Math.Max(40, minimum);
            if (column.Width < column.MinimumWidth) column.Width = column.MinimumWidth;
        }
    }
    TableLayoutPanel PathRow(string label, TextBox box, Action choose)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        p.ColumnStyles.Add(new(SizeType.Absolute, 155)); p.ColumnStyles.Add(new(SizeType.Percent, 100)); p.ColumnStyles.Add(new(SizeType.AutoSize));
        p.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0); p.Controls.Add(box, 1, 0);
        var b = new Button { Text = "参煥", AutoSize = true }; b.Click += (_, _) => choose(); p.Controls.Add(b, 2, 0); return p;
    }
    static FlowLayoutPanel Flow(Action a, string at, Action b, string bt, Action? c = null, string? ct = null, Action? d = null, string? dt = null)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        void Add(Action action, string title) { var x = new Button { Text = title, AutoSize = true }; x.Click += (_, _) => action(); p.Controls.Add(x); }
        Add(a, at); Add(b, bt); if (c is not null) Add(c, ct!); if (d is not null) Add(d, dt!); return p;
    }
    void PickFile(TextBox box, string filter) { using var d = new OpenFileDialog { Filter = filter }; if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName; }
    void AddChecksums() { using var d = new OpenFileDialog { Filter = "Checksum|*.manifest;*.sha512;*.blake3", Multiselect = true }; if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) AddChecksum(f); }
    void AddChecksum(string path) { if (!checksumList.Items.Contains(path)) checksumList.Items.Add(path); }
    void AddReferer() { try { RememberReferer(Inputs.ValidateReferer(refererBox.Text)); } catch (Exception ex) { Error(ex.Message); } }
    void UpdateReferer()
    {
        try
        {
            string value = Inputs.ValidateReferer(refererBox.Text);
            if (refererBox.SelectedIndex >= 0) refererBox.Items.RemoveAt(refererBox.SelectedIndex);
            settings.Referers = refererBox.Items.Cast<string>().ToList(); RememberReferer(value);
        }
        catch (Exception ex) { Error(ex.Message); }
    }
    void DeleteReferer()
    {
        if (refererBox.SelectedIndex < 0) return;
        string removed = (string)refererBox.Items[refererBox.SelectedIndex]!;
        refererBox.Items.RemoveAt(refererBox.SelectedIndex);
        settings.Referers = refererBox.Items.Cast<string>().ToList();
        if (Settings.SameReferer(settings.Referer, removed)) settings.Referer = "";
        refererBox.Text = settings.Referer;
    }
    void RememberReferer(string value)
    {
        settings.Referers = refererBox.Items.Cast<string>().ToList(); settings.RememberReferer(value);
        refererBox.BeginUpdate(); refererBox.Items.Clear(); refererBox.Items.AddRange(settings.Referers.Cast<object>().ToArray()); refererBox.EndUpdate();
        refererBox.Text = value;
    }
    void DestinationDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    void DestinationDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) if (paths is not null) {
            try { destBox.Text = Inputs.ValidateDestinationDrop(paths); }
            catch (FormatException ex) { Error(ex.Message); }
        }
    }
    void Error(string msg) => MessageBox.Show(this, msg, "Closed_Stack_Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
    async Task BeginAsync(bool failedOnly, bool resumeRun = false)
    {
        if (runTask is { IsCompleted: false }) return;
        try
        {
            if (!resumeRun)
            {
                var urls = Inputs.ReadUrls(urlBox.Text); duplicates = urls.Duplicates;
                var records = Inputs.ReadChecksums(checksumList.Items.Cast<string>());
                var plan = Inputs.Plan(urls, records);
                string dest = Path.GetFullPath(destBox.Text); Directory.CreateDirectory(dest);
                string probe = Path.Combine(dest, ".csd_write_" + Guid.NewGuid().ToString("N")); using (File.Create(probe)) { } File.Delete(probe);
                long needed = 0;
                foreach (var item in plan)
                {
                    if (item.Checksum?.Size is not long size) continue;
                    string target = Path.Combine(dest, item.FileName);
                    if (File.Exists(target) && new FileInfo(target).Length == size) continue;
                    long have = File.Exists(target + ".part") ? new FileInfo(target + ".part").Length : 0;
                    needed = checked(needed + Math.Max(0, size - have));
                }
                long? free = null; try { var drive = Path.GetPathRoot(dest); if (drive is not null) free = new DriveInfo(drive).AvailableFreeSpace; }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
                if (free < needed) throw new IOException("保存先の空き量が、既知の必要段릏る少なくなっています。");
                if (useReferer.Checked)
                {
                    if (string.IsNullOrWhiteSpace(refererBox.Text)) throw new FormatException("Refererを入力してください。");
                    RememberReferer(Inputs.ValidateReferer(refererBox.Text));
                }
                if (!failedOnly || statuses.Count == 0) { statuses.Clear(); statuses.AddRange(plan.Select(x => new DownloadStatus { Item = x, Total = x.Checksum?.Size })); }
                else if (!plan.Select(x => x.Url.Uri.AbsoluteUri).SequenceEqual(statuses.Select(x => x.Item.Url.Uri.AbsoluteUri))) throw new FormatException("URLリストが前回と異なります。");
                grid.Rows.Clear(); foreach (var s in statuses) grid.Rows.Add(s.State, s.Item.FileName, "", "", "", "", "", "", "", "", s.Item.Url.Original);
                SaveSettings();
            }
            running = new CancellationTokenSource(); SetButtons();
            var selected = statuses.Where(s => resumeRun ? s.State is "一時停止" or "待機" or "再試行待ち" : failedOnly ? s.State is "失敗" or "Checksum不一致" or "アクセス拒否" or "リンク切れ" : s.State == "彅機").ToArray();
            var token = running.Token; var semaphore = new SemaphoreSlim((int)concurrency.Value); string? referer = useReferer.Checked ? refererBox.Text : null;
            int retryCount = (int)retries.Value, timeoutSeconds = (int)timeout.Value; bool allowUnverified = allowNoChecksum.Checked; string destination = destBox.Text;
            runTask = Task.Run(async () =>
            {
                using var transfer = new Transfer(statuses.Select(x => x.Item));
                await Task.WhenAll(selected.Select(async s =>
                {
                    try
                    {
                        await semaphore.WaitAsync(token);
                        try
                        {
                            if (s.Item.Checksum is null && !allowUnverified) { s.State = "Checksumなし・スキップ"; Update(s); return; }
                            await transfer.RunAsync(s, destination, referer, retryCount, timeoutSeconds, Update, token);
                        }
                        finally { semaphore.Release(); }
                    }
                    catch (OperationCanceledException) { s.State = paused ? "一時停止" : "中止"; Update(s); }
                    catch (Exception ex) { s.State = "失敗�"; s.Detail = ex.Message; Update(s); }
                }));
            });
            await runTask;
        }
        catch (Exception ex) { Error(ex.Message); }
        finally { running?.Dispose(); running = null; SetButtons(); UpdateSummary(); }
    }
    void Update(DownloadStatus s)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (IsDisposed) return; int i = statuses.IndexOf(s); if (i < 0 || i >= grid.Rows.Count) return;
            var row = grid.Rows[i]; row.Cells[0].Value = s.State + (s.Detail.Length == 0 ? "" : " - " + s.Detail); row.Cells[1].Value = s.ResolvedName ?? s.Item.FileName;
            row.Cells[2].Value = s.Total?.ToString("N0") ?? "サイズ不明"; row.Cells[3].Value = s.Bytes.ToString("N0");
            row.Cells[4].Value = s.Total > 0 ? (100.0 * s.Bytes / s.Total.Value).ToString("F1") + "%" : "—";
            row.Cells[5].Value = s.BytesPerSecond > 0 ? (s.BytesPerSecond / 1024 / 1024).ToString("F2") + " MiB/s" : "—";
            row.Cells[6].Value = s.Total > s.Bytes && s.BytesPerSecond > 0 ? TimeSpan.FromSeconds((s.Total.Value - s.Bytes) / s.BytesPerSecond).ToString("hh\\:mm\\:ss") : "—";
            row.Cells[7].Value = s.State.Contains("検証済み") ? "一致" : s.State.Contains("未照合") ? "未照合" : s.State.Contains("サイズ確認") ? "サイズのみ" : "—";
            row.Cells[8].Value = s.Retries; row.Cells[9].Value = s.HttpStatus?.ToString() ?? ""; UpdateSummary();
        });
    }
    void UpdateSummary()
    {
        int verified = statuses.Count(x => x.State.Contains("検証済み")); int uncheckedDone = statuses.Count(x => x.State.Contains("未照合") && x.State.Contains("完了"));
        int finished = statuses.Count(x => x.State is "検証済み" or "検証済み・既存" or "完了（未照合）" or "完了（サイズ確認のみ）" or "Checksumなし・スキップ" or "既存（Checksumなし・未照合）");
        summary.Text = $"総URL {statuses.Count} / 重複除外 {duplicates} / 待機 {statuses.Count(x => x.State == "待機")} / ダウンロード中 {statuses.Count(x => x.State == "ダウンロード中")} / 検証済み {verified} / 未照合完了 {uncheckedDone} / スキップ {statuses.Count(x => x.State.Contains("スキップ"))} / 失敗 {statuses.Count(x => x.State is "失敗" or "Checksum不一致" or "アクセス拒否" or "リンク切れ")}";
        bar.Value = statuses.Count == 0 ? 0 : Math.Clamp((int)(100.0 * finished / statuses.Count), 0, 100);
    }
    void SetButtons() { bool active = runTask is { IsCompleted: false } || running is not null && !running.IsCancellationRequested; start.Enabled = !active; pause.Enabled = active && running is not null && !running.IsCancellationRequested; stop.Enabled = active && running is not null && !running.IsCancellationRequested; resume.Enabled = !active && paused; retryFailed.Enabled = !active && statuses.Any(x => x.State is "失敗" or "Checksum不一致" or "アクセス拒否" or "リンク切れ"); }
    void SaveSettings()
    {
        settings.UrlList = urlBox.Text; settings.Checksums = checksumList.Items.Cast<string>().ToList(); settings.Destination = destBox.Text;
        settings.UseReferer = useReferer.Checked; settings.Referers = refererBox.Items.Cast<string>().ToList();
        if (settings.Referers.Any(x => Settings.SameReferer(x, refererBox.Text))) settings.Referer = refererBox.Text;
        else if (!settings.Referers.Any(x => Settings.SameReferer(x, settings.Referer))) settings.Referer = "";
        settings.Concurrency = (int)concurrency.Value; settings.Retries = (int)retries.Value; settings.TimeoutSeconds = (int)timeout.Value;
        settings.AllowNoChecksum = allowNoChecksum.Checked; settings.FontSize = (int)Font.Size;
        if (WindowState == FormWindowState.Normal) { settings.X = Left; settings.Y = Top; settings.Width = Width; settings.Height = Height; }
        settings.ColumnWidths = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToList(); settings.Save(Settings.DefaultPath);
    }
    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (runTask is { IsCompleted: false })
        {
            if (MessageBox.Show(this, "ダウンロード中です。中止して終了しますか？", "確認", MessageBoxButtons.YesNo) != DialogResult.Yes) { e.Cancel = true; return; }
            running?.Cancel(); e.Cancel = true; Enabled = false;
            _ = CloseAfterRunAsync(); return;
        }
        try { SaveSettings(); } catch (Exception ex) { Error("設定を保存できません: " + ex.Message); }
    }
    async Task CloseAfterRunAsync() { if (runTask is not null) await runTask; if (!IsDisposed) { Enabled = true; Close(); } }
}
