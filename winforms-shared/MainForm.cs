using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Winzilla
{
    public sealed class MainForm : Form
    {
        private readonly Settings _settings = Settings.Load();
        private readonly string _downloadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        private readonly ComboBox _protocolBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _hostBox = new TextBox();
        private readonly TextBox _portBox = new TextBox();
        private readonly TextBox _userBox = new TextBox();
        private readonly TextBox _passwordBox = new TextBox { UseSystemPasswordChar = true };
        private readonly Button _connectButton = new Button { Text = "Connect" };
        private readonly Label _remoteTitle = new Label { Text = "Remote (not connected)", AutoSize = true };
        private readonly TextBox _pathBox = new TextBox();
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            AllowDrop = true,
        };
        private readonly ImageList _icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        private readonly Label _status = new Label { Text = "Ready", AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ProgressBar _progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        private readonly Button _cancelButton = new Button { Text = "Cancel", AutoSize = true, Visible = false, Anchor = AnchorStyles.Left };

        private readonly TransferProgress _transfer = new TransferProgress();
        private readonly System.Windows.Forms.Timer _transferTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly TableLayoutPanel _transferPanel = new TableLayoutPanel { Visible = false };
        private readonly Label _transferPath = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label _transferElapsed = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _transferLeft = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly PercentBar _transferBar = new PercentBar { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        private readonly Label _transferBytes = new Label { AutoSize = true, Anchor = AnchorStyles.Left };

        private IRemoteClient? _client;
        private string _remotePath = "/";
        private IReadOnlyList<FileEntry> _remoteItems = new List<FileEntry>();
        private SortColumn _sortColumn = SortColumn.Name;
        private bool _sortAscending = true;
        private int _busy;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public MainForm()
        {
            Text = "Winzilla";
            Font = SystemFonts.MessageBoxFont ?? Font;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Scale(980), Scale(620));
            MinimumSize = new Size(Scale(820), Scale(420));

            BuildLayout();
            WireEvents();

            _protocolBox.Items.AddRange(new object[] { "SFTP", "FTP" });
            _protocolBox.SelectedIndex = (int)_settings.Protocol;
            _hostBox.Text = _settings.Host;
            _portBox.Text = _settings.Port.ToString();
            _userBox.Text = _settings.User;
            UpdateSortHeaders();

            AcceptButton = _connectButton;
        }

        // ---------- Layout ----------

        private int Scale(int logical) => LogicalToDeviceUnits(logical);

        private void BuildLayout()
        {
            int gap = Scale(8);

            _remoteTitle.Font = new Font(Font, FontStyle.Bold);
            _remoteTitle.Margin = new Padding(0, Scale(10), 0, Scale(4));

            _list.SmallImageList = _icons;
            _list.Columns.Add("Name", Scale(400));
            _list.Columns.Add("Size", Scale(90), HorizontalAlignment.Right);
            _list.Columns.Add("Date modified", Scale(150));
            _list.Margin = Padding.Empty;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(gap * 3 / 2, gap, gap * 3 / 2, gap),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(BuildConnectionBar(gap), 0, 0);
            root.Controls.Add(_remoteTitle, 0, 1);
            root.Controls.Add(BuildPathBar(gap), 0, 2);
            root.Controls.Add(_list, 0, 3);
            root.Controls.Add(BuildTransferPanel(gap), 0, 4);
            root.Controls.Add(BuildStatusBar(gap), 0, 5);

            Controls.Add(root);
        }

        /// <summary>FileZilla-style "source → target / elapsed / left / [bar] / bytes (rate)" panel.</summary>
        private Control BuildTransferPanel(int gap)
        {
            var panel = _transferPanel;
            panel.Dock = DockStyle.Fill;
            panel.AutoSize = true;
            panel.ColumnCount = 4;
            panel.RowCount = 2;
            panel.Margin = new Padding(0, Scale(8), 0, 0);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(200)));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            int wide = gap * 3;
            _transferPath.Height = Font.Height + Scale(6);
            _transferPath.Margin = new Padding(0, 0, 0, Scale(4));
            _transferElapsed.Margin = new Padding(0, 0, wide, 0);
            _transferLeft.Margin = new Padding(0, 0, wide, 0);
            _transferBar.Height = Font.Height + Scale(8);
            _transferBar.Margin = new Padding(0, 0, wide, 0);
            _transferBytes.Margin = Padding.Empty;

            panel.Controls.Add(_transferPath, 0, 0);
            panel.SetColumnSpan(_transferPath, 4);
            panel.Controls.Add(_transferElapsed, 0, 1);
            panel.Controls.Add(_transferLeft, 1, 1);
            panel.Controls.Add(_transferBar, 2, 1);
            panel.Controls.Add(_transferBytes, 3, 1);
            return panel;
        }

        private Control BuildStatusBar(int gap)
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, Scale(6), 0, 0),
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(160) + gap));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute, _pathBox.PreferredHeight + Scale(4)));

            _status.Margin = new Padding(0, 0, gap, 0);
            _progress.Margin = new Padding(0, 0, gap, 0);
            _progress.Height = Scale(14);
            _cancelButton.Margin = Padding.Empty;
            _cancelButton.MinimumSize = new Size(Scale(72), _pathBox.PreferredHeight + Scale(4));

            bar.Controls.Add(_status, 0, 0);
            bar.Controls.Add(_progress, 1, 0);
            bar.Controls.Add(_cancelButton, 2, 0);
            return bar;
        }

        private Control BuildConnectionBar(int gap)
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 6,
                RowCount = 2,
                Margin = Padding.Empty,
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(90)));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(70)));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(150)));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(160)));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var fields = new (string Label, Control Input)[]
            {
                ("Protocol", _protocolBox), ("Host", _hostBox), ("Port", _portBox),
                ("Username", _userBox), ("Password", _passwordBox),
            };
            for (int i = 0; i < fields.Length; i++)
            {
                bar.Controls.Add(new Label
                {
                    Text = fields[i].Label,
                    AutoSize = true,
                    Margin = new Padding(0, 0, gap, Scale(3)),
                }, i, 0);
                fields[i].Input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                fields[i].Input.Margin = new Padding(0, 0, gap, 0);
                bar.Controls.Add(fields[i].Input, i, 1);
            }

            _connectButton.AutoSize = true;
            _connectButton.MinimumSize = new Size(Scale(100), _hostBox.PreferredHeight + Scale(4));
            _connectButton.Padding = new Padding(Scale(6), 0, Scale(6), 0);
            _connectButton.Anchor = AnchorStyles.Left;
            _connectButton.Margin = Padding.Empty;
            bar.Controls.Add(_connectButton, 5, 1);
            return bar;
        }

        private Control BuildPathBar(int gap)
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 8,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, Scale(6)),
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            int height = _pathBox.PreferredHeight + Scale(4);
            Button Action(string text, EventHandler onClick, bool last = false)
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    MinimumSize = new Size(Scale(72), height),
                    Padding = new Padding(Scale(4), 0, Scale(4), 0),
                    Margin = new Padding(0, 0, last ? 0 : Scale(4), 0),
                    Anchor = AnchorStyles.Left,
                };
                b.Click += onClick;
                return b;
            }

            _pathBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _pathBox.Margin = new Padding(Scale(2), 0, gap, 0);

            bar.Controls.Add(Action("Up", RemoteUp_Click), 0, 0);
            bar.Controls.Add(Action("Refresh", RemoteRefresh_Click), 1, 0);
            bar.Controls.Add(_pathBox, 2, 0);
            bar.Controls.Add(Action("Upload…", Upload_Click), 3, 0);
            bar.Controls.Add(Action("Download", Download_Click), 4, 0);
            bar.Controls.Add(Action("New folder", RemoteNewFolder_Click), 5, 0);
            bar.Controls.Add(Action("Rename", RemoteRename_Click), 6, 0);
            bar.Controls.Add(Action("Delete", RemoteDelete_Click, last: true), 7, 0);
            return bar;
        }

        /// <summary>Keeps the Name column filling whatever width the other columns leave.</summary>
        private void FitNameColumn()
        {
            int others = _list.Columns[1].Width + _list.Columns[2].Width;
            int available = _list.ClientSize.Width - others;
            _list.Columns[0].Width = Math.Max(Scale(150), available);
        }

        private void WireEvents()
        {
            _protocolBox.SelectedIndexChanged += Protocol_Changed;
            _connectButton.Click += Connect_Click;
            _cancelButton.Click += (_, __) => _cts.Cancel();
            _pathBox.KeyDown += RemotePath_KeyDown;
            _list.ColumnClick += Sort_Click;
            _list.DoubleClick += RemoteList_DoubleClick;
            _list.KeyDown += RemoteList_KeyDown;
            _list.DragEnter += List_DragOver;
            _list.DragOver += List_DragOver;
            _list.DragDrop += List_DragDrop;
            _list.Resize += (_, __) => FitNameColumn();
            _list.ColumnWidthChanged += (_, e) => { if (e.ColumnIndex != 0) FitNameColumn(); };
            FormClosed += (_, __) => _client?.Dispose();
            FormClosed += (_, __) => _transferTimer.Dispose();
            _transferTimer.Tick += (_, __) => UpdateTransferUi();
        }

        // ---------- Connection ----------

        private void Protocol_Changed(object? sender, EventArgs e)
        {
            bool sftp = _protocolBox.SelectedIndex == 0;
            if (_portBox.Text == "22" || _portBox.Text == "21" || _portBox.Text.Length == 0)
                _portBox.Text = sftp ? "22" : "21";
        }

        private async void Connect_Click(object? sender, EventArgs e)
        {
            if (_client != null)
            {
                Disconnect();
                return;
            }

            string host = _hostBox.Text.Trim();
            if (host.Length == 0) { SetStatus("Enter a host."); return; }
            var protocol = _protocolBox.SelectedIndex == 0 ? Protocol.Sftp : Protocol.Ftp;
            if (!int.TryParse(_portBox.Text, out int port) || port < 1 || port > 65535)
                port = protocol == Protocol.Sftp ? 22 : 21;
            string user = _userBox.Text.Trim();
            var conn = new ConnectionSettings(protocol, host, port, user, _passwordBox.Text);

            _settings.Protocol = protocol;
            _settings.Host = host;
            _settings.Port = port;
            _settings.User = user;
            _settings.Save();

            IRemoteClient client = protocol == Protocol.Sftp ? (IRemoteClient)new SftpRemoteClient(_settings) : new FtpRemoteClient();
            _connectButton.Enabled = false;
            bool connected = false;
            try
            {
                await RunOpAsync(async ct =>
                {
                    SetStatus($"Connecting to {host}:{port}…");
                    await client.ConnectAsync(conn, ct);
                    _client = client;
                    _remotePath = await client.GetHomeAsync(ct);
                    await LoadRemoteAsync(_remotePath, ct);
                    connected = true;
                });
            }
            finally { _connectButton.Enabled = true; }

            if (!connected)
            {
                _client = null;
                client.Dispose();
                return;
            }
            _connectButton.Text = "Disconnect";
            _remoteTitle.Text = $"Remote ({user}@{host})";
            SetStatus($"Connected to {host}");
        }

        private void Disconnect()
        {
            var client = _client;
            _client = null;
            client?.Dispose();
            _list.Items.Clear();
            _remoteItems = new List<FileEntry>();
            _pathBox.Text = "";
            _remoteTitle.Text = "Remote (not connected)";
            _connectButton.Text = "Connect";
            SetStatus("Disconnected");
        }

        // ---------- Busy / status ----------

        private void SetStatus(string text)
        {
            if (InvokeRequired) BeginInvoke(new Action(() => _status.Text = text));
            else _status.Text = text;
        }

        private void SetBusyVisible(bool visible) => _progress.Visible = _cancelButton.Visible = visible;

        private async Task RunOpAsync(Func<CancellationToken, Task> work)
        {
            if (_busy++ == 0) _cts = new CancellationTokenSource();
            SetBusyVisible(true);
            try { await work(_cts.Token); }
            catch (OperationCanceledException) { SetStatus("Cancelled"); }
            catch (Exception) when (_cts.IsCancellationRequested) { SetStatus("Cancelled"); }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (--_busy == 0) SetBusyVisible(false);
            }
        }

        private bool Confirm(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        private string? Prompt(string title, string initial = "")
        {
            using (var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(340, 90),
            })
            {
                var box = new TextBox { Text = initial, Left = 12, Top = 12, Width = 316 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 172, Top = 52 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 253, Top = 52 };
                dlg.Controls.AddRange(new Control[] { box, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                dlg.Shown += (_, __) => { box.Focus(); box.SelectAll(); };
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                string name = box.Text.Trim();
                return name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 ? null : name;
            }
        }

        // ---------- Remote pane ----------

        private async Task LoadRemoteAsync(string path, CancellationToken ct)
        {
            if (_client == null) return;
            _remoteItems = await _client.ListAsync(path, ct);
            _remotePath = path;
            _pathBox.Text = path;
            ApplySort();
        }

        private IEnumerable<FileEntry> SelectedEntries() =>
            _list.SelectedItems.Cast<ListViewItem>().Select(i => (FileEntry)i.Tag!).ToList();

        private void Sort_Click(object? sender, ColumnClickEventArgs e)
        {
            var column = (SortColumn)e.Column;
            _sortAscending = column != _sortColumn || !_sortAscending;
            _sortColumn = column;
            ApplySort();
        }

        private void ApplySort()
        {
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var entry in FileEntry.Sort(_remoteItems, _sortColumn, _sortAscending))
                {
                    var item = new ListViewItem(entry.Name, ShellIcons.EnsureIcon(_icons, entry.Name, entry.IsDirectory))
                    {
                        Tag = entry,
                    };
                    item.SubItems.Add(entry.SizeText);
                    item.SubItems.Add(entry.ModifiedText);
                    _list.Items.Add(item);
                }
            }
            finally { _list.EndUpdate(); }
            UpdateSortHeaders();
        }

        private void UpdateSortHeaders()
        {
            string arrow = _sortAscending ? " ▲" : " ▼";
            _list.Columns[0].Text = "Name" + (_sortColumn == SortColumn.Name ? arrow : "");
            _list.Columns[1].Text = "Size" + (_sortColumn == SortColumn.Size ? arrow : "");
            _list.Columns[2].Text = "Date modified" + (_sortColumn == SortColumn.Modified ? arrow : "");
        }

        private async void RemoteUp_Click(object? sender, EventArgs e)
        {
            if (_client == null) return;
            await RunOpAsync(ct => LoadRemoteAsync(RemotePath.Parent(_remotePath), ct));
        }

        private async void RemoteRefresh_Click(object? sender, EventArgs e)
        {
            if (_client == null) return;
            await RunOpAsync(ct => LoadRemoteAsync(_remotePath, ct));
        }

        private async void RemotePath_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter || _client == null) return;
            e.SuppressKeyPress = true;
            string path = _pathBox.Text.Trim();
            await RunOpAsync(async ct =>
            {
                try { await LoadRemoteAsync(path, ct); }
                catch (Exception) { _pathBox.Text = _remotePath; throw; }
            });
        }

        private async void RemoteList_DoubleClick(object? sender, EventArgs e)
        {
            var entry = SelectedEntries().FirstOrDefault();
            if (entry == null) return;
            if (entry.IsDirectory) await RunOpAsync(ct => LoadRemoteAsync(entry.FullPath, ct));
            else await DownloadEntriesAsync(new List<FileEntry> { entry });
        }

        private async void RemoteList_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) RemoteDelete_Click(sender, e);
            else if (e.KeyCode == Keys.Enter && SelectedEntries().FirstOrDefault() is FileEntry d && d.IsDirectory)
                await RunOpAsync(ct => LoadRemoteAsync(d.FullPath, ct));
        }

        private async void RemoteNewFolder_Click(object? sender, EventArgs e)
        {
            var client = _client;
            if (client == null) return;
            var name = Prompt("New remote folder");
            if (name == null) return;
            await RunOpAsync(async ct =>
            {
                await client.CreateDirectoryAsync(RemotePath.Combine(_remotePath, name), ct);
                await LoadRemoteAsync(_remotePath, ct);
            });
        }

        private async void RemoteRename_Click(object? sender, EventArgs e)
        {
            var client = _client;
            var entry = SelectedEntries().FirstOrDefault();
            if (client == null || entry == null) return;
            var name = Prompt("Rename", entry.Name);
            if (name == null || name == entry.Name) return;
            await RunOpAsync(async ct =>
            {
                await client.RenameAsync(entry.FullPath, RemotePath.Combine(_remotePath, name), ct);
                await LoadRemoteAsync(_remotePath, ct);
            });
        }

        private async void RemoteDelete_Click(object? sender, EventArgs e)
        {
            var client = _client;
            var entries = SelectedEntries().ToList();
            if (client == null || entries.Count == 0) return;
            if (!Confirm("Delete", $"Delete {entries.Count} selected remote item(s)? This cannot be undone.")) return;
            await RunOpAsync(async ct =>
            {
                foreach (var entry in entries) await client.DeleteAsync(entry, ct);
                await LoadRemoteAsync(_remotePath, ct);
            });
        }

        // ---------- Transfers ----------

        private async void Upload_Click(object? sender, EventArgs e)
        {
            if (_client == null) return;
            using (var dlg = new OpenFileDialog { Multiselect = true, Title = "Upload files" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                await UploadPathsAsync(dlg.FileNames);
            }
        }

        private async Task UploadPathsAsync(IReadOnlyCollection<string> paths)
        {
            var client = _client;
            if (client == null || paths.Count == 0) return;
            await RunOpAsync(async ct =>
            {
                string target = _remotePath;
                BeginTransferUi();
                try
                {
                    foreach (var p in paths)
                        await TransferService.UploadAsync(client, p, target, SetStatus, _transfer, ct);
                }
                finally { EndTransferUi(); }
                SetStatus($"Uploaded {paths.Count} item(s)");
                await LoadRemoteAsync(_remotePath, ct);
            });
        }

        private async void Download_Click(object? sender, EventArgs e) =>
            await DownloadEntriesAsync(SelectedEntries().ToList());

        private async Task DownloadEntriesAsync(List<FileEntry> entries)
        {
            var client = _client;
            if (client == null || entries.Count == 0) return;
            await RunOpAsync(async ct =>
            {
                Directory.CreateDirectory(_downloadPath);
                BeginTransferUi();
                try
                {
                    foreach (var entry in entries)
                        await TransferService.DownloadAsync(client, entry, _downloadPath, SetStatus, _transfer, ct);
                }
                finally { EndTransferUi(); }
                SetStatus($"Downloaded {entries.Count} item(s) to {_downloadPath}");
            });
        }

        private void BeginTransferUi()
        {
            _transfer.Reset();
            UpdateTransferUi();
            _progress.Visible = false;
            _transferPanel.Visible = true;
            _transferTimer.Start();
        }

        private void EndTransferUi()
        {
            _transferTimer.Stop();
            _transferPanel.Visible = false;
            _progress.Visible = _busy > 0;
        }

        private void UpdateTransferUi()
        {
            string? source = _transfer.Source;
            if (source == null)
            {
                _transferPath.Text = "Preparing…";
                _transferElapsed.Text = _transferLeft.Text = _transferBytes.Text = _transferBar.Text = "";
                _transferBar.Value = 0;
                return;
            }

            long total = _transfer.Total, done = _transfer.Transferred;
            TimeSpan elapsed = _transfer.Elapsed;
            double rate = elapsed.TotalSeconds >= 0.5 ? done / elapsed.TotalSeconds : 0;
            double percent = total > 0 ? done * 100.0 / total : 0;

            _transferPath.Text = source + "  →  " + _transfer.Target;
            _transferElapsed.Text = FormatDuration(elapsed) + " elapsed";
            _transferLeft.Text = (rate > 0 && total > 0
                ? FormatDuration(TimeSpan.FromSeconds(Math.Max(0, total - done) / rate))
                : "--:--:--") + " left";
            _transferBar.Value = percent;
            _transferBar.Text = total > 0 ? percent.ToString("0.0") + "%" : "";
            _transferBytes.Text = $"{done:N0} bytes ({FormatRate(rate)})";
        }

        private static string FormatDuration(TimeSpan t) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        private static string FormatRate(double bytesPerSecond)
        {
            string[] units = { "B/s", "KiB/s", "MiB/s", "GiB/s" };
            int unit = 0;
            while (bytesPerSecond >= 1024 && unit < units.Length - 1)
            {
                bytesPerSecond /= 1024;
                unit++;
            }
            return bytesPerSecond.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        // ---------- Drag & drop (upload) ----------

        private void List_DragOver(object? sender, DragEventArgs e) =>
            e.Effect = _client != null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

        private async void List_DragDrop(object? sender, DragEventArgs e)
        {
            if (_client == null || e.Data == null || !(e.Data.GetData(DataFormats.FileDrop) is string[] paths)) return;
            await UploadPathsAsync(paths);
        }
    }
}
