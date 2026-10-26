namespace ProcessPanel;

// 统一扫描调度状态机：单一任务 + 手动优先 + 合并待处理（无 UI 依赖，可测）
public class ScanScheduler
{
    private readonly object _gate = new();
    private bool _running;
    private bool _manualPending;

    // 尝试开始一次扫描。返回 true=允许启动；false=已有扫描在跑（手动请求被记录为待处理）
    public bool TryBegin(bool manual)
    {
        lock (_gate)
        {
            if (_running)
            {
                if (manual) _manualPending = true; // 合并，不无限排队
                return false;
            }
            _running = true;
            return true;
        }
    }

    // 扫描结束（正常/异常）。返回 true=扫描期间有手动请求，需立即补跑一次手动扫描
    public bool End()
    {
        lock (_gate)
        {
            _running = false;
            bool again = _manualPending;
            _manualPending = false;
            return again;
        }
    }
}
