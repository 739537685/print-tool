using System.Diagnostics;
using PrintAssistant.Storage;

namespace PrintAssistant.Forms;

public sealed class HistoryForm : Form
{
    private readonly TaskRepository _repository;
    private readonly Action<PrintTask> _reprintRequested;
    private readonly DataGridView _grid = new();

    public HistoryForm(TaskRepository repository, Action<PrintTask> reprintRequested)
    {
        _repository = repository;
        _reprintRequested = reprintRequested;

        Text = "打印历史";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 520);
        MinimumSize = new Size(720, 400);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = SystemIcons.Application;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        var refresh = new Button { Text = "刷新", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        refresh.Click += (_, _) => Reload();

        var reprintButton = new Button { Text = "重打", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        reprintButton.Click += (_, _) => ReprintSelected();

        var open = new Button { Text = "打开 PDF", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        open.Click += (_, _) => OpenSelectedPdf();

        var close = new Button { Text = "关闭", AutoSize = true };
        close.Click += (_, _) => Close();

        actions.Controls.Add(refresh);
        actions.Controls.Add(reprintButton);
        actions.Controls.Add(open);
        actions.Controls.Add(close);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "时间", FillWeight = 15 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "打印机", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "份数", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "页数", FillWeight = 7 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "说明", FillWeight = 39 });
        _grid.CellDoubleClick += (_, _) => ReprintSelected();

        root.Controls.Add(actions, 0, 0);
        root.Controls.Add(_grid, 0, 1);
        Controls.Add(root);

        Reload();
    }

    private void Reload()
    {
        _grid.Rows.Clear();
        foreach (var task in _repository.Recent(200))
        {
            var index = _grid.Rows.Add(
                task.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm:ss"),
                StatusText(task.Status),
                task.Printer,
                task.Copies,
                task.Pages?.ToString() ?? "-",
                task.Message ?? task.Error ?? "");
            _grid.Rows[index].Tag = task.TaskId;
        }
    }

    private static string StatusText(string status) => status switch
    {
        PrintTaskStatus.Done => "成功",
        PrintTaskStatus.Failed => "失败",
        PrintTaskStatus.Interrupted => "已中断",
        PrintTaskStatus.Printing => "打印中",
        PrintTaskStatus.Queued => "排队中",
        _ => status
    };

    private PrintTask? SelectedTask()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "请先选择一条任务", "打印历史", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }

        var taskId = _grid.SelectedRows[0].Tag as string;
        var task = taskId == null ? null : _repository.Find(taskId);
        if (task == null)
        {
            MessageBox.Show(this, "任务记录已不存在", "打印历史", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return task;
    }

    private void ReprintSelected()
    {
        var task = SelectedTask();
        if (task == null)
        {
            return;
        }

        try
        {
            _reprintRequested(task);
            MessageBox.Show(this, "已加入打印队列", "重打", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "重打失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedPdf()
    {
        var task = SelectedTask();
        if (task == null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = task.PdfPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开 PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
