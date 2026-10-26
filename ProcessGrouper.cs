using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcessPanel;

// 程序聚合（纯函数，可被测试直接验证）：
// 按可执行文件路径分组 → 组内前台优先、同状态内内存降序 → 组按聚合内存降序
public static class ProcessGrouper
{
    public const int MaxGroups = 80;

    public static List<AppGroup> Build(List<ProcessItem> all)
    {
        var groups = all
            .GroupBy(x => string.IsNullOrEmpty(x.Path) ? x.Name : x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // 前台实例在前，同状态内按内存降序
                var list = g.OrderByDescending(x => x.IsForeground)
                            .ThenByDescending(x => x.MemoryBytes)
                            .ToList();
                var first = list[0];
                var grp = new AppGroup
                {
                    Key = g.Key,
                    Name = first.Name,
                    Display = first.Display,
                    Path = first.Path,
                    TotalMemory = list.Sum(x => x.MemoryBytes),
                };

                // 子项内存条以组内最大值为基准
                long maxChild = list[0].MemoryBytes;
                foreach (var it in list)
                {
                    it.BarRatio = maxChild > 0 ? (double)it.MemoryBytes / maxChild : 0;
                    grp.Children.Add(it);
                }

                grp.AllForeground = list.All(x => x.IsForeground);
                grp.AllBackground = list.All(x => !x.IsForeground);
                grp.Mixed = !grp.AllForeground && !grp.AllBackground;
                return grp;
            })
            .OrderByDescending(g => g.TotalMemory)
            .Take(MaxGroups)
            .ToList();

        if (groups.Count > 0)
        {
            long max = groups[0].TotalMemory;
            for (int i = 0; i < groups.Count; i++)
            {
                groups[i].Rank = i + 1;
                groups[i].BarRatio = max > 0 ? (double)groups[i].TotalMemory / max : 0;
            }
        }
        return groups;
    }
}
