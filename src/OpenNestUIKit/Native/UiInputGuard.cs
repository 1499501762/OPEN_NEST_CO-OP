using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace OpenNestUIKit.Native;

/// <summary>
/// 输入守卫：菜单打开期间的"输入隔离"总闸。**做法照抄已双端实测的 OpenNestModMenu**
/// （`docs/MOD_MENU.md` §八），只按本库的结构重写：
///
/// ① **层级停靠**：本库画布固定 `32766`（**严格低于**游戏虚拟光标层 `32767`，且**从不修改光标画布**），
///    比我们高的非光标画布降到我们之下，关闭时原样还原；
/// ② **压制外部射线**：所有非本库画布的 `GraphicRaycaster.enabled = false`，关闭时原样恢复；
/// ③ **世界点击拦截**：Harmony prefix 挂 `LookAtTarget.OnClickDown`（本游戏所有交互按钮/拉杆的唯一点击入口），
///    菜单打开时返回 false（Harmony 用**反射**拿，双端通用）；
/// ④ **组件级交互锁**：禁用场景里所有交互组件（基类链含 `Interactable` 的派生类 + `LookAtTarget`）
///    + `FirstPersonController.SetFrozen(true)` —— 这一层与"谁触发、走哪条路"**无关**，是兜底的总闸。
///
/// ⚠️ 只压制**其它**画布，且仅在打开期间生效；关闭即全部还原（与同进程其它模组互不干扰）。
/// </summary>
public static class UiInputGuard
{
    private sealed class SavedRaycaster
    {
        public GraphicRaycaster Rc;
        public bool WasEnabled;
    }

    /// <summary>停用的游戏输入模块（主拦截；只动 1~2 个组件，关菜单原样还原）。</summary>
    private sealed class SavedModule
    {
        public UnityEngine.EventSystems.BaseInputModule M;
        public bool WasEnabled;
    }

    private sealed class SavedOrder
    {
        public Canvas Canvas;
        public int Order;
    }

    /// <summary>游戏虚拟光标画布所在层级（游戏自己写 32767；隐藏时写 -32768）。**我们绝不占用这个值。**</summary>
    private const int CursorOrder = 32767;

    /// <summary>本库画布层级（紧贴游戏光标层之下；官方联机 UI 用的也是这一档）。</summary>
    private const int OurOrder = CursorOrder - 1;

    private static readonly List<SavedRaycaster> _savedRc = new();
    private static readonly List<SavedModule> _savedModules = new();   // 停用的游戏输入模块（主拦截）
    private static readonly List<SavedRaycaster> _textRc = new();     // 输入聚焦时额外压住的射线器
    private static bool _raycasterFallback;
    private static readonly List<SavedOrder> _orderSaved = new();
    private static readonly List<MonoBehaviour> _locked = new();
    private static readonly List<MonoBehaviour> _pendingLock = new();   // 待禁用（增量处理，避免单帧尖峰）
    private static readonly HashSet<int> _lockedIds = new();            // 去重用（实例 ID；O(1)）
    private static readonly HashSet<int> _pendingIds = new();

    /// <summary>每帧最多禁用多少个交互组件（增量锁；165 个 ÷ 24 ≈ 7 帧完成≈0.12s，与开菜单动画同量级）。</summary>
    private const int LockPerFrame = 24;

    private static Canvas _mineCanvas;
    private static int _mineOrder;
    private static bool _lockOn;
    /// <summary>游戏 EventSystem（记录用；不存在时也是 null）。</summary>
    private static UnityEngine.EventSystems.EventSystem _savedEs;
    /// <summary>EventSystem 原本是否激活（输入聚焦时临时开，失焦要还回去）。</summary>
    private static bool _esWasActive;
    private static bool _esActivatedByUs;
    /// <summary>是否处于“输入聚焦”态（临时放行了输入模块）。</summary>
    private static bool _textCapture;
    private static bool _scanZeroLogged;
    private static bool _scanZeroLoggedForScene;   // 本场景是否已经试过退化遍历
    private static float _nextScan;
    private static bool _clickBlockOk;
    private static float _nextClickBlockTry;
    private static int _blockedClicks;

    // ---- 性能：重活只在“开菜单 / 换场景”时做一次，其余时候 Apply 只做几个 bool 判断 ----
    private static int _sceneKey = int.MinValue;   // 场景指纹（sceneCount + activeScene.handle）
    private static bool _suppressDone;             // 射线压制是否已做过（同场景不重复做）
    private static bool _lockScanDone;             // 交互锁是否已扫过（同场景不重复扫）
    private static bool _lockScanDue;              // 该做一次收集了（由 TickLock 在下一帧执行）

    /// <summary>诊断：上一次 Apply 的实际耗时（ms；“跳过”时≈0）。</summary>
    public static float LastApplyMs { get; private set; }
    /// <summary>诊断：上一次交互组件搜索的耗时（ms）。</summary>
    public static float LastLockMs { get; private set; }
    /// <summary>诊断：上一次交互组件搜索“看过多少东西”（快速路径只看已知类型；退化路径才会遍历场景）。</summary>
    public static int LastScanned { get; private set; }
    /// <summary>诊断：上一次“原生 FindObjectsOfType”本身的耗时（ms）。</summary>
    public static float LastSearchMs { get; private set; }
    /// <summary>诊断：上一次“遍历结果 + 入队”的耗时（ms）。</summary>
    public static float LastIterMs { get; private set; }
    /// <summary>诊断：是否走了“遍历全场景”的退化路径。</summary>
    public static bool LastScanWasFull { get; private set; }

    private static Behaviour _fpc;
    private static MethodInfo _setFrozen;
    private static bool _fpcLogged, _fpcMissingLogged;

