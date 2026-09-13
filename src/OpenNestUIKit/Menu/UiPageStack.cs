using System;
using System.Collections.Generic;

namespace OpenNestUIKit.Menu;

/// <summary>
/// 页面栈（"子菜单"的载体）：<see cref="Push"/> 进入子页、<see cref="Pop"/> 返回上级。
/// 栈深 &gt; 1 时界面显示面包屑；栈底时 <see cref="Pop"/> 无效果（由菜单窗口决定是否关闭）。
/// </summary>
public sealed class UiPageStack
{
    private readonly List<string> _ids = new();

    /// <summary>栈变化（进入/返回）→ 界面刷新标题/面包屑/返回按钮。</summary>
    public event Action Changed;

    /// <summary>当前页 id（栈空时为空串）。</summary>
    public string Current => _ids.Count > 0 ? _ids[_ids.Count - 1] : "";

    /// <summary>栈深。</summary>
    public int Depth => _ids.Count;

    /// <summary>是否可返回（栈深 &gt; 1）。</summary>
    public bool CanGoBack => _ids.Count > 1;

    /// <summary>当前栈内容（拷贝，诊断用）。</summary>
    public string[] Path
    {
        get { lock (_ids) return _ids.ToArray(); }
    }

    /// <summary>重置到某页（作为栈底）。</summary>
    public void Reset(string id)
    {
        lock (_ids)
        {
            _ids.Clear();
            if (!string.IsNullOrEmpty(id)) _ids.Add(id);
        }
        Raise();
    }

    /// <summary>进入子页。</summary>
    public void Push(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_ids)
        {
            if (_ids.Count > 0 && _ids[_ids.Count - 1] == id) return;   // 重复推同一页忽略
            _ids.Add(id);
        }
        Raise();
    }

    /// <summary>返回上一级；返回新的当前页 id（无法返回时返回原值）。</summary>
    public string Pop()
    {
        lock (_ids)
        {
            if (_ids.Count > 1) _ids.RemoveAt(_ids.Count - 1);
        }
        Raise();
        return Current;
    }

    /// <summary>替换当前页（不改变深度）。</summary>
    public void Replace(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_ids)
        {
            if (_ids.Count == 0) _ids.Add(id);
            else _ids[_ids.Count - 1] = id;
        }
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
