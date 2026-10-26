using System.ComponentModel;

namespace ProcessPanel;

// 单个进程实例（按 PID 独立），主列表前后台各 TOP10 的行数据模型
public class ProcessItem : INotifyPropertyChanged
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";      // 进程名（exe，如 chrome）
    public string Display { get; set; } = "";    // 友好显示名
    public long MemoryBytes { get; set; }
    public string Path { get; set; } = "";       // 可执行文件完整路径

    public bool IsForeground { get; set; }       // 是否有可见顶层窗口（前台应用）
    public bool IsActive { get; set; }           // 是否当前活动窗口进程
    public int Rank { get; set; }                // 区域内排名，1-based
    public double BarRatio { get; set; }         // 占用条比例 0~1（相对本区域第一名）

    public string MemText => FormatMem(MemoryBytes);
    public string RankText => Rank > 0 ? Rank.ToString("00") : "";
    public string PidText => $"PID {Pid}";

    // 一次性扫光：true 时播放约 0.5 秒斜向光带（由统一的顺序调度器逐项驱动）
    private bool _isShining;
    public bool IsShining
    {
        get => _isShining;
        set { _isShining = value; OnChanged(nameof(IsShining)); }
    }

    // 扫光完成后的常驻彩色边框（直到收起）
    private bool _isLit;
    public bool IsLit
    {
        get => _isLit;
        set { _isLit = value; OnChanged(nameof(IsLit)); }
    }

    // 内联确认状态（运行时交互，需通知）
    private bool _confirming;
    public bool Confirming
    {
        get => _confirming;
        set { _confirming = value; OnChanged(nameof(Confirming)); }
    }

    private string _confirmMessage = "";
    public string ConfirmMessage
    {
        get => _confirmMessage;
        set { _confirmMessage = value; OnChanged(nameof(ConfirmMessage)); }
    }

    public static string FormatMem(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
            : $"{bytes / (1024.0 * 1024):F0} MB";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
