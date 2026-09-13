using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace OpenNestModMenu.UI;

/// <summary>
/// 输入守卫：菜单打开期间**压制其它画布的射线接收**，解决"点击穿透到背景原生 UI"（T4 实测反馈）。
///
/// 为什么不是靠"全屏 blocker 挡一层"就够：
/// - blocker 只能压住 **sortingOrder 比我们低** 的画布；本进程里 OpenNestCoop 的画布是 32766（高于我们），
///   游戏自身也可能有高序画布 → 点在我们的面板上时，那些画布照样收到点击。
/// - 因此改为**按对象压制**：把所有非本模组画布的 `GraphicRaycaster.enabled = false`，
///   射线只可能命中我们自己的画布（顺序无关，天然覆盖高序画布）。关菜单时**原样恢复**。
///
/// 注意：这里只压 UGUI 射线。若将来发现游戏还有"直接读鼠标的世界交互"，
/// 需要在 T5/T11 补 Harmony 级输入吞掉（见 docs/MOD_MENU.md §八 待办）。
/// </summary>
public static class UiInputGuard
{
    private sealed class Saved
    {
        public GraphicRaycaster Rc;
        public bool WasEnabled;
    }

    private sealed class SavedOrder
    {
        public Canvas Canvas;
        public int Order;
    }

    private static readonly List<Saved> _saved = new();
    private static readonly List<SavedOrder> _orderSaved = new();
    /// <summary>游戏虚拟光标画布所在层级（本机实测：游戏自己写 32767；隐藏时写 -32768）。**我们绝不占用这个值。**</summary>
    private const int CursorOrder = 32767;
    /// <summary>
    /// 本模组画布层级 = 32766：**严格低于**游戏虚拟光标层。
    /// 抄联机模组做法 —— `OpenNestCoop.UI.CoopUIManager.BuildCanvas` 与官方联机 `MultiplayerMenu`
    /// 都把自己的画布固定成 32766，并且**从不修改**游戏光标画布的层级。
    /// </summary>
    private const int OurOrder = CursorOrder - 1;
    private static Canvas _mineCanvas;
    private static int _mineOrder;
    private static bool _canvasLogged;
    private static bool _clickBlockOk;
    private static bool _swallowInstalled;

    /// <summary>菜单打开时调用（可重复调用：新出现的画布/交互物会被增量压制）。</summary>
    public static void Apply(Canvas mine)
    {
        DockLayers(mine);
        SuppressRaycasters(mine);
        InstallGameClickBlock();
        InstallInputSwallow();
        LockInteractions(true);
    }

    /// <summary>诊断：每次打开菜单记一条环境快照（EventSystem / 输入模块 / Harmony 是否装上）。</summary>
    public static void LogProbe()
    {
        LogInputProbe();
    }

