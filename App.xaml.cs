using System;
using System.Threading;
using System.Windows;

namespace ProcessPanel;

public partial class App : Application
{
    // 全局单实例：不同目录/不同副本的 exe 也互斥，避免多开
    private static Mutex? _instanceMutex;
    private static bool _ownsMutex;
    private const string MutexName = @"Local\ProcessPanel.SingleInstance.B7E1";

    // 「请把已有面板亮一下」的跨进程信号：第二次启动时置顶已有窗口 3 秒，
    // 避免用户以为没启动（旧行为是静默退出，容易被误判为崩溃）
    private const string ShowEventName = @"Local\ProcessPanel.ShowExisting.B7E1";
    internal static EventWaitHandle? ShowEvent { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(false, MutexName);
        ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);

        // 用 WaitOne 而非 createdNew 判断：上一个实例刚退出时内核对象可能尚未回收，
        // createdNew 会误判为「已存在」导致新进程秒退。AbandonedMutex 表示上一实例异常退出，可直接接管。
        bool owned = false;
        try
        {
            owned = _instanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            try { ShowEvent.Set(); } catch { }
            try { _instanceMutex.Dispose(); } catch { }
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _ownsMutex = true;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }
        try { _instanceMutex?.Dispose(); } catch { }
        try { ShowEvent?.Dispose(); } catch { }
        _instanceMutex = null;
        ShowEvent = null;
        base.OnExit(e);
    }
}
