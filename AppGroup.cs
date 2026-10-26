using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ProcessPanel;

// 程序级聚合：同一可执行文件路径的所有进程实例归入一组，主列表只显示一个主项
public class AppGroup : INotifyPropertyChanged
{
    public string Key { get; set; } = "";       // 分组键 = 可执行文件完整路径
    public string Name { get; set; } = "";      // 进程名（exe）
    public string Display { get; set; } = "";   // 友好显示名
    public string Path { get; set; } = "";      // 可执行文件路径
    public long TotalMemory { get; set; }       // 组内全部实例的内存之和

    public ObservableCollection<ProcessItem> Children { get; } = new();

    public int Count => Children.Count;
    public string CountText => Children.Count > 1 ? $"×{Children.Count}" : "";
    public string MemText => ProcessItem.FormatMem(TotalMemory);
    public string RankText => Rank > 0 ? Rank.ToString("00") : "";
    public double BarRatio { get; set; }        // 相对列表首位的比例
    public int Rank { get; set; }

    // 前后台构成（来自子项真实的可见窗口状态）
    public bool AllForeground { get; set; }
    public bool AllBackground { get; set; }
    public bool Mixed { get; set; }

    // 悬停展开
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnChanged(nameof(IsExpanded)); }
    }

    // 整组结束的内联确认
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