    /// <summary>
    /// 摆平画布层级（菜单打开时）。**做法直接抄联机模组**（权威做法，不是猜的）：
    /// `OpenNestCoop.UI.CoopUIManager.BuildCanvas()` → `sortingOrder = 32766`；
    /// 官方联机 `MultiplayerMenu` → `sortingOrder = 32766`；两者都**从不碰游戏光标画布**。
    ///
    /// 于是规则只有两条：
    /// ① 本模组画布 = <see cref="OurOrder"/>（32766，**严格低于**光标层 32767）。
    ///    游戏想显示虚拟光标时它就在 32767 → 必定盖在我们之上，指针永远看得见；
    ///    游戏改用硬件光标时会把光标画布压到 -32768（不渲染指针）→ 系统硬件光标永远在最上层。
    ///    两种状态都不用我们操心。
    ///    ⚠️ **绝不能再像旧实现那样把光标画布 +1 去“保证它在上面”**：游戏每帧写回自己的值
    ///    （-32768 / 32767），我们改的值要么被写回、要么把它推到 32768，实测结果反而是
    ///    “指针掉到菜单下面”（用户多轮实测反馈的根因）。
    /// ② 其它**非光标**画布里层级 ≥ 我们的（实测 `OpenNestCoop_UI` = 32766，与旧实现的 32765 冲突）：
    ///    **降**到我们之下（保持它们彼此的相对顺序），否则右栏详情会被同级画布盖住（同级先后不可控）。
    /// 所有被降级的画布原值记录在案，关菜单时原样还原。
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
                if (LooksLikeCursor(c.name)) continue;      // 光标画布：一律不动（交给游戏自己管）
                if (c.sortingOrder < OurOrder) continue;    // 本来就在我们之下，不用管
                if (IsOrderSaved(c)) continue;
                higher.Add(c);
            }

            higher.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));   // 原层级降序 → 依次 32765、32764…
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
                int b = before, d = demoted, h = higher.Count, k = CountCursorCanvases(all, true);
                CoopLog.Info("modmenu.ui", () => $"layer dock: ours {b}->{OurOrder} (below game cursor layer {CursorOrder}), demoted={d}/{h}, active-cursor-canvases={k} (untouched)");
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "layer dock failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 每帧重申层级（便宜：几次 int 写入；在 **LateUpdate** 调，晚于游戏的 Update）。
    /// 只保证“我们在 32766 + 被降级的画布仍在我们之下”，**不碰光标画布**。
    /// </summary>
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

    /// <summary>
    /// 是否必须**自绘**指针。只有一种情形：游戏既没有活跃的虚拟光标画布，硬件光标又是隐藏的
    /// —— 否则游戏中总有一个指针层级在我们之上（虚拟光标 32767 / 硬件光标由系统画），自绘只会变成“双指针”。
    /// </summary>
    public static bool NeedsOwnPointer()
    {
        try
        {
            if (Cursor.visible) return false;    // 硬件光标：系统永远画在最上层
            var all = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            return CountCursorCanvases(all, true) == 0;
        }
        catch { return false; }
    }

    private static int CountCursorCanvases(Canvas[] all, bool activeOnly)
    {
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var c = all[i];
            if (c == null || !LooksLikeCursor(c.name)) continue;
            if (activeOnly && !c.isActiveAndEnabled) continue;
            n++;
        }
        return n;
    }

    private static bool LooksLikeCursor(string name)
        => !string.IsNullOrEmpty(name) && name.IndexOf("cursor", StringComparison.OrdinalIgnoreCase) >= 0;

    // ---------------- 游戏交互点击拦截（世界内按钮/拉杆） ----------------

    /// <summary>
    /// 抑制游戏世界的交互点击（实测反馈：压制 UGUI 射线后，点菜单仍会点到背景原生部件）。
    ///
    /// 依据（来自本项目的 OpenNestCoop `ButtonClickSync` 结论）：本游戏**所有**交互按钮/拉杆的点击
    /// 唯一入口是 <c>LookAtTarget.OnClickDown</c>（拉杆动画 + onClickDown UnityEvent + 状态推进都在它内部）。
    /// 用 Harmony 给它挂一个 prefix：菜单打开时返回 false → 跳过原方法 → 点击不再透到世界。
    ///
    /// Harmony 用**反射**拿（BepInEx 的 `0Harmony` / MelonLoader 自带的 `HarmonyLib` 都在进程里），
    /// 共享代码不直接引用 `HarmonyLib`，避免双端引用差异（与“全反射”的其他共享代码一致）。
    /// </summary>
    public static void InstallGameClickBlock()
    {
        if (_clickBlockOk) return;
        // 失败要能重试（类型可能晚加载；MLL 端曾因解析不到类型整层静默失效）→ 5s 节流重试
        try
        {
            if (_nextClickBlockTry > 0f && Time.unscaledTime < _nextClickBlockTry) return;
            _nextClickBlockTry = Time.unscaledTime + 5f;
        }
        catch { }

        try
        {
            var targetType = LoaderReflect.FindType("LookAtTarget");
            if (targetType == null)
            {
                CoopLog.Warn("modmenu.ui", () => "game click block pending: LookAtTarget not found (will retry)");
                return;
            }

            var target = targetType.GetMethod("OnClickDown", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (target == null)
            {
                CoopLog.Warn("modmenu.ui", () => "game click block skipped: LookAtTarget.OnClickDown not found");
                return;
            }

            if (!TryPatch(target, nameof(PrefixLookAtTargetClick), "LookAtTarget.OnClickDown"))
            {
                CoopLog.Warn("modmenu.ui", () => "game click block skipped: harmony patch failed");
                return;
            }

            _clickBlockOk = true;
            var tn = targetType.FullName;
            CoopLog.Info("modmenu.ui", () => $"game click block installed: {tn}.OnClickDown 前缀（菜单打开时跳过原方法）");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "game click block failed: " + ex.Message);
        }
    }

    private static float _nextClickBlockTry;

    /// <summary>
    /// **输入吞掉（设备层）**：菜单打开时，让 Input System 的“本帧按下/抬起”属性
    /// （`ButtonControl.wasPressedThisFrame` / `wasReleasedThisFrame`）一律返回 false。
    ///
    /// 为何还要这一层：
    /// - 压制 `GraphicRaycaster` 只拦“事件系统”这条路（UGUI 点击）；
    /// - Harmony 拦 `LookAtTarget.OnClickDown` 只拦“世界交互实体”那条；
    /// - 但游戏里还有大量**直接轮询设备**的代码（炮塔/开火/视角/快捷键）——它们读的就是这两个属性。
    ///   这里拦的是**设备层**，是最后一次总闸。
    ///
    /// 代价与对策：模组自己的 UI 不能再读 `wasPressedThisFrame`（会被自己拦掉）
    /// → 改读原始状态 `press` 并**自算边沿**（见 `ModMenuUI.HandlePointer` / 热键）。
    /// InputAction 回调不走这两个属性，故不受影响。
    /// </summary>
    public static void InstallInputSwallow()
    {
        if (_swallowInstalled) return;
        _swallowInstalled = true;
        try
        {
            var t = LoaderReflect.FindType("UnityEngine.InputSystem.Controls.ButtonControl");
            if (t == null)
            {
                CoopLog.Warn("modmenu.ui", () => "input swallow skipped: ButtonControl not found");
                return;
            }

            string[] props = { "wasPressedThisFrame", "wasReleasedThisFrame" };
            int ok = 0;
            for (int i = 0; i < props.Length; i++)
            {
                MethodInfo getter = null;
                try
                {
                    var pi = t.GetProperty(props[i], BindingFlags.Public | BindingFlags.Instance);
                    if (pi != null) getter = pi.GetGetMethod();
                }
                catch { }
                if (getter == null) continue;
                if (TryPatch(getter, nameof(PrefixButtonEdge), "ButtonControl." + props[i])) ok++;
            }

            CoopLog.Info("modmenu.ui", () => $"input swallow {(ok > 0 ? "installed" : "FAILED")}: patched {ok}/{props.Length} device edge getters");
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "input swallow failed: " + ex.Message);
        }
    }

    /// <summary>Harmony prefix（无参版）：菜单打开时不执行原方法（返回 false = 跳过）。</summary>
    public static bool PrefixLookAtTargetClick()
    {
        try
        {
            if (!ModMenuUI.IsOpen) return true;
            int n = ++_blockedClicks;
            // 只记前几次 + 每 50 次一条：这是"拦截确实生效"的**直接取证**（之前整层静默，无法判断是否拦到）。
            if (n <= 3 || n % 50 == 0)
                CoopLog.Info("modmenu.ui", () => $"game click blocked (LookAtTarget.OnClickDown) #{n}");
            return false;
        }
        catch { return true; }
    }

    private static int _blockedClicks;

    /// <summary>Harmony prefix：菜单打开时跳过原 getter（返回值 default = false）。</summary>
    public static bool PrefixButtonEdge()
    {
        try { return !ModMenuUI.IsOpen; }
        catch { return true; }
    }

    // ---------------- 交互锁（组件级；抄联机模组 OpenNestCoop.UI.CoopUIManager） ----------------

    /// <summary>
    /// 菜单打开期间的"交互锁"。**做法抄联机模组**（`CoopUIManager.ApplyMenuState()`：`_blocker` + `LockPlayer` +
    /// `DisableInteractables`）——他们在双端实测过"菜单打开时点不到背景东西"，是这套问题的既有解：
    /// ① 全屏 blocker：**本来就有**——`ModMenuUI.EnsureBuilt` 里的 `UiKit.MakeBlocker`（全屏压暗层，
    ///    `raycastTarget=true`、`SetAsFirstSibling`），与联机模组的 `_blocker` 等价，不需要再加一个；
    /// ② <see cref="FreezePlayer"/>：`FirstPersonController.SetFrozen(true)` —— 冻结第一人称控制器
    ///    （菜单打开时鼠标不该再转视角/走路，这也是"鼠标穿透"的一种表现）；
    /// ③ <see cref="DisableInteractables"/>：**禁用场景里所有交互组件**（`Interactable` 及其派生类 + `LookAtTarget`）。
    ///
    /// 为什么必须补这一层（前几层为什么不够）：
    /// - 压制 `GraphicRaycaster` 只拦 UGUI EventSystem 这条路；
    /// - Harmony 拦 `LookAtTarget.OnClickDown` 只拦"点了这个组件"的路，而联机模组已记录**部分点击根本不走 OnClickDown**
    ///   （"选药量/投放发射药：Button Dispencer / Charge Rammer 走 isClicked 轮询 + 方法调用"）；
    /// - 世界交互多半是**游戏自己**从光标位置做射线/轮询，与我们的画布层级、EventSystem 都无关。
    /// 组件级禁用与"谁触发、走哪条路"完全无关 —— 这才是路径无关的那一层。
    ///
    /// 组件用**类型名**识别（基类链），因此不依赖能否把游戏类型解析成 `Type`（实测 MLL 端解析不到 `LookAtTarget`）。
    /// 关闭菜单时按记录**原样恢复**。
    /// </summary>
    public static void LockInteractions(bool on)
    {
        try
        {
            _lockOn = on;
            if (on)
            {
                // 组件扫描**不做 1.5s 一次**（Apply 的节奏）：整场遍历 + GetComponentsInChildren 在主线程上有成本
                // （实测场景 13213 个 MonoBehaviour → 9ms）。打开时必扫（_locked 为空），之后 5s 一次
                // （捡菜单打开期间新出现/被游戏重新启用的交互物；实测任务场景的交互物比菜单晚出现，靠这一步补上）。
                bool scan;
                try { float now = Time.unscaledTime; scan = _locked.Count == 0 || now >= _nextScan; if (scan) _nextScan = now + 5f; }
                catch { scan = true; }
                if (scan) DisableInteractables();

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
            CoopLog.Warn("modmenu.ui", () => "interaction lock failed: " + ex.Message);
        }
    }

    /// <summary>禁用场景里所有交互组件（记录原始 enabled，关闭时逐个还原）。</summary>
    private static void DisableInteractables()
    {
        int n = 0;
        int scanned = 0;
        var census = new Dictionary<string, int>(StringComparer.Ordinal);
        float t0 = 0f;
        try { t0 = Time.realtimeSinceStartup; } catch { }
        foreach (var mb in SceneBehaviours())
        {
            if (mb == null) continue;
            scanned++;
            try
            {
                if (!IsInteractionComponent(mb.GetType())) continue;
                if (!mb.enabled) continue;          // 本来就禁用 / 已被我们禁用 → 不重复记录
                mb.enabled = false;
            }
            catch { continue; }

            _locked.Add(mb);
            n++;
            string key = TypeName(mb.GetType());
            census.TryGetValue(key, out int c);
            census[key] = c + 1;
        }

        if (n == 0)
        {
            // 场景里确实没有交互组件（例如刚开菜单时任务还没加载完）——记一次，便于区分"锁没生效"和"没东西可锁"
            if (!_scanZeroLogged)
            {
                _scanZeroLogged = true;
                int sc = scanned;
                CoopLog.Info("modmenu.ui", () => $"interaction lock: nothing to lock in current scene(s) (scanned {sc} MonoBehaviour(s))");
            }
            return;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var kv in census)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(kv.Key).Append('=').Append(kv.Value);
        }
        int total = _locked.Count;
        string detail = sb.ToString();
        float ms = 0f;
        try { ms = (Time.realtimeSinceStartup - t0) * 1000f; } catch { }
        CoopLog.Info("modmenu.ui", () => $"interaction lock: disabled {n} component(s) [{detail}] (total locked={total}, scan={ms:F1}ms)");
    }

    /// <summary>按记录恢复被禁用的交互组件。</summary>
    private static void RestoreInteractables()
    {
        int n = 0;
        for (int i = 0; i < _locked.Count; i++)
        {
            var b = _locked[i];
            try
            {
                if (b != null) { b.enabled = true; n++; }
            }
            catch { }
        }
        _locked.Clear();
        if (n > 0) CoopLog.Debug("modmenu.ui", () => $"interaction lock: restored {n} component(s)");
    }

    /// <summary>冻结/解冻第一人称控制器（`FirstPersonController.SetFrozen(bool)`，与联机模组同一入口）。</summary>
    private static void FreezePlayer(bool frozen)
    {
        try
        {
            if (!frozen)
            {
                if (_fpc == null) return;
                InvokeFrozen(false);
                CoopLog.Debug("modmenu.ui", () => "player unfrozen (SetFrozen(false))");
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
                if (!_fpcMissingLogged) { _fpcMissingLogged = true; CoopLog.Warn("modmenu.ui", () => "player freeze skipped: FirstPersonController not found in scene"); }
                return;
            }
            InvokeFrozen(true);
            if (!_fpcLogged) { _fpcLogged = true; CoopLog.Info("modmenu.ui", () => "player frozen (FirstPersonController.SetFrozen(true))"); }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "player freeze failed: " + ex.Message);
        }
    }

    private static void InvokeFrozen(bool frozen)
    {
        try
        {
            var mi = _setFrozen ??= _fpc.GetType().GetMethod("SetFrozen",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(bool) }, null);
            mi?.Invoke(_fpc, new object[] { frozen });
        }
        catch { }
    }

    /// <summary>是否"交互组件"：基类链里含 `Interactable`（覆盖 DialInteractable / LinearSliderInteractable 等派生类），或类型名就是 `LookAtTarget`。</summary>
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

    /// <summary>类型名（去掉泛型反引号后缀；Il2Cpp 代理类型名与游戏类型名一致）。</summary>
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

    /// <summary>遍历**全部已加载场景**的根对象 → 其下所有 MonoBehaviour（含 inactive）。</summary>
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

    private static readonly List<MonoBehaviour> _locked = new();
    private static bool _lockOn;
    private static bool _scanZeroLogged;
    private static float _nextScan;
    private static Behaviour _fpc;
    private static MethodInfo _setFrozen;
    private static bool _fpcLogged, _fpcMissingLogged;

    /// <summary>用 Harmony（反射）给某方法挂 prefix；返回是否成功。</summary>
    private static bool TryPatch(MethodBase target, string prefixMethodName, string label)
    {
        try
        {
            var harmonyType = LoaderReflect.FindType("HarmonyLib.Harmony");
            var harmonyMethodType = LoaderReflect.FindType("HarmonyLib.HarmonyMethod");
            if (harmonyType == null || harmonyMethodType == null) return false;

            var prefixMi = typeof(UiInputGuard).GetMethod(prefixMethodName, BindingFlags.Public | BindingFlags.Static);
            if (prefixMi == null) return false;

            var prefix = Activator.CreateInstance(harmonyMethodType, new object[] { prefixMi });
            var harmony = Activator.CreateInstance(harmonyType, new object[] { "open.nest.modmenu" });

            // ⚠️ 不能按精确参数类型找 Patch：Harmony 2.x 的 Patch 重载数目随版本变（新版多一个 ilmanipulator）。
            // 这里按“名字 = Patch + 第1参是 MethodBase + 其余都是 HarmonyMethod”筛选，再把多余参数补 null。
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
                {
                    if (ps[j].ParameterType != harmonyMethodType) { ok = false; break; }
                }
                if (!ok) continue;
                patch = m;
                break;
            }
            if (patch == null) return false;

            var args = new object[patch.GetParameters().Length];
            args[0] = target;
            args[1] = prefix;   // 其余（postfix/transpiler/…）= null
            patch.Invoke(harmony, args);
            CoopLog.Debug("modmenu.ui", () => $"harmony patched: {label}");
            return true;
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => $"harmony patch '{label}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>诊断：列出全部 EventSystem / 当前输入模块 / 本模组画布的射线器状态（每次打开菜单各记一条）。</summary>
    private static void LogInputProbe()
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
            sb.Append("\n  current=").Append(curName).Append(" clickBlock=").Append(_clickBlockOk)
              .Append(" interactionLock=").Append(_lockOn).Append(" lockedComponents=").Append(_locked.Count);

            // z-order 快照：我们 + 层数最高的前 8 个其它画布（取证“指针在 UI 下层”到底是谁在上面）
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            sb.Append("\n  z-order: ours=").Append(_mineOrder);
            var top = new List<Canvas>();
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null) continue;
                if (_mineCanvas != null && c == _mineCanvas) continue;
                top.Add(c);
            }
            top.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
            for (int i = 0; i < top.Count && i < 8; i++)
            {
                var c = top[i];
                sb.Append("\n    ").Append(c.sortingOrder).Append("  ").Append(c.name)
                  .Append(c.isActiveAndEnabled ? "" : " (inactive)")
                  .Append(LooksLikeCursor(c.name) ? " [cursor]" : "");
            }

            // 光标取证：硬件光标是否可见 + 每个光标画布的名字/层级/激活/精灵
            sb.Append("\n  cursor: hardwareVisible=").Append(Cursor.visible);
            int cn = 0;
            for (int i = 0; i < canvases.Length; i++)
            {
                var c = canvases[i];
                if (c == null || !LooksLikeCursor(c.name)) continue;
                sb.Append("\n    ").Append(c.name).Append(" order=").Append(c.sortingOrder)
                  .Append(c.isActiveAndEnabled ? "" : " (inactive)")
                  .Append(" imgs=").Append(SpriteInfo(c));
                cn++;
            }
            sb.Append("\n  cursorCanvases=").Append(cn).Append(" needOwnPointer=").Append(NeedsOwnPointer());
            CoopLog.Info("modmenu.ui", () => sb.ToString());
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "input probe failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 能力/状态快照（T10 调试面板用）：全部字段来自实测状态，不做推测；不抛（失败也返回文本）。
    /// </summary>
    internal static string CapabilitySummary()
    {
        try
        {
            int es = 0, esEnabled = 0;
            string mod = "<none>";
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>(true);
                es = all != null ? all.Length : 0;
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null) continue;
                        if (all[i].enabled) esEnabled++;
                        try { if (all[i].currentInputModule != null) mod = all[i].currentInputModule.GetType().Name; } catch { }
                    }
            }
            catch { }

            string tops = "<none>";
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
                var list = new List<Canvas>();
                for (int i = 0; i < canvases.Length; i++)
                    if (canvases[i] != null && (_mineCanvas == null || canvases[i] != _mineCanvas)) list.Add(canvases[i]);
                list.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
                var sb2 = new System.Text.StringBuilder();
                for (int i = 0; i < list.Count && i < 4; i++)
                {
                    if (i > 0) sb2.Append(" | ");
                    sb2.Append(list[i].sortingOrder).Append(' ').Append(list[i].name);
                }
                if (sb2.Length > 0) tops = sb2.ToString();
            }
            catch { }

            string needOwn;
            try { needOwn = NeedsOwnPointer() ? "yes" : "no"; } catch { needOwn = "?"; }

            return $"eventSystems={es} enabled={esEnabled} module={mod} clickBlock={_clickBlockOk} lock={_lockOn} locked={_locked.Count}"
                 + $"\n      zOurs={_mineOrder} top={tops} needOwnPointer={needOwn} hwCursor={Cursor.visible}";
        }
        catch (Exception ex) { return "probe failed: " + ex.Message; }
    }

    private static void SuppressRaycasters(Canvas mine)
    {
        try
        {
            if (!_canvasLogged)
            {
                _canvasLogged = true;
                LogCanvasInventory(mine);
            }

            Prune();

            var all = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>(true);
            int added = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var rc = all[i];
                if (rc == null) continue;
                if (mine != null && rc.gameObject == mine.gameObject) continue;   // 自己的画布不动
                if (IsSaved(rc)) continue;
                bool was = true;
                try { was = rc.enabled; rc.enabled = false; }
                catch { }
                _saved.Add(new Saved { Rc = rc, WasEnabled = was });
                added++;
            }

            if (added > 0)
            {
                int total = _saved.Count;
                CoopLog.Debug("modmenu.ui", () => $"input guard: suppressed {added} foreign GraphicRaycaster(s), total={total}");
            }
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "input guard apply failed: " + ex.Message);
        }
    }

    /// <summary>菜单关闭 / 销毁时调用：把压制的射线恢复原状。</summary>
    public static void Restore()
    {
        // 先解交互锁（组件恢复原状），再还原层级/射线
        try { LockInteractions(false); } catch { }

        int n = 0;
        for (int i = 0; i < _saved.Count; i++)
        {
            var s = _saved[i];
            try
            {
                if (s?.Rc != null) { s.Rc.enabled = s.WasEnabled; n++; }
            }
            catch { }
        }
        _saved.Clear();

        int m = 0;
        for (int i = 0; i < _orderSaved.Count; i++)
        {
            var s = _orderSaved[i];
            try
            {
                if (s?.Canvas != null) { s.Canvas.sortingOrder = s.Order; m++; }
            }
            catch { }
        }
        _orderSaved.Clear();

        if (n > 0 || m > 0)
        {
            int restored = n, orders = m;
            CoopLog.Debug("modmenu.ui", () => $"input guard: restored {restored} GraphicRaycaster(s), {orders} canvas order(s)");
        }
    }

    private static bool IsOrderSaved(Canvas c)
    {
        for (int i = 0; i < _orderSaved.Count; i++)
            if (_orderSaved[i]?.Canvas == c) return true;
        return false;
    }

    // ---------------- 诊断（一次性） ----------------

    private static void LogCanvasInventory(Canvas mine)
    {
        try
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
            var sb = new System.Text.StringBuilder();
            sb.Append("canvas inventory: count=").Append(canvases.Length);
            int shown = 0;
            for (int i = 0; i < canvases.Length && shown < 24; i++)
            {
                var c = canvases[i];
                if (c == null) continue;
                bool isMine = mine != null && c == mine;
                sb.Append('\n').Append("  ")
                  .Append(isMine ? "*ours* " : "       ")
                  .Append(ModMenuDisplay_Trunc(c.name, 28))
                  .Append(" order=").Append(c.sortingOrder)
                  .Append(" mode=").Append(c.renderMode.ToString());
                shown++;
            }
            CoopLog.Info("modmenu.ui", () => sb.ToString());
        }
        catch (Exception ex)
        {
            CoopLog.Warn("modmenu.ui", () => "canvas inventory failed: " + ex.Message);
        }
    }

    private static string ModMenuDisplay_Trunc(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    /// <summary>光标画布里的 Image 精灵名（取证用：拿不到精灵说明我们的自绘兜底也抄不到样式）。</summary>
    private static string SpriteInfo(Canvas c)
    {
        try
        {
            var imgs = c.GetComponentsInChildren<Image>(true);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < imgs.Length && i < 4; i++)
            {
                var im = imgs[i];
                if (im == null) continue;
                sb.Append(im.gameObject.activeInHierarchy ? "" : "*").Append(im.sprite != null ? im.sprite.name : "<no-sprite>").Append(' ');
            }
            return sb.Length > 0 ? sb.ToString() : "<none>";
        }
        catch { return "?"; }
    }

    private static bool IsSaved(GraphicRaycaster rc)
    {
        for (int i = 0; i < _saved.Count; i++)
            if (_saved[i]?.Rc == rc) return true;
        return false;
    }

    /// <summary>清掉已销毁的对象（场景切换后旧画布会被销毁）。</summary>
    private static void Prune()
    {
        for (int i = _saved.Count - 1; i >= 0; i--)
        {
            try { if (_saved[i]?.Rc == null) _saved.RemoveAt(i); }
            catch { _saved.RemoveAt(i); }
        }
    }
}
