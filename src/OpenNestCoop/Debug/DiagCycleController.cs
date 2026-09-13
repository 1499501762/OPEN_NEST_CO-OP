using UnityEngine;

using OpenNestCoop.Core;

namespace OpenNestCoop.Debug;

/// <summary>
/// 诊断菜单循环切换器（F9）：统一管理诊断 UI 的显示——按 **F9** 在
/// **不显示 → 帧性能(FrameDiagUI) → 网络(NetworkGovernorDebugUI) → 任务节点(MissionNodeDiagUI)
/// → 交互工具(InteractableNameTool) → 不显示** 之间循环。替代原独立按键，减少按键占用。
/// 各 UI 不再自监听按键，由本控制器通过 <c>SetVisible</c> 全权控制（同一时刻只显示一个）。
/// </summary>
public class DiagCycleController : MonoBehaviour
{
    /// <summary>诊断菜单模式（F9 循环序）。</summary>
    public enum DiagMode { None, Frame, Network, Mission, Interactable }

    private DiagMode _mode = DiagMode.None;

    public DiagCycleController(System.IntPtr ptr) : base(ptr) { }

    public void Update()
    {
        try
        {
            // 开机参数：--framediag = 启动就直接开“帧性能”档（排查掉帧时不必手动按 F9；
            // 也可写进 Steam 启动选项，玩家侧零操作就能采到 frame.log）。
            if (!_argsChecked)
            {
                _argsChecked = true;
                try
                {
                    var args = System.Environment.GetCommandLineArgs();
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (string.Equals(args[i], "--framediag", System.StringComparison.OrdinalIgnoreCase))
                        {
                            _mode = DiagMode.Frame;
                            Apply();
                            CoopRuntime.LogSource?.LogInfo("[DiagCycle] --framediag → 帧性能档（每 5s 写 OpenNestLogs/frame.log）");
                            break;
                        }
                    }
                }
                catch { }
            }

            // F9 循环切换（新 Input System——旧 UnityEngine.Input 在 IL2CPP 下被禁用）
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f9Key.wasPressedThisFrame)
                Cycle();
        }
        catch { }
    }

    private bool _argsChecked;

    private void Cycle()
    {
        _mode = (DiagMode)(((int)_mode + 1) % 5);
        Apply();
        CoopRuntime.LogSource?.LogInfo($"[DiagCycle] F9 → {_mode}");
    }

    /// <summary>应用当前模式到各诊断 UI（同一时刻只显示一个）。</summary>
    private void Apply()
    {
        bool frame = _mode == DiagMode.Frame;
        bool net = _mode == DiagMode.Network;
        bool mission = _mode == DiagMode.Mission;
        bool ia = _mode == DiagMode.Interactable;
        if (FrameDiagUI.Instance != null) FrameDiagUI.Instance.SetVisible(frame);
        if (NetworkGovernorDebugUI.Instance != null) NetworkGovernorDebugUI.Instance.SetVisible(net);
        if (MissionGraphViewUI.Instance != null) MissionGraphViewUI.Instance.SetVisible(mission);
        if (InteractableNameTool.Instance != null) InteractableNameTool.Instance.SetVisible(ia);
    }
}
