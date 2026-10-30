using System.Diagnostics;
using PrintAssistant.Printing;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant.Forms;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly AuthService _auth;
    private readonly PrinterCatalog _catalog;
    private readonly TlsCertificateService _tls;

    private readonly Label _statusLabel = new();
    private readonly Label _wsLabel = new();
    private readonly Label _wssLabel = new();
    private readonly Label _codeLabel = new();
    private readonly ListView _printerList = new();
    private readonly CheckBox _autoStartCheck = new();
    private readonly TextBox _logBox = new();
    private readonly Button _refreshPrintersButton = new();
    private bool _allowClose;

    public MainForm(
        AppSettings settings,
        AuthService auth,
        PrinterCatalog catalog,
        TlsCertificateService tls)
    {
        _settings = settings;
        _auth = auth;
        _catalog = catalog;
        _tls = tls;

        Text = "打印助手";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 720);
        MinimumSize = new Size(600, 640);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = SystemIcons.Application;

        BuildUi();
        RefreshPairingCode();
        UpdateServerState(false, null);
    }

    public event Action? HistoryRequested;

    public void UpdateServerState(bool running, string? error)
    {
        Ui(() =>
        {
            if (error != null)
            {
                _statusLabel.Text = "● 启动失败";
                _statusLabel.ForeColor = Color.Firebrick;
            }
            else if (running)
            {
                _statusLabel.Text = "● 运行中";
                _statusLabel.ForeColor = Color.SeaGreen;
            }
            else
            {
                _statusLabel.Text = "● 正在启动";
                _statusLabel.ForeColor = Color.DarkOrange;
            }

            _wsLabel.Text = running
                ? $"ws://127.0.0.1:{_settings.WsPort}"
                : $"ws://127.0.0.1:{_settings.WsPort}（未启动）";

            _wssLabel.Text = !running
                ? $"wss://127.0.0.1:{_settings.WssPort}（未启动）"
                : _tls.Certificate == null
                    ? "wss 未启用（证书不可用）"
                    : $"wss://127.0.0.1:{_settings.WssPort}（{(_tls.Trusted ? "证书已信任" : "证书未信任")}）";
        });
    }

    public void RefreshPairingCode()
    {
        Ui(() =>
        {
            var code = _auth.PairCode;
            _codeLabel.Text = $"{code[..3]} {code[3..]}";
        });
    }

    public void RefreshPrintersAsync()
    {
        if (_refreshPrintersButton.Enabled)
        {
            _refreshPrintersButton.Enabled = false;
        }

        _ = Task.Run(() =>
        {
            IReadOnlyList<PrinterInfo> printers;
            try
            {
                printers = _catalog.GetPrinters(forceRefresh: true);
            }
            catch (Exception ex)
            {
                Ui(() => _wsLabel.Text = "读取打印机失败：" + ex.Message);
                return;
            }

            Ui(() =>
            {
                _printerList.BeginUpdate();
                _printerList.Items.Clear();
                foreach (var printer in printers)
                {
                    var item = new ListViewItem(printer.Name);
                    item.SubItems.Add(printer.IsDefault ? "是" : "");
                    item.SubItems.Add(printer.Status == "ready" ? "就绪" : "不可用");
                    item.SubItems.Add(printer.Papers.Count.ToString());
                    _printerList.Items.Add(item);
                }

                _printerList.EndUpdate();
                _refreshPrintersButton.Enabled = true;
            });
        });
    }

    public void AppendLog(string line)
    {
        Ui(() =>
        {
            if (_logBox.TextLength > 200_000)
            {
                _logBox.Text = _logBox.Text[^100_000..];
            }

            _logBox.AppendText(line + Environment.NewLine);
        });
    }

    public void SyncAutoStart()
    {
        Ui(() => _autoStartCheck.Checked = StartupRegistration.IsEnabled());
    }

    public void HideToTray()
    {
        Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        Controls.Add(root);

        root.Controls.Add(BuildStatusGroup(), 0, 0);
        root.Controls.Add(BuildPairingGroup(), 0, 1);
        root.Controls.Add(BuildPrinterGroup(), 0, 2);
        root.Controls.Add(BuildActions(), 0, 3);
        root.Controls.Add(BuildLogGroup(), 0, 4);
    }

    private Control BuildStatusGroup()
    {
        var group = new GroupBox
        {
            Text = "运行状态",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 10)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 3
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
        _statusLabel.Margin = new Padding(0, 4, 12, 4);

        _wsLabel.AutoSize = true;
        _wsLabel.Margin = new Padding(0, 4, 0, 4);
        _wssLabel.AutoSize = true;
        _wssLabel.Margin = new Padding(0, 4, 0, 4);

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.SetColumnSpan(_statusLabel, 2);
        layout.Controls.Add(new Label { Text = "HTTP 页面", AutoSize = true, ForeColor = Color.DimGray }, 0, 1);
        layout.Controls.Add(_wsLabel, 1, 1);
        layout.Controls.Add(new Label { Text = "HTTPS 页面", AutoSize = true, ForeColor = Color.DimGray }, 0, 2);
        layout.Controls.Add(_wssLabel, 1, 2);

        group.Controls.Add(layout);
        return group;
    }

    private Control BuildPairingGroup()
    {
        var group = new GroupBox
        {
            Text = "配对",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 10)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _codeLabel.AutoSize = true;
        _codeLabel.Font = new Font("Consolas", 26F, FontStyle.Bold);
        _codeLabel.ForeColor = Color.FromArgb(20, 60, 140);
        _codeLabel.Margin = new Padding(0, 4, 16, 4);

        var refresh = new Button { Text = "刷新", AutoSize = true, Margin = new Padding(0, 10, 8, 4) };
        refresh.Click += (_, _) =>
        {
            _auth.RefreshPairCode();
            RefreshPairingCode();
            AppendLog($"{DateTime.Now:HH:mm:ss} 配对码已刷新");
        };

        var copy = new Button { Text = "复制", AutoSize = true, Margin = new Padding(0, 10, 8, 4) };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_auth.PairCode);
            }
            catch
            {
                // 剪贴板被占用
            }
        };

        var hint = new Label
        {
            Text = "在网页中输入配对码，配对成功后网页会保存 token，无需重复输入。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 4)
        };

        layout.Controls.Add(_codeLabel, 0, 0);
        layout.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(copy, 2, 0);
        layout.Controls.Add(hint, 0, 1);
        layout.SetColumnSpan(hint, 4);

        group.Controls.Add(layout);
        return group;
    }

    private Control BuildPrinterGroup()
    {
        var group = new GroupBox
        {
            Text = "打印机",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 10)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _refreshPrintersButton.Text = "刷新打印机";
        _refreshPrintersButton.AutoSize = true;
        _refreshPrintersButton.Margin = new Padding(0, 0, 0, 8);
        _refreshPrintersButton.Click += (_, _) => RefreshPrintersAsync();

        _printerList.Dock = DockStyle.Fill;
        _printerList.View = View.Details;
        _printerList.FullRowSelect = true;
        _printerList.GridLines = true;
        _printerList.HideSelection = false;
        _printerList.Columns.Add("名称", 320);
        _printerList.Columns.Add("默认", 50);
        _printerList.Columns.Add("状态", 70);
        _printerList.Columns.Add("纸张数", 70);

        layout.Controls.Add(_refreshPrintersButton, 0, 0);
        layout.Controls.Add(_printerList, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildActions()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 8, 0, 8)
        };

        var history = new Button { Text = "打印历史", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        history.Click += (_, _) => HistoryRequested?.Invoke();

        var openData = new Button { Text = "打开数据目录", AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        openData.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppPaths.Root,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog($"{DateTime.Now:HH:mm:ss} 打开目录失败：{ex.Message}");
            }
        };

        _autoStartCheck.Text = "开机自启";
        _autoStartCheck.AutoSize = true;
        _autoStartCheck.Margin = new Padding(0, 8, 16, 0);
        _autoStartCheck.CheckedChanged += (_, _) =>
        {
            try
            {
                StartupRegistration.SetEnabled(_autoStartCheck.Checked);
                AppendLog($"{DateTime.Now:HH:mm:ss} 开机自启{(_autoStartCheck.Checked ? "已开启" : "已关闭")}");
            }
            catch (Exception ex)
            {
                AppendLog($"{DateTime.Now:HH:mm:ss} 设置开机自启失败：{ex.Message}");
            }
        };

        var hide = new Button { Text = "隐藏到托盘", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        hide.Click += (_, _) => Hide();

        panel.Controls.Add(history);
        panel.Controls.Add(openData);
        panel.Controls.Add(_autoStartCheck);
        panel.Controls.Add(hide);
        return panel;
    }

    private Control BuildLogGroup()
    {
        var group = new GroupBox
        {
            Text = "最近日志",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 10)
        };

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ReadOnly = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Font = new Font("Consolas", 8.5F);
        _logBox.BackColor = Color.White;
        group.Controls.Add(_logBox);
        return group;
    }

    private void Ui(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch
            {
                // 窗口正在关闭
            }

            return;
        }

        action();
    }
}
