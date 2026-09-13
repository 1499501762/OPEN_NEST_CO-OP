using UnityEngine;

namespace OpenNestCoop.Core;

/// <summary>
/// 由 BepInEx 挂载到游戏场景的 MonoBehaviour，负责驱动联机逻辑的 Update 循环。
/// UI 由 CoopUIManager（UGUI）单独驱动。
/// </summary>
public class CoopBehaviour : MonoBehaviour
{
    private bool _updatedOnce;
    private OpenNestCoop.Net.SessionState _lastState;

    public CoopBehaviour(System.IntPtr ptr) : base(ptr) { }

    public void Update()
    {
        if (!_updatedOnce)
        {
            _updatedOnce = true;
            CoopRuntime.LogSource?.LogInfo("[CoopBehaviour] Update called");
            try { Application.runInBackground = true; } catch { } // 失焦不暂停（虚拟机/Alt-Tab）
        }
        try
        {
            var net = CoopRuntime.Net;
            // 用 unscaledDeltaTime：游戏切菜单/UI 暂停（timeScale=0）时同步不中断
            net?.Update(Time.unscaledDeltaTime);

            // 独立文件日志批量落盘（1s 间隔缓冲写，性能好；诊断/联机日志 → OpenNestLogs/*.log）
            OpenNestCore.Logging.ModLog.Flush(Time.unscaledDeltaTime);

            // 配置文件热重载（标准 cfg，2s 轮询 mtime；改配置不用重启游戏。见 docs/CONFIG.md）
            try { CoopConfig.Tick(Time.unscaledDeltaTime); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CoopBehaviour] CoopConfig tick: {ex.Message}"); }

            // 语言键文件热重载（OpenNestCoop.lang.ini，2s 轮询 mtime；改文案不用重启。见 docs/LOCALIZATION.md）
            try { OpenNestCoop.Core.Loc.LocFile.Tick(Time.unscaledDeltaTime); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CoopBehaviour] LocFile tick: {ex.Message}"); }

            // OpenNestUIKit 集成：环境里有它 → 菜单交给它渲染、原生 ESC 入口也由它注入（纯探测，接上后零开销）
            try { OpenNestCoop.UI.UIKitIntegration.Tick(); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CoopBehaviour] UIKit tick: {ex.Message}"); }

            // 自定义任务桥接驱动（OpenNestCore.Tasks → 游戏宿主）：单机驱动自定义任务状态机
            try { OpenNestCoop.GameSync.OncMissionBridge.Update(Time.unscaledDeltaTime); }
            catch (System.Exception ex) { CoopRuntime.LogSource?.LogWarning($"[CoopBehaviour] OncMission Update: {ex.Message}"); }

            // 检测会话状态变化：进入联机时移除“失焦暂停”残留
            if (net != null && net.State != _lastState)
            {
                if (net.State == OpenNestCoop.Net.SessionState.Hosting || net.State == OpenNestCoop.Net.SessionState.Joined)
                {
                    UnpauseForCoop();
                    // 自动加入成功：恢复"加入时暂停"
                    OpenNestCoop.Net.AutoJoin.ResumeIfPaused();
                }
                else if (net.State == OpenNestCoop.Net.SessionState.Idle)
                {
                    // 加入失败放弃（回到 Idle）：恢复"加入时暂停"，避免游戏卡在暂停
                    OpenNestCoop.Net.AutoJoin.ResumeIfPaused();
                }
                _lastState = net.State;
            }
        }
        catch (System.Exception ex)
        {
            CoopRuntime.LogSource?.LogError($"[CoopBehaviour] Update exception: {ex}");
        }
    }

    /// <summary>进入联机会话时：恢复时间流速 + 关闭 PauseManager 的失焦暂停（PauseOnFocusLoss 为静态）。</summary>
    private static void UnpauseForCoop()
    {
        try { Time.timeScale = 1f; } catch { }
        try { PauseManager.PauseOnFocusLoss = false; } catch { }
            CoopRuntime.LogSource?.LogInfo("[CoopBehaviour] removed focus-loss pause (PauseOnFocusLoss=false, timeScale=1)");
    }
}
