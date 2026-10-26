using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcessPanel;

// 前台/后台分类 + TOP10 排名的纯计算（无 UI 依赖，可被测试直接验证）
public static class ProcessClassifier
{
    public const int TopCount = 10;

    // visiblePids：拥有可见顶层窗口的进程 PID；activePid：当前活动窗口进程 PID
    public static (List<ProcessItem> Foreground, List<ProcessItem> Background) Classify(
        List<ProcessItem> all, HashSet<int> visiblePids, int activePid)
    {
        foreach (var it in all)
        {
            it.IsForeground = visiblePids.Contains(it.Pid);
            it.IsActive = it.Pid == activePid;
        }

        var fg = all.Where(x => x.IsForeground).OrderByDescending(x => x.MemoryBytes).Take(TopCount).ToList();
        var bg = all.Where(x => !x.IsForeground).OrderByDescending(x => x.MemoryBytes).Take(TopCount).ToList();
        ApplyRank(fg);
        ApplyRank(bg);
        return (fg, bg);
    }

    // 排名 1-based；占用条以本区域第一名内存为 100% 基准
    private static void ApplyRank(List<ProcessItem> list)
    {
        if (list.Count == 0) return;
        long max = list[0].MemoryBytes;
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Rank = i + 1;
            list[i].BarRatio = max > 0 ? (double)list[i].MemoryBytes / max : 0;
        }
    }
}