    /// <summary>
    /// 菜单打开时调用（可重复调用：只在新场景/还没处理过时才做重活）。
    /// ⚠ 早期版本这里每次都：全场景 `FindObjectsOfType<GraphicRaycaster>` + 遍历全场景 MonoBehaviour（实测 1.3 万个，
    /// 7~几十 ms + 大量 GC），并且打开期间每 5s 重扫一次 —— 用户反馈“防止指针穿透的拦截机制很耗性能”就是这个。
    /// 现在：**换场景才重做**（场景指纹变化），其余调用只过几个 bool。
    /// </summary>
    public static void Apply(Canvas mine)
    {
        float t0 = 0f;
        try { t0 = Time.realtimeSinceStartup; } catch { }
        _mineCanvas = mine;
        try
        {
            int key = SceneKey();
            if (key != _sceneKey)
            {
                _sceneKey = key;
                _suppressDone = false;
                _lockScanDone = false;
                _lockScanDue = false;
                _scanZeroLoggedForScene = false;
            }
            DockLayers(mine);                 // 便宜：只动少量画布 sortingOrder
            if (!_suppressDone) { SuppressRaycasters(mine); _suppressDone = _savedRc.Count > 0; }
            InstallGameClickBlock();          // 已装就直接返回
            LockInteractions(true);           // 同场景且已扫过 → 只 FreezePlayer
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "guard apply failed: " + ex.Message); }
        try { LastApplyMs = (Time.realtimeSinceStartup - t0) * 1000f; } catch { }
    }

    /// <summary>场景指纹（廉价、不分配）：sceneCount 与 activeScene.handle。</summary>
    private static int SceneKey()
    {
        try
        {
            int n = UnityEngine.SceneManagement.SceneManager.sceneCount;
            var a = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            int h = a.IsValid() ? a.handle : 0;
            return unchecked(n * 397 ^ h);
        }
        catch { return int.MinValue; }
    }

    /// <summary>诊断：每次打开菜单记一条环境快照。</summary>
    public static void LogProbe()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var esAll = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>(true);
            sb.Append("input probe: eventSystems=").Append(esAll.Length);
            for (int i = 0; i < esAll.Length && i < 6; i++)
            {
                var es = esAll[i];
                if (es == null) continue;
                string mod = "<none>";
                try { if (es.currentInputModule != null) mod = es.currentInputModule.GetType().Name; } catch { }
                sb.Append("\n  ").Append(es.gameObject.name).Append(" enabled=").Append(es.enabled).Append(" module=").Append(mod);
            }
            string curName = "<none>";
            try { var cur = UnityEngine.EventSystems.EventSystem.current; if (cur != null) curName = cur.gameObject.name; } catch { }
            sb.Append("\n  current=").Append(curName)
              .Append(" clickBlock=").Append(_clickBlockOk)
              .Append(" interactionLock=").Append(_lockOn)
              .Append(" lockedComponents=").Append(_locked.Count)
              .Append(" disabledModules=").Append(_savedModules.Count)
              .Append(" suppressedRaycasters=").Append(_savedRc.Count)
              .Append(" raycasterFallback=").Append(_raycasterFallback)
              .Append(" ours=").Append(_mineOrder);
            CoopLog.Info("uikit.ui", () => sb.ToString());
        }
        catch { }
    }

    // ---------------- 层级停靠 ----------------

    /// <summary>
    /// 摆平画布层级：本库画布 = <see cref="OurOrder"/>；其它**非光标**画布里层级 ≥ 我们的降到我们之下
    /// （保持它们彼此的相对顺序），关菜单还原。**从不修改光标画布**（游戏每帧写回自己的值）。
    /// </summary>
    public static void DockLayers(Canvas mine)
    {
        if (mine == null) return;
        _mineCanvas = mine;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>(true);

            var higher = new List<Canvas>();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || c == mine) continue;
                if (LooksLikeCursor(c.name)) continue;      // 光标画布：一律不动
                if (c.sortingOrder < OurOrder) continue;    // 本来就在我们之下
                if (IsOrderSaved(c)) continue;
                higher.Add(c);
            }

            higher.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
            int demoted = 0;
            for (int i = 0; i < higher.Count; i++)
            {
                var c = higher[i];
                int want = Mathf.Max(OurOrder - 1 - i, 30000);
                _orderSaved.Add(new SavedOrder { Canvas = c, Order = c.sortingOrder });
                if (c.sortingOrder != want) { c.sortingOrder = want; demoted++; }
            }

            int before = mine.sortingOrder;
            if (mine.sortingOrder != OurOrder) mine.sortingOrder = OurOrder;
            _mineOrder = OurOrder;

            if (demoted > 0 || before != OurOrder)
            {
                int b = before, d = demoted, h = higher.Count, k = CountCursorCanvases(all);
                CoopLog.Info("uikit.ui", () => $"layer dock: ours {b}->{OurOrder}（低于游戏光标层 {CursorOrder}），demoted={d}/{h}，活跃光标画布={k}（未动）");
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "layer dock failed: " + ex.Message);
        }
    }

    /// <summary>每帧后段重申层级（LateUpdate；游戏会在自己的 Update 里写回光标画布，但**不写我们的**）。</summary>
    public static void TickLayerOrder()
    {
        try
        {
            var mine = _mineCanvas;
            if (mine != null && mine.sortingOrder != OurOrder) mine.sortingOrder = OurOrder;
            for (int i = 0; i < _orderSaved.Count; i++)
            {
                var c = _orderSaved[i]?.Canvas;
                if (c == null) continue;
                if (c.sortingOrder >= OurOrder) c.sortingOrder = OurOrder - 1;
            }
        }
        catch { }
    }

    /// <summary>是否必须**自绘**指针（只有"既没有活跃光标画布、硬件光标又隐藏"时才需要，否则会变双指针）。</summary>
    public static bool NeedsOwnPointer()
    {
        try
        {
            if (Cursor.visible) return false;
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            return CountCursorCanvases(all) == 0;
        }
        catch { return false; }
    }

    private static int CountCursorCanvases(Canvas[] all)
    {
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c == null || !LooksLikeCursor(c.name)) continue;
            if (!c.isActiveAndEnabled) continue;
            n++;
        }
        return n;
    }

    private static bool LooksLikeCursor(string name)
        => !string.IsNullOrEmpty(name) && name.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsOrderSaved(Canvas c)
    {
        for (int i = 0; i < _orderSaved.Count; i++) if (ReferenceEquals(_orderSaved[i].Canvas, c)) return true;
        return false;
    }

    // ---------------- 射线压制 ----------------

    /// <summary>
    /// **关掉游戏自己的输入模块**（`EventSystem` 上的 `BaseInputModule`，本游戏是虚拟光标模块）——
    /// 这是"菜单打开期间游戏 UI 不该再收点击"的**语义正确**做法：只动 1~2 个组件，关菜单原样恢复。
    ///
    /// 为什么换掉旧的"压制所有外部 `GraphicRaycaster`"（2026-09-13 重构）：
    /// 旧做法要 `FindObjectsOfType<GraphicRaycaster>` 找出 36~59 个射线器逐个 `enabled=false`，
    /// 而且游戏**每帧会写回**自己的状态 → 只能周期性重做（帧尖峰 + 大量 GC）；但从语义上说，
    /// 我们想拦的不是"某个射线器"，而是"游戏的输入处理"。拦模块既短又准。
    ///
    /// 兜底：拿不到 `EventSystem`/模块时，回退到旧射线压制（行为不倒退）。
    /// </summary>
    public static void SuppressRaycasters(Canvas mine)
    {
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null)
            {
                // `current` 只在激活实例上才有值 → 连未激活的也找一遍（打字模式会临时激活它）
                try
                {
                    var esAll = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>(true);
                    if (esAll != null && esAll.Length > 0)
                    {
                        for (int i = 0; i < esAll.Length; i++) if (esAll[i] != null) { es = esAll[i]; break; }
                    }
                }
                catch { }
            }
            if (es != null) { _savedEs = es; try { _esWasActive = es.gameObject.activeSelf; } catch { } }
            var mods = es != null ? es.GetComponents<UnityEngine.EventSystems.BaseInputModule>() : null;
            int n = 0;
            if (mods != null)
            {
                for (int i = 0; i < mods.Length; i++)
                {
                    var m = mods[i];
                    if (m == null) continue;
                    if (IsSavedModule(m)) continue;
                    _savedModules.Add(new SavedModule { M = m, WasEnabled = m.enabled });
                    if (m.enabled) { m.enabled = false; n++; }
                }
            }
            if (n > 0)
            {
                _raycasterFallback = false;
                CoopLog.Debug("uikit.ui", () => $"game input module(s) disabled: {n}（替代旧的射线器压制）");
                return;
            }

            // 兜底：没有可停用的模块 → 还是去压射线器（保持旧行为，别丢功能）
            _raycasterFallback = true;
            var all = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>(true);
            int rc = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                if (mine != null && IsUnder(r.transform, mine.transform)) continue;
                if (IsSavedRc(r)) continue;
                _savedRc.Add(new SavedRaycaster { Rc = r, WasEnabled = r.enabled });
                if (r.enabled) { r.enabled = false; rc++; }
            }
            if (rc > 0) CoopLog.Debug("uikit.ui", () => $"suppressed {rc} external GraphicRaycaster(s)（兜底路径）");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "suppress game input failed: " + ex.Message);
        }
    }

    private static bool IsSavedModule(UnityEngine.EventSystems.BaseInputModule m)
    {
        for (int i = 0; i < _savedModules.Count; i++) if (ReferenceEquals(_savedModules[i].M, m)) return true;
        return false;
    }

    // ---------------- （已移除）打字模式 ----------------
    // 2026-09-13：早期为了“让 TMP_InputField 收到键盘”而临时放行游戏输入模块 / 临时激活 EventSystem。
    // 现已改为**不用 TMP_InputField 收键**（照搬 Coop 模组的输入管线：物理键 + 隐藏 IME 锚点 + 原生 Win32 IME，
    // 见 Widgets/UiTextRouter.cs）→ 这套临时放行不再需要，也少了一个“点击可能穿透”的风险点。

    /// <summary>诊断：游戏输入模块当前状态（实机取证用）。</summary>
    public static string ProbeTextCapture()
    {
        int on = 0, off = 0, dead = 0;
        try
        {
            for (int i = 0; i < _savedModules.Count; i++)
            {
                var s = _savedModules[i];
                if (s?.M == null) { dead++; continue; }
                if (s.M.enabled) on++; else off++;
            }
        }
        catch { }
        bool hasKb = false, hasMs = false;
        try { hasKb = UnityEngine.InputSystem.Keyboard.current != null; } catch { }
        try { hasMs = UnityEngine.InputSystem.Mouse.current != null; } catch { }
        return $"拦截层：游戏输入模块 启用={on} 停用={off} 已毁={dead}"
             + $"｜新输入系统 键盘={hasKb} 鼠标={hasMs}"
             + $"｜聚焦临时压住射线器={_textRc.Count} 打字中={_textCapture}"
             + $"｜世界点击前缀={(_clickBlockOk ? "已装" : "未装")} 已拦={_blockedClicks}"
             + $"｜原生页压制={_nativePageSuppress} 交互锁={_lockOn}";
    }

    /// <summary>
    /// **输入聚焦期间的临时放行**（由 <c>UiTextRouter.SetFocused</c> 调用）。
    ///
    /// 为什么需要：我们防指针穿透的做法是**停用游戏的 `BaseInputModule`**，而
    /// `TMP_InputField` 要靠它才能真的“拿到焦点”（`isFocused`）；输入法（IME）组合只会送到
    /// **已聚焦的输入框**。所以打字期间要：
    ///   ① 若游戏 `EventSystem` 未激活 → **临时激活**（实测主菜单里 `EventSystem.current == null`）；
    ///   ② 把那 1~2 个原本 enabled 的输入模块**临时启用**；
    ///   ③ 同时把外部射线器压住（键盘/IME 放行 ≠ 鼠标点击放行）；
    /// 失焦立即原样还原（模块重新停用、EventSystem 还原激活状态、射线器还原）。
    ///
    /// 注：即便这一步全部失败，英文/数字仍靠**物理键轮询**、中文仍靠**原生 Win32 IME 读取**
    /// （`UiTextRouter`），所以它是“锦上添花”而不是唯一通道。
    /// </summary>
    public static void SetTextCapture(bool on)
    {
        if (_textCapture == on) return;
        _textCapture = on;
        try
        {
            if (on)
            {
                try
                {
                    if (_savedEs != null && !_savedEs.gameObject.activeSelf)
                    {
                        _savedEs.gameObject.SetActive(true);
                        _esActivatedByUs = true;
                        CoopLog.Debug("uikit.ui", () => "输入聚焦：临时激活游戏 EventSystem（IME 锚点需要它才能被聚焦）");
                    }
                }
                catch { }
                for (int i = 0; i < _savedModules.Count; i++)
                {
                    var s = _savedModules[i];
                    if (s?.M == null) continue;
                    try { if (s.WasEnabled) s.M.enabled = true; } catch { }
                }
                SuppressExternalRaycasters(_mineCanvas);
            }
            else
            {
                for (int i = 0; i < _savedModules.Count; i++)
                {
                    var s = _savedModules[i];
                    if (s?.M == null) continue;
                    try { s.M.enabled = false; } catch { }
                }
                if (_esActivatedByUs)
                {
                    _esActivatedByUs = false;
                    try { if (_savedEs != null) _savedEs.gameObject.SetActive(_esWasActive); } catch { }
                }
                // ⚠ 2026-09-13（用户：“Chat 聚焦在失去聚焦之后就会导致菜单组件都没法接受点击”）：
                //   以前这里**只还原了输入模块与 EventSystem，漏了 `_textRc`（聚焦期额外压住的射线器）**，
                //   而 `_textRc` 只增不减 ⇒ 只要聚焦过一次输入框，游戏自己的射线器就**永久 disabled**，
                //   游戏 UI（含 ESC 菜单里的按钮 / 原生菜单）再也接不到点击。
                RestoreTextRaycasters();
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "SetTextCapture: " + ex.Message); }
    }

    /// <summary>压住“不是我们自己的”射线器（已记录的不重复压）。</summary>
    private static void SuppressExternalRaycasters(Canvas mine)
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                if (mine != null && IsUnder(r.transform, mine.transform)) continue;
                if (IsSavedRc(r) || IsInList(_textRc, r)) continue;
                _textRc.Add(new SavedRaycaster { Rc = r, WasEnabled = r.enabled });
                if (r.enabled) r.enabled = false;
            }
        }
        catch { }
    }

    /// <summary>
    /// 还原「输入聚焦」期间额外压住的射线器（<see cref="_textRc"/>）并清空记录。
    ///
    /// ⚠ 2026-09-13（用户：“Chat 聚焦在失去聚焦之后就会导致菜单组件都没法接受点击”）：
    ///   `_textRc` 以前**只增不减、从未还原** —— `SetTextCapture(false)` 与 `Restore()` 都漏了它，
    ///   `EnsureReleasedWhenIdle` 的“残留”判定也没看它。结果：聚焦过一次输入框后，游戏自己的射线器永久失效。
    /// </summary>
    private static void RestoreTextRaycasters()
    {
        if (_textRc.Count == 0) return;
        try
        {
            for (int i = 0; i < _textRc.Count; i++)
            {
                var s = _textRc[i];
                try { if (s.Rc != null) s.Rc.enabled = s.WasEnabled; } catch { }
            }
            CoopLog.Debug("uikit.ui", () => $"输入失焦：已还原聚焦期临时压住的射线器 {_textRc.Count} 个");
        }
        catch { }
        _textRc.Clear();
    }

    private static bool IsInList(List<SavedRaycaster> list, GraphicRaycaster r)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i].Rc, r)) return true;
        return false;
    }

    /// <summary>压住“不是我们自己的”射线器（带记录，便于原样恢复）。</summary>
    private static void SuppressExternalRaycasters(Canvas mine, List<SavedRaycaster> into)
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                if (mine != null && IsUnder(r.transform, mine.transform)) continue;
                if (IsSavedRc(r) || IsInList(into, r)) continue;
                into.Add(new SavedRaycaster { Rc = r, WasEnabled = r.enabled });
                if (r.enabled) r.enabled = false;
            }
        }
        catch { }
    }

    private static bool IsSavedRc(GraphicRaycaster rc)
    {
        for (int i = 0; i < _savedRc.Count; i++) if (ReferenceEquals(_savedRc[i].Rc, rc)) return true;
        return false;
    }

    private static bool IsUnder(Transform t, Transform root)
    {
        try
        {
            var p = t;
            for (int i = 0; i < 64 && p != null; i++)
            {
                if (ReferenceEquals(p, root)) return true;
                p = p.parent;
            }
        }
        catch { }
        return false;
    }

    /// <summary>还原输入拦截与层级（关菜单调用；先还原再隐藏画布，顺序无歧义）。
    /// ⚠ 原生模块页显示期间**拒绝还原**：任何关闭路径（窗口关、失焦、场景切换……）都不应把游戏的输入放回去，
    /// 否则同一帧内点我们那页的鼠标会“漏”给游戏（用户反复报的“鼠标还有穿透”）。</summary>
    public static void Restore()
    {
        if (_nativePageSuppress)
        {
            CoopLog.Debug("uikit.ui", () => "Restore 被忽略：原生页仍占用输入（避免穿透）");
            return;
        }
        // ① 还原游戏输入模块（主拦截）
        try
        {
            for (int i = 0; i < _savedModules.Count; i++)
            {
                var s = _savedModules[i];
                try { if (s.M != null) s.M.enabled = s.WasEnabled; } catch { }
            }
        }
        catch { }
        _savedModules.Clear();

        // ② 兜底路径：还原射线器
        try
        {
            for (int i = 0; i < _savedRc.Count; i++)
            {
                var s = _savedRc[i];
                try { if (s.Rc != null) s.Rc.enabled = s.WasEnabled; } catch { }
            }
        }
        catch { }
        _savedRc.Clear();

        // ②' 聚焦期额外压住的射线器也要放回去（否则游戏 UI 永久失去点击 —— 2026-09-13 修）
        RestoreTextRaycasters();

        try
        {
            for (int i = 0; i < _orderSaved.Count; i++)
            {
                var s = _orderSaved[i];
                try { if (s.Canvas != null) s.Canvas.sortingOrder = s.Order; } catch { }
            }
        }
        catch { }
        _orderSaved.Clear();

        LockInteractions(false);
        _mineCanvas = null;
    }

    // ---------------- 世界点击拦截（Harmony 反射） ----------------

    /// <summary>打字临时放行是否还开着（诊断）。</summary>
    public static bool TextCaptureEnabled => _textCapture;

    private static bool _nativePageSuppress;

    /// <summary>
    /// **每帧幂等保障**（由 `UiKitBehaviour.Update` 调）：只要原生页还开着，就必须拿着输入。
    /// 为何需要：游戏（或其它模组）会在自己的 `Update` 里**写回** `BaseInputModule.enabled = true`，
    /// 一帧的缺口就够构成一次“穿透”（旧注释里就记过“游戏每帧会写回自己的状态”）；
    /// 另外若有任何路径把压制放掉了（自愈），这里也会重新拿回来。
    /// </summary>
    public static void EnsureNativePageSuppress()
    {
        try
        {
            bool want = Native.NativeMenuPage.IsShown;
            if (!want)
            {
                if (_nativePageSuppress) SuppressForNativePage(false);   // 页面已关但压制还在 → 收尾（自愈）
                return;
            }
            if (!_nativePageSuppress) { SuppressForNativePage(true); return; }

            // 补刀：把游戏写回的模块重新停用（1~2 个组件，代价可忽略）
            int back = 0;
            for (int i = 0; i < _savedModules.Count; i++)
            {
                var s = _savedModules[i];
                if (s == null || s.M == null) continue;
                try { if (s.M.enabled) { s.M.enabled = false; back++; } } catch { }
            }
            // 兜底路径（拿不到模块 / 模块被销毁）时，射线器也可能被游戏写回 → 一并补刀
            for (int i = 0; i < _savedRc.Count; i++)
            {
                var s = _savedRc[i];
                if (s == null || s.Rc == null) continue;
                try { if (s.Rc.enabled) { s.Rc.enabled = false; back++; } } catch { }
            }
            if (back > 0)
            {
                _reSuppressTicks++;
                if (_reSuppressTicks == 1 || _reSuppressTicks % 120 == 0)
                    CoopLog.Info("uikit.ui", () => $"游戏把输入模块自己打开了（{back} 个）→ 重新停用（第 {_reSuppressTicks} 次）");
            }
        }
        catch { }
    }

    private static int _reSuppressTicks;

    /// <summary>
    /// **原生次级菜单页占用期间的输入阻挡**：用户报“点击现在是穿透了的，应该阻挡” ——
    /// 以前开原生页只开了自管指针，没压游戏输入；于是点我们那一页的同时游戏自己的按钮/世界交互也收到同一击。
    /// 现在开页时禁用游戏 `BaseInputModule`（同窗口那套），关页还原；且与窗口互不干扰。
    /// </summary>
    public static void SuppressForNativePage(bool on)
    {
        try
        {
            if (on)
            {
                if (_nativePageSuppress) return;
                _nativePageSuppress = true;
                SuppressRaycasters(_mineCanvas);
                // ⚠ 2026-09-13 用户：“鼠标还是有穿透” —— 真因：`InstallGameClickBlock()` 以前只在
                //   `Apply()`（= 我们自己的窗口打开时）里装。可原生页是**从游戏 ESC 菜单直接进**的，
                //   那条路上我们窗口可能从未开过 ⇒ 前缀根本没装 ⇒ 点我们那页的同时**世界交互**（炮塔/拉杆/控制台）
                //   照旧被点（实测日志里只有 Coop 模组的 patch，没有我们的）。现在开原生页就装。
                InstallGameClickBlock();
                LockInteractions(true);          // 兜底：组件级交互锁 + 冻结玩家（默认为关，见 InteractionLockEnabled）
                CoopLog.Info("uikit.ui", () => "原生页：已压制游戏输入（点击不再穿透）");
            }
            else
            {
                if (!_nativePageSuppress) return;
                _nativePageSuppress = false;
                LockInteractions(false);          // 解冻玩家 / 还原交互组件
                if (!Menu.UiMenuWindow.IsOpen) Restore();
                CoopLog.Info("uikit.ui", () => "原生页：已还原游戏输入");
            }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => "SuppressForNativePage: " + ex.Message); }
    }

    /// <summary>
    /// **每帧不变量检查**（极便宜）：如果我们**没有任何界面在占用输入**，那拦截层必须是干净的 ——
    /// 一旦发现残留（游戏输入模块还被停着 / 射线器还被压着 / 层级被我们改过 / 交互锁还开着），
    /// 立刻全部还原并记一条 Warn。
    ///
    /// 为什么需要（用户：“退到最外层再打开 ESC 菜单无法交互了”）：这种“菜单看着在、点不动”的典型成因就是
    /// 某条关闭路径漏了还原（早期就踩过好几回：窗口被销毁、剪贴板被游戏关掉、场景切换……）。
    /// 这里是兵底：只要我们没开着任何界面，残留就不可能活过一帧；同时日志会告诉我们到底漏了什么。
    /// </summary>
    public static void EnsureReleasedWhenIdle()
    {
        try
        {
            if (_textCapture) return;                                     // 打字中（临时放行）—— 由失焦路径还原
            if (Menu.UiMenuWindow.IsOpen) return;                         // 窗口正开着，本来就该拦截
            if (_nativePageSuppress) return;                              // 原生页正开着，是**有意**压着（不是残留）
            if (_savedModules.Count == 0 && _savedRc.Count == 0 && _orderSaved.Count == 0 && _textRc.Count == 0 && !_lockOn) return;

            CoopLog.Warn("uikit.ui", () => "发现拦截层残留（我们已无界面）→ 强制还原："
                + $"输入模块={_savedModules.Count} 射线器={_savedRc.Count} 聚焦射线器={_textRc.Count} 层级={_orderSaved.Count} 交互锁={_lockOn}");
            Restore();
        }
        catch { }
    }

    /// <summary>Hook 游戏交互点击入口（列表唯一入口 = `LookAtTarget.OnClickDown`）。失败会节流重试（类型可能晚加载）。</summary>
    public static void InstallGameClickBlock()
    {
        if (_clickBlockOk) return;
        try
        {
            if (_nextClickBlockTry > 0f && Time.unscaledTime < _nextClickBlockTry) return;
            _nextClickBlockTry = Time.unscaledTime + 5f;
        }
        catch { }

        try
        {
            var targetType = GameReflect.Find("LookAtTarget");
            if (targetType == null)
            {
                CoopLog.Warn("uikit.ui", () => "game click block pending: LookAtTarget not found (will retry)");
                return;
            }
            var target = targetType.GetMethod("OnClickDown", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null)
            {
                CoopLog.Warn("uikit.ui", () => "game click block skipped: LookAtTarget.OnClickDown not found");
                return;
            }
            if (!TryPatch(target, nameof(PrefixLookAtTargetClick), "LookAtTarget.OnClickDown"))
            {
                CoopLog.Warn("uikit.ui", () => "game click block skipped: harmony patch failed");
                return;
            }
            _clickBlockOk = true;
            CoopLog.Info("uikit.ui", () => $"game click block installed: {targetType.FullName}.OnClickDown prefix");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "game click block failed: " + ex.Message);
        }
    }

    /// <summary>Harmony prefix：菜单打开时不执行原方法（返回 false = 跳过）。</summary>
    public static bool PrefixLookAtTargetClick()
    {
        try
        {
            if (!Menu.UiMenuWindow.IsOpen && !NativeMenuPage.IsShown) return true;
            int n = ++_blockedClicks;
            if (n <= 3 || n % 50 == 0)
                CoopLog.Info("uikit.ui", () => $"game click blocked (LookAtTarget.OnClickDown) #{n}");
            return false;
        }
        catch { return true; }
    }

    /// <summary>用 Harmony（反射拿类型，不引用 HarmonyLib）给某方法挂 prefix。</summary>
    private static bool TryPatch(MethodBase target, string prefixMethodName, string label)
    {
        try
        {
            var harmonyType = GameReflect.Find("HarmonyLib.Harmony");
            var harmonyMethodType = GameReflect.Find("HarmonyLib.HarmonyMethod");
            if (harmonyType == null || harmonyMethodType == null) return false;

            var prefixMi = typeof(UiInputGuard).GetMethod(prefixMethodName, BindingFlags.Public | BindingFlags.Static);
            if (prefixMi == null) return false;

            var prefix = Activator.CreateInstance(harmonyMethodType, new object[] { prefixMi });
            var harmony = Activator.CreateInstance(harmonyType, new object[] { UiKitInfo.Guid });

            // ⚠️ 不能按精确参数类型找 Patch：Harmony 2.x 的 Patch 重载数目随版本变（新版多一个 ilmanipulator）。
            // 这里按"名字 = Patch + 第 1 参是 MethodBase + 其余都是 HarmonyMethod"筛选，多余参数补 null。
            MethodInfo patch = null;
            var methods = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != "Patch") continue;
                var ps = m.GetParameters();
                if (ps.Length < 2) continue;
                if (!typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType)) continue;
                bool ok = true;
                for (int j = 1; j < ps.Length; j++)
                    if (ps[j].ParameterType != harmonyMethodType) { ok = false; break; }
                if (!ok) continue;
                patch = m;
                break;
            }
            if (patch == null) return false;

            var args = new object[patch.GetParameters().Length];
            args[0] = target;
            args[1] = prefix;
            patch.Invoke(harmony, args);
            CoopLog.Debug("uikit.ui", () => $"harmony patched: {label}");
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => $"harmony patch '{label}' failed: {ex.Message}");
            return false;
        }
    }

    // ---------------- 组件级交互锁 ----------------

    /// <summary>菜单打开期间的"交互锁"：禁用场景里所有交互组件 + 冻结第一人称控制器；关闭时原样恢复。</summary>
    public static void LockInteractions(bool on)
    {
        try
        {
            _lockOn = on;
            if (on)
            {
                // 交互锁**默认不启用**（点击已由 Harmony 前缀拦住；这一层会把 100+ 个游戏组件的 enabled 改掉）
                if (InteractionLockEnabled)
                {
                    if (!_lockScanDone) _lockScanDue = true;
                }
                FreezePlayer(true);
            }
            else
            {
                RestoreInteractables();
                FreezePlayer(false);
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "interaction lock failed: " + ex.Message);
        }
    }

    private static void DisableInteractables(bool fastOnly)
    {
        int n = 0, scanned = 0;
        var census = new Dictionary<string, int>(StringComparer.Ordinal);
        float t0 = 0f;
        try { t0 = Time.realtimeSinceStartup; } catch { }

        // ① **快速路径**：按已知交互类型做原生级搜索（`LookAtTarget` 是本游戏所有交互的唯一入口基名，
        //    `Interactable` 是它的基类族）。不遍历场景 → 1 次原生调用，几乎不产生 GC。
        bool fast = false, resolved = false;
        n += LockByTypeName("LookAtTarget", census, ref scanned, ref fast, ref resolved);
        // `Interactable` 是基类族，搜索面大得多（实测多花 ~2ms）；只在 `LookAtTarget` 解析不到时兵底。
        if (!resolved) n += LockByTypeName("Interactable", census, ref scanned, ref fast, ref resolved);

        // ② **退化路径**：只有**连类型都找不到**（说明游戏换了交互入口类名）才做全场景遍历，
        //    且每个场景最多一次。以前“快查没命中就遍历”在这里是白花 21ms（主菜单本来就没有交互物）。
        bool full = false;
        if (!fastOnly && !resolved && n == 0 && !_scanZeroLoggedForScene)
        {
            _scanZeroLoggedForScene = true;
            full = true;
            foreach (var mb in SceneBehaviours())
            {
                if (mb == null) continue;
                scanned++;
                try
                {
                    if (!IsInteractionComponent(mb.GetType())) continue;
                    if (!mb.enabled) continue;
                    mb.enabled = false;
                }
                catch { continue; }
                _locked.Add(mb);
                n++;
                string key = TypeName(mb.GetType());
                census.TryGetValue(key, out int c);
                census[key] = c + 1;
            }
        }

        LastScanned = scanned;
        LastScanWasFull = full;
        try { LastLockMs = (Time.realtimeSinceStartup - t0) * 1000f; } catch { }

        if (n == 0)
        {
            if (!_scanZeroLogged)
            {
                _scanZeroLogged = true;
                CoopLog.Info("uikit.ui", () => $"interaction lock: nothing to lock（快查可用={resolved}，本场景遍历={scanned} 个）");
            }
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var kv in census)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(kv.Key).Append('=').Append(kv.Value);
        }
        string detail = sb.ToString();
        CoopLog.Info("uikit.ui", () => $"interaction lock: 待锁 {n} 个 [{detail}]（收集耗时={LastLockMs:F2}ms，全场景遍历={full}，分帧禁用 {LockPerFrame}/帧）");
    }

    /// <summary>
    /// 增量执行禁用：每帧最多 <see cref="LockPerFrame"/> 个（<see cref="Core.UiKitBehaviour"/> 每帧调用）。
    /// 为什么这么做：开菜单时一口气禁用 167 个组件实测 6.5ms 单帧尖峰；分帧后无可见卡顿，
    /// 而“点击穿透”本身已由 Harmony 前缀拦住（不依赖这个锁），所以延迟几帧完全安全。
    /// </summary>
    public static void TickLock()
    {
        if (!InteractionLockEnabled) return;      // 默认关：点击已由 LookAtTarget 前缀拦住
        if (!InteractionLockEnabled) return;
        // ① 首次（或换场景后）做一次收集（入队）；放到这一帧而不是开菜单那一帧
        if (_lockScanDue)
        {
            _lockScanDue = false;
            _lockScanDone = true;
            try { DisableInteractables(fastOnly: false); } catch { }
        }
        // ② 还没锁到任何东西（例：开菜单时交互物还没生成）→ 每 2 秒**便宜重试**；
        //    一旦锁到东西就停（⛔ 以前每 5 秒重扫全场景 1.3 万个 MonoBehaviour = 性能和 GC 黑洞）
        else if (_lockOn && _locked.Count == 0 && _pendingLock.Count == 0)
        {
            float now = 0f;
            try { now = Time.unscaledTime; } catch { }
            if (now >= _nextScan)
            {
                _nextScan = now + 2f;
                try { DisableInteractables(fastOnly: true); } catch { }
            }
        }

        // ③ 增量禁用（每帧最多 LockPerFrame 个）
        if (_pendingLock.Count == 0) return;
        int n = 0;
        for (int i = _pendingLock.Count - 1; i >= 0 && n < LockPerFrame; i--)
        {
            var mb = _pendingLock[i];
            _pendingLock.RemoveAt(i);
            if (mb == null) continue;
            int id = 0;
            try { id = mb.GetInstanceID(); _pendingIds.Remove(id); } catch { }
            try
            {
                if (!mb.enabled) continue;
                mb.enabled = false;
                _locked.Add(mb);
                if (id != 0) _lockedIds.Add(id);
                n++;
            }
            catch { }
        }
    }

    /// <summary>把“待锁”队列入队（去重：已锁/已在队里的不再入；用实例 ID 哈希，O(1)）。返回 true = 新入队。</summary>
    private static bool EnqueueLock(MonoBehaviour mb)
    {
        if (mb == null) return false;
        int id;
        try { id = mb.GetInstanceID(); } catch { return false; }
        if (_lockedIds.Contains(id) || _pendingIds.Contains(id)) return false;
        _pendingIds.Add(id);
        _pendingLock.Add(mb);
        return true;
    }

    /// <summary>
    /// 按类型名做一次**原生**级 `FindObjectsOfType(type, true)` 并锁定（禁用）它们。
    /// 返回锁了几个；<paramref name="fast"/> 标记快速路径是否真的可用过。
    /// 说明：IL2CPP 下 `FindObjectsOfType` 的参数是 `Il2CppSystem.Type` → 反射挑重载 + `Il2CppType.From` 转换（双端通用）。
    /// </summary>
    private static int LockByTypeName(string typeName, Dictionary<string, int> census, ref int scanned, ref bool fast, ref bool resolved)
    {
        int n = 0;
        try
        {
            var t = GameReflect.Find(typeName);
            if (t == null) return 0;
            // ⚠ 只查**激活**对象：未激活的组件本来就收不到点击，查它们要走全量扫描
            //（实测 includeInactive=true 时主菜单场景单次搜索 ~6.6ms，false 后降到亚毫秒）。
            float s0 = 0f;
            try { s0 = Time.realtimeSinceStartup; } catch { }
            var list = FindAllOfType(t, includeInactive: false);
            try { LastSearchMs = (Time.realtimeSinceStartup - s0) * 1000f; } catch { }
            if (list == null) return 0;
            float i0 = 0f;
            try { i0 = Time.realtimeSinceStartup; } catch { }
            fast = true;
            resolved = true;      // 类型找到了 = 快速路径可用（即使一个实例都没有）
            foreach (var o in list)
            {
                var mb = o as MonoBehaviour;
                if (mb == null) continue;
                scanned++;
                if (!EnqueueLock(mb)) continue;       // 已在锁/队列里 → 跳过（不再计数）
                n++;
                census.TryGetValue(typeName, out int c);
                census[typeName] = c + 1;
            }
            try { LastIterMs = (Time.realtimeSinceStartup - i0) * 1000f; } catch { }
        }
        catch (Exception ex) { CoopLog.Warn("uikit.ui", () => $"lock '{typeName}' failed: {ex.Message}"); }
        return n;
    }

    private static bool IsLockedAlready(MonoBehaviour mb)
    {
        try { return mb != null && _lockedIds.Contains(mb.GetInstanceID()); } catch { return false; }
    }

    private static bool IsPendingLock(MonoBehaviour mb)
    {
        try { return mb != null && _pendingIds.Contains(mb.GetInstanceID()); } catch { return false; }
    }

    /// <summary>
    /// 反射调 `Object.FindObjectsOfType(System.Type/Il2CppSystem.Type, bool)`（含 inactive），
    /// 用 `IEnumerable` 迭代结果（避开 Il2CppReferenceArray ↔ T[] 的转换坑）。
    /// </summary>
    private static System.Collections.IEnumerable FindAllOfType(Type t, bool includeInactive)
    {
        try
        {
            var methods = typeof(UnityEngine.Object).GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                if (m.Name != "FindObjectsOfType" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length != 2 || ps[1].ParameterType != typeof(bool)) continue;

                object arg;
                var pt = ps[0].ParameterType;
                if (pt == typeof(Type)) arg = t;
                else if (pt.Name == "Type") { arg = Il2CppTypeFrom(t); if (arg == null) continue; }
                else continue;

                var r = m.Invoke(null, new object[] { arg, includeInactive });
                if (r is System.Collections.IEnumerable en) return en;
            }
        }
        catch (Exception ex) { CoopLog.Debug("uikit.ui", () => "FindAllOfType: " + ex.Message); }
        return null;
    }

    /// <summary>`System.Type` → `Il2CppSystem.Type`（反射拿 `Il2CppInterop.Runtime.Il2CppType.From`，双端都有）。</summary>
    private static object Il2CppTypeFrom(Type t)
    {
        try
        {
            var it = GameReflect.Find("Il2CppType");
            if (it == null) return null;
            var ms = it.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < ms.Length; i++)
            {
                var m = ms[i];
                if (m.Name != "From") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(Type)) continue;
                return m.Invoke(null, new object[] { t });
            }
        }
        catch (Exception ex) { CoopLog.Debug("uikit.ui", () => "Il2CppTypeFrom: " + ex.Message); }
        return null;
    }

    /// <summary>
    /// 交互锁开关（**默认关**）：它把场景里 100+ 个 `LookAtTarget.enabled` 置 false，
    /// 属于“改游戏状态”的重手段，而且**点击已由 `LookAtTarget.OnClickDown` 前缀拦住**，
    /// 所以默认不必要。想恢复旧行为（连悬停高亮一起锁）可用 `lock:on` 或把它置 true。
    /// </summary>
    /// <summary>是否启用了交互锁。</summary>
    public static bool InteractionLockEnabled { get; set; }

    /// <summary>诊断：当前停用的游戏输入模块数 / 压制的外部射线器数 / 是否走了兜底路径。</summary>
    public static int DisabledModuleCount => _savedModules.Count;
    public static int SuppressedRaycasterCount => _savedRc.Count;
    public static bool RaycasterFallback => _raycasterFallback;

    /// <summary>还原交互锁（把之前禁用的组件 enabled 置回 true）。</summary>
    private static void RestoreInteractables()
    {
        int n = 0;
        for (int i = 0; i < _locked.Count; i++)
        {
            try { if (_locked[i] != null) { _locked[i].enabled = true; n++; } } catch { }
        }
        _locked.Clear();
        _lockedIds.Clear();
        _pendingLock.Clear();
        _pendingIds.Clear();
        if (n > 0) CoopLog.Debug("uikit.ui", () => $"interaction lock: restored {n} component(s)");
    }

    private static void FreezePlayer(bool frozen)
    {
        try
        {
            if (!frozen)
            {
                if (_fpc == null) return;
                InvokeFrozen(false);
                CoopLog.Debug("uikit.ui", () => "player unfrozen (SetFrozen(false))");
                return;
            }
            if (_fpc == null)
            {
                foreach (var mb in SceneBehaviours())
                {
                    if (mb == null) continue;
                    if (!string.Equals(TypeName(mb.GetType()), "FirstPersonController", StringComparison.Ordinal)) continue;
                    _fpc = mb;
                    break;
                }
            }
            if (_fpc == null)
            {
                if (!_fpcMissingLogged) { _fpcMissingLogged = true; CoopLog.Warn("uikit.ui", () => "player freeze skipped: FirstPersonController not found in scene"); }
                return;
            }
            InvokeFrozen(true);
            if (!_fpcLogged) { _fpcLogged = true; CoopLog.Info("uikit.ui", () => "player frozen (FirstPersonController.SetFrozen(true))"); }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("uikit.ui", () => "player freeze failed: " + ex.Message);
        }
    }

    private static void InvokeFrozen(bool frozen)
    {
        try
        {
            if (_setFrozen == null)
                _setFrozen = _fpc.GetType().GetMethod("SetFrozen",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            _setFrozen?.Invoke(_fpc, new object[] { frozen });
        }
        catch { }
    }

    /// <summary>是否"交互组件"：基类链里含 `Interactable`（覆盖派生类），或类型名就是 `LookAtTarget`。</summary>
    private static bool IsInteractionComponent(Type t)
    {
        for (int i = 0; i < 12 && t != null; i++)
        {
            string n = TypeName(t);
            if (string.Equals(n, "Interactable", StringComparison.Ordinal)) return true;
            if (string.Equals(n, "LookAtTarget", StringComparison.Ordinal)) return true;
            try { t = t.BaseType; } catch { return false; }
        }
        return false;
    }

    private static string TypeName(Type t)
    {
        try
        {
            string n = t?.Name ?? "";
            int tick = n.IndexOf('`');
            return tick > 0 ? n.Substring(0, tick) : n;
        }
        catch { return ""; }
    }

    /// <summary>遍历全部已加载场景根对象 → 其下所有 MonoBehaviour（含 inactive）。</summary>
    private static IEnumerable<MonoBehaviour> SceneBehaviours()
    {
        int count = 0;
        try { count = UnityEngine.SceneManagement.SceneManager.sceneCount; } catch { }
        for (int s = 0; s < count; s++)
        {
            UnityEngine.SceneManagement.Scene scene;
            try { scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s); } catch { continue; }
            GameObject[] roots = null;
            try { if (scene.IsValid()) roots = scene.GetRootGameObjects(); } catch { }
            if (roots == null) continue;
            for (int i = 0; i < roots.Length; i++)
            {
                var r = roots[i];
                if (r == null) continue;
                MonoBehaviour[] list = null;
                try { list = r.GetComponentsInChildren<MonoBehaviour>(true); } catch { }
                if (list == null) continue;
                for (int j = 0; j < list.Length; j++)
                    if (list[j] != null) yield return list[j];
            }
        }
    }
}
