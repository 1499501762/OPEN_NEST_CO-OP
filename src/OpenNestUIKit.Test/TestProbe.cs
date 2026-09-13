using System.Collections.Generic;

namespace OpenNestUIKit.Test;

/// <summary>
/// 探针：测试页里每个可交互控件都调一次 <see cref="Hit"/>，于是 CLI 的 <c>click:</c> 命令
/// 能**自动判定点击是否真的生效**（点前/点后计数变化 → PASS/FAIL），而不是只看"命令跑完了"。
/// </summary>
public static class TestProbe
{
    private static readonly Dictionary<string, int> _hits = new();
    private static readonly List<string> _order = new();

    /// <summary>记录一次命中（key = 控件名/标签）。</summary>
    public static void Hit(string key)
    {
        if (string.IsNullOrEmpty(key)) key = "?";
        lock (_hits)
        {
            if (!_hits.ContainsKey(key)) _order.Add(key);
            _hits.TryGetValue(key, out int n);
            _hits[key] = n + 1;
        }
        TestLog.Info($"probe hit: {key} (total={Count(key)})");
    }

    /// <summary>取某 key 的命中次数。</summary>
    public static int Count(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        lock (_hits) return _hits.TryGetValue(key, out int n) ? n : 0;
    }

    /// <summary>命中总次数。</summary>
    public static int Total
    {
        get { lock (_hits) { int t = 0; foreach (var kv in _hits) t += kv.Value; return t; } }
    }

    /// <summary>命中记录快照（诊断/页面展示）。</summary>
    public static string[] Lines()
    {
        lock (_hits)
        {
            var list = new List<string>(_order.Count);
            for (int i = 0; i < _order.Count; i++)
            {
                string k = _order[i];
                _hits.TryGetValue(k, out int n);
                list.Add($"{k}  ×{n}");
            }
            return list.ToArray();
        }
    }

    /// <summary>清空。</summary>
    public static void Clear()
    {
        lock (_hits) { _hits.Clear(); _order.Clear(); }
    }
}
