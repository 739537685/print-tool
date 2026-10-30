using PrintAssistant.Forms;
using PrintAssistant.Printing;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant;

public sealed class TrayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly TaskRepository _repository;
    private readonly AuthService _auth;
    private readonly TlsCertificateService _tls;
    private readonly PrinterCatalog _catalog;
    private readonly PrintQueue _queue;
    private readonly Gateway _gateway;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startupItem;
    private readonly MessageReceiverWindow _messageReceiver;
    private MainForm? _mainForm;
    private HistoryForm? _historyForm;
    private bool _exiting;

    public TrayAppContext(bool startHidden)
    {
        AppPaths.EnsureCreated();
        AppLog.Initialize(AppPaths.LogsDir);

        _settings = AppSettings.Load(AppPaths.SettingsFile);
        _repository = new TaskRepository(
            AppPaths.TasksFile,
            AppPaths.SpoolDir,
            _settings.HistoryLimit,
            _settings.HistoryDays);
        _repository.Initialize();

        _auth = new AuthService(AppPaths.TokensFile);
        _tls = new TlsCertificateService(AppLog.Info);
        _tls.Ensure(AppPaths.CertsDir);

        _catalog = new PrinterCatalog();
        var renderer = new PdfRenderer();
        _queue = new PrintQueue(_repository, renderer, new LabelPrinter(), _settings);
        var preview = new PreviewService(renderer, AppPaths.SpoolDir, _settings.MaxPages);
        _gateway = new Gateway(_settings, _auth, _repository, _catalog, _queue, _tls, preview);

        var showMessageId = Program.RegisterWindowMessage(Program.ShowMainWindowMessageName);
        _messageReceiver = new MessageReceiverWindow(showMessageId, ShowMain);

        _startupItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled()
        };
        _startupItem.Click += (_, _) => ToggleStartup();

        _tray = BuildTray();
        AppLog.LogWritten += OnLogWritten;
        AppLog.Info($"打印助手 v{AppInfo.Version} 启动");

        _queue.StartAsync();
        _ = StartGatewayAsync();

        if (startHidden)
        {
            _tray.ShowBalloonTip(2000, "打印助手", "已在后台运行，双击托盘图标打开面板", ToolTipIcon.Info);
        }
        else
        {
            ShowMain();
        }
    }

    private NotifyIcon BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开面板", null, (_, _) => ShowMain());
        menu.Items.Add("打印历史", null, (_, _) => ShowHistory());
        menu.Items.Add("复制配对码", null, (_, _) => CopyPairCode());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        menu.Opening += (_, _) =>
        {
            try
            {
                _startupItem.Checked = StartupRegistration.IsEnabled();
            }
            catch
            {
                // 忽略读取失败
            }
        };

        var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "打印助手",
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowMain();
        return tray;
    }

    private async Task StartGatewayAsync()
    {
        try
        {
            await _gateway.StartAsync();
            UpdateTrayText("打印助手 - 运行中");
            _mainForm?.UpdateServerState(true, null);
        }
        catch (Exception ex)
        {
            UpdateTrayText("打印助手 - 启动失败");
            _mainForm?.UpdateServerState(false, ex.Message);
            _tray.ShowBalloonTip(3000, "打印助手启动失败", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ShowMain()
    {
        if (_mainForm == null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(_settings, _auth, _catalog, _tls);
            _mainForm.HistoryRequested += ShowHistory;
            _mainForm.UpdateServerState(_gateway.IsRunning, _gateway.LastError);
            _mainForm.RefreshPrintersAsync();
        }

        _mainForm.SyncAutoStart();
        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private void ShowHistory()
    {
        if (_historyForm == null || _historyForm.IsDisposed)
        {
            _historyForm = new HistoryForm(_repository, Reprint);
            _historyForm.FormClosed += (_, _) => _historyForm = null;
        }

        _historyForm.Show();
        _historyForm.Activate();
    }

    private void Reprint(PrintTask original)
    {
        var printer = _catalog.ResolvePrinterName(original.Printer);
        var task = JobFactory.CreateReprint(AppPaths.SpoolDir, original);
        task.Printer = printer;
        _repository.Add(task);
        _queue.Enqueue(task);
        AppLog.Info($"手动重打任务 {task.TaskId}（原任务 {original.TaskId}）");
    }

    private void CopyPairCode()
    {
        try
        {
            Clipboard.SetText(_auth.PairCode);
            _tray.ShowBalloonTip(2000, "打印助手", $"配对码：{_auth.PairCode}", ToolTipIcon.Info);
        }
        catch
        {
            // 剪贴板被占用
        }
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.SetEnabled(_startupItem.Checked);
            AppLog.Info($"开机自启{(_startupItem.Checked ? "已开启" : "已关闭")}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"设置开机自启失败：{ex.Message}");
            MessageBox.Show($"设置开机自启失败：{ex.Message}", "打印助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnLogWritten(string line)
    {
        _mainForm?.AppendLog(line);
    }

    private void UpdateTrayText(string text)
    {
        try
        {
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
        catch
        {
            // 忽略托盘文本异常
        }
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _tray.Visible = false;
        AppLog.Info("打印助手退出");

        try
        {
            Task.Run(async () => await _gateway.DisposeAsync().AsTask())
                .Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 忽略退出异常
        }

        _queue.Dispose();
        _messageReceiver.Dispose();
        AppLog.LogWritten -= OnLogWritten;

        _historyForm?.Close();
        _mainForm?.CloseForExit();
        _tray.Dispose();
        ExitThread();
    }

    private sealed class MessageReceiverWindow : NativeWindow, IDisposable
    {
        private readonly uint _messageId;
        private readonly Action _onTrigger;

        public MessageReceiverWindow(uint messageId, Action onTrigger)
        {
            _messageId = messageId;
            _onTrigger = onTrigger;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (_messageId != 0 && m.Msg == _messageId)
            {
                _onTrigger();
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }
}
