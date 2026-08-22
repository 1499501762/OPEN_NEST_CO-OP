using System;
using LiteNetLib.Utils;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#if !MELONLOADER
using TMPro;
#else
using TMPro = Il2CppTMPro;
#endif

using OpenNestCoop.Core;
using OpenNestCoop.GameSync;
using OpenNestCoop.Net;

namespace OpenNestCoop.SyncV2;

/// <summary>
/// 任务打字机打印同步（TeleprinterSyncV2，MsgType=228）。M7：把 V1 <c>TeleprinterSync</c>（134）迁入分层架构。
/// <see cref="V2Authority.Host"/>：打字机事件/状态仅主机广播（客机任务图不跑，打印由主机任务状态机触发；
/// 客机回发会干扰主机）——客机只接收应用。EvState 状态同步（完整富文本 + 揭示数 + 纸张位置 + 打字针）
/// 打印中 0.1s 高频捕获逐字 reveal 序列（客机打字机动画跟随）。
/// 应用打印：TryCast IEnumerable 调 SubmitLines（协程逐字动画）+ TryStart；空闲时最终态兜底（reveal mask）。
/// </summary>
public sealed class TeleprinterSyncV2 : ISyncedModule
{
    public static TeleprinterSyncV2 Instance { get; } = new TeleprinterSyncV2();

    private TeleprinterSyncV2() { }

    private IHostStore Store => HostDataLayer.Instance;
    private NetManager _net => CoopRuntime.Net;

    public byte MsgType => (byte)OpenNestCoop.Net.MsgType.V2Teleprinter;

    private const byte EvPrint = 1;
    private const byte EvState = 2;
    private const byte EvClearAll = 3;
    private const byte EvClearAlarm = 4;
    private const byte EvAppend = 5;

    /// <summary>应用远端打字机事件时的防环标志。</summary>
    public static bool IsApplying;
    private static int _log;
    private static int _stateDiag;
    private static int _applyDiag;

    private readonly System.Collections.Generic.Dictionary<byte, string> _lastRich = new();
    private float _stateTimer;
    private const float StateInterval = 0.5f;
    private const float StateIntervalPrinting = 0.1f;
    private bool _anyPrinting;
    private float _printingCheckTimer;

    // ---------------- 本地事件（Harmony patch 调用，V2 分支；仅主机广播） ----------------

    public void OnLocalPrint(Teleprinter printer, System.Collections.Generic.List<string> lines)
    {
        try
        {
            if (IsApplying || printer == null || !Store.IsHost || !Store.IsOnline) return;
            if (lines == null || lines.Count == 0) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Teleprinter);
            w.Put(EvPrint);
            w.Put(ptype);
            w.Put((byte)Math.Min(lines.Count, 255));
            for (int i = 0; i < lines.Count && i < 255; i++)
                w.Put(lines[i] ?? "");
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] OnLocalPrint: {ex.Message}"); }
    }

    public void OnLocalAppend(Teleprinter printer, string chunkRich, bool prepend)
    {
        try
        {
            if (IsApplying || printer == null || !Store.IsHost || !Store.IsOnline) return;
            if (string.IsNullOrEmpty(chunkRich)) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Teleprinter);
            w.Put(EvAppend);
            w.Put(ptype);
            w.Put(prepend ? (byte)1 : (byte)0);
            w.Put(chunkRich);
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] OnLocalAppend: {ex.Message}"); }
    }

    public void OnLocalClearAll(Teleprinter printer)
    {
        try
        {
            if (IsApplying || printer == null || !Store.IsHost || !Store.IsOnline) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Teleprinter);
            w.Put(EvClearAll);
            w.Put(ptype);
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] OnLocalClearAll: {ex.Message}"); }
    }

    public void OnLocalClearAlarm(Teleprinter printer)
    {
        try
        {
            if (IsApplying || printer == null || !Store.IsHost || !Store.IsOnline) return;
            byte ptype = 0;
            try { ptype = (byte)(int)printer.TeleprinterType; } catch { }
            var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Teleprinter);
            w.Put(EvClearAlarm);
            w.Put(ptype);
            Broadcast(w);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] OnLocalClearAlarm: {ex.Message}"); }
    }

    private void Broadcast(NetDataWriter w)
    {
        var net = _net;
        if (net == null) return;
        var data = NetProtocol.Snapshot(w);
        if (Store.IsHost)
        {
            for (int i = 0; i < net.Roster.Count; i++)
            {
                var p = net.Roster[i];
                if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
            }
        }
        else if (net.HostSteamId != 0)
            net.Transport.Send(net.HostSteamId, data, true);
        if ((++_log % 5) == 1)
            CoopRuntime.LogSource?.LogInfo($"[TeleprinterV2] local ev={(w.Data.Length > 1 ? w.Data[1] : (byte)0)} isHost={Store.IsHost}");
    }

    // ---------------- EvState 状态同步（主机权威，打印中高频） ----------------

    public void Tick(float dt)
    {
        if (!Store.IsOnline) return;
        bool anyPrinting = _anyPrinting;
        if (!_anyPrinting || _printingCheckTimer <= 0f)
        {
            _printingCheckTimer = 0.25f;
            try
            {
                anyPrinting = false;
                var allP = UnityEngine.Object.FindObjectsOfType<Teleprinter>(true);
                if (allP != null)
                    foreach (var tp in allP)
                        if (tp != null) { try { if (tp.IsPrinting) { anyPrinting = true; break; } } catch { } }
                _anyPrinting = anyPrinting;
            }
            catch { }
        }
        else _printingCheckTimer -= dt;
        _stateTimer += dt;
        float interval = anyPrinting ? StateIntervalPrinting : StateInterval;
        if (_stateTimer < interval) return;
        _stateTimer = 0f;
        // 打字机状态仅主机广播（主机权威）
        if (!Store.IsHost || IsApplying) return;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Teleprinter>(true);
            if (all == null || all.Length == 0) return;
            foreach (var tp in all)
            {
                if (tp == null) continue;
                byte ptype = 0;
                try { ptype = (byte)(int)tp.TeleprinterType; } catch { continue; }
                string rich = "";
                try { rich = tp._currentFullRich ?? ""; } catch { }
                if (string.IsNullOrEmpty(rich))
                {
                    try
                    {
                        var p = typeof(Teleprinter).GetField("_tmp");
                        if (p != null)
                        {
                            var tmpObj = p.GetValue(tp);
                            if (tmpObj != null)
                            {
                                var textProp = tmpObj.GetType().GetProperty("text");
                                if (textProp != null)
                                {
                                    var tv = textProp.GetValue(tmpObj);
                                    rich = tv == null ? "" : tv.ToString();
                                }
                            }
                        }
                    }
                    catch { }
                }
                if (rich.Length == 0) continue;
                int revealed = 0;
                try { revealed = tp._currentRevealedCharIndex; } catch { }
                string sig2 = rich + "|" + revealed.ToString();
                if (_lastRich.TryGetValue(ptype, out var last) && last == sig2) continue;
                _lastRich[ptype] = sig2;
                if ((++_stateDiag % 20) == 1)
                {
                    try
                    {
                        string diag = $" isRunning={tp._isRunning} isPrinting={tp.IsPrinting} revealed={tp._currentRevealedCharIndex}";
                        CoopRuntime.LogSource?.LogInfo($"[TeleprinterV2] state ptype={ptype}{diag} rich='{Truncate(rich)}'");
                    }
                    catch { }
                }
                float px = 0f, py = 0f, pz = 0f;
                bool paperOk = false;
                try
                {
                    var pt = tp.paperTransform;
                    if (pt != null) { var lp = pt.localPosition; px = lp.x; py = lp.y; pz = lp.z; paperOk = true; }
                }
                catch { }
                bool animTyping = false;
                try { animTyping = tp._animTypingState; } catch { }
                var w = NetProtocol.Begin(OpenNestCoop.Net.MsgType.V2Teleprinter);
                w.Put(EvState);
                w.Put(ptype);
                w.Put(rich);
                w.Put(revealed);
                w.Put(paperOk ? (byte)1 : (byte)0);
                w.Put(px); w.Put(py); w.Put(pz);
                w.Put(animTyping ? (byte)1 : (byte)0);
                var data = NetProtocol.Snapshot(w);
                var net = _net;
                if (net != null)
                {
                    for (int i = 0; i < net.Roster.Count; i++)
                    {
                        var p = net.Roster[i];
                        if (p != null && !p.IsLocal) net.Transport.Send(p.SteamId, data, true);
                    }
                }
            }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] Tick: {ex.Message}"); }
    }

    public void OnPacket(ulong from, byte[] data)
    {
        try
        {
            var r = new NetDataReader(data);
            r.GetByte(); // 跳过消息类型
            byte ev = r.GetByte();
            byte ptype = r.GetByte();
            if (Store.IsHost)
            {
                // 主机转发给其他客机；不本地 Apply（打字机事件仅主机权威广播，客机回发只转发防干扰）
                var net = _net;
                if (net != null)
                    for (int i = 0; i < net.Roster.Count; i++)
                    {
                        var p = net.Roster[i];
                        if (p != null && !p.IsLocal && (ulong)p.SteamId != from)
                            net.Transport.Send(p.SteamId, data, true);
                    }
                return;
            }
            Apply(ev, ptype, r);
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] OnPacket: {ex.Message}"); }
    }

    private static void Apply(byte ev, byte ptype, NetDataReader r)
    {
        try
        {
            IsApplying = true;
            try
            {
                var tp = FindPrinter(ptype);
                switch (ev)
                {
                    case EvPrint:
                    {
                        int n = r.GetByte();
                        var lines = new System.Collections.Generic.List<string>(n);
                        for (int i = 0; i < n; i++) lines.Add(r.GetString());
                        if (tp == null) return;
                        // 去重 + 替换（2026-08-15）：客机本地任务图/seed 打印与主机内容重复且不一致
                        // → 双份打字机。复现前先清空打印机（替换残留本地内容），仅显示主机权威内容。
                        try
                        {
                            string joined = string.Join("\n", lines).Trim();
                            string curRich = (tp._currentFullRich ?? "").Trim();
                            if (curRich.Length > 0 && curRich == joined)
                            {
                                CoopRuntime.LogSource?.LogInfo($"[TeleprinterV2] skip print (already shown) ptype={ptype}");
                                break;
                            }
                            try { tp.ClearAll(); } catch { }
                        }
                        catch { }
                        try
                        {
                            var il2cppLines = new Il2CppSystem.Collections.Generic.List<string>();
                            foreach (var s in lines)
                            {
                                try
                                {
                                    Il2CppSystem.String ilstr = s ?? "";
                                    il2cppLines.Add(ilstr);
                                }
                                catch { }
                            }
                            var val = ((Il2CppObjectBase)il2cppLines)
                                .TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>();
                            if (val != null)
                            {
                                var job = tp.SubmitLines("", val, null, false);
                                if (job != null)
                                {
                                    try
                                    {
                                        var jl = job.lines;
                                        if (jl != null && jl.Count > 0 && (jl[0] ?? "").StartsWith("Il2CppSystem.", StringComparison.Ordinal))
                                        {
                                            jl.Clear();
                                            foreach (var s in lines)
                                            {
                                                Il2CppSystem.String ilstr = s ?? "";
                                                jl.Add(ilstr);
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            try { tp.TryStart(true); } catch { }
                            if ((++_applyDiag % 10) == 7)
                                CoopRuntime.LogSource?.LogInfo($"[TeleprinterV2] after print ptype={ptype} isPrinting={tp.IsPrinting} revealed={tp._currentRevealedCharIndex} isRunning={tp._isRunning}");
                        }
                        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterV2] apply print: {ex.Message}"); }
                        break;
                    }
                    case EvAppend:
                    {
                        bool prepend = r.GetByte() != 0;
                        string chunk = r.GetString();
                        if (tp == null) return;
                        try { tp.AppendInstant(chunk ?? "", prepend); } catch { }
                        break;
                    }
                    case EvState:
                    {
                        string rich = r.GetString();
                        int revealed = 0;
                        bool paperOk = false;
                        float px = 0f, py = 0f, pz = 0f;
                        bool animTyping = false;
                        try
                        {
                            if (r.AvailableBytes >= 4) revealed = r.GetInt();
                            if (r.AvailableBytes >= 1) paperOk = r.GetByte() != 0;
                            if (r.AvailableBytes >= 12) { px = r.GetFloat(); py = r.GetFloat(); pz = r.GetFloat(); }
                            if (r.AvailableBytes >= 1) animTyping = r.GetByte() != 0;
                        }
                        catch { }
                        if (tp == null) return;
                        bool printing = false;
                        try { printing = tp.IsPrinting; } catch { }
                        bool keepLocalAnimation = printing;
                        // 打印中也设 _currentFullRich = 主机正确内容（逐字揭示底本；不破坏动画）
                        try { tp._currentFullRich = rich; } catch { }
                        bool tmpSet = false;
                        var tmpObj = GetTmpText(tp);
                        if (tmpObj != null)
                        {
                            try
                            {
                                var textProp = tmpObj.GetType().GetProperty("text");
                                if (textProp != null) { textProp.SetValue(tmpObj, rich); tmpSet = true; }
                            }
                            catch { }
                        }
                        int targetRev = revealed;
                        if (!keepLocalAnimation)
                        {
                            int curRev = 0;
                            try { curRev = tp._currentRevealedCharIndex; } catch { }
                            targetRev = Math.Max(curRev, revealed);
                            try { tp._currentRevealedCharIndex = targetRev; } catch { }
                            try
                            {
                                var tmpObj2 = GetTmpText(tp);
                                if (tmpObj2 != null)
                                {
                                    try { tmpObj2.GetType().GetMethod("ForceMeshUpdate")?.Invoke(tmpObj2, null); } catch { }
                                    int charCount = 0;
                                    try
                                    {
                                        var ti = tmpObj2.GetType().GetProperty("textInfo")?.GetValue(tmpObj2);
                                        if (ti != null)
                                        {
                                            var cc = ti.GetType().GetProperty("characterCount");
                                            if (cc != null) charCount = (int)cc.GetValue(ti);
                                        }
                                    }
                                    catch { }
                                    if (charCount > 0)
                                    {
                                        var mask = new Il2CppSystem.Collections.Generic.List<bool>();
                                        for (int i = 0; i < charCount; i++) mask.Add(i < targetRev);
                                        try { tp._revealMask = mask; } catch { }
                                        try { tp.ApplyAlphaMaskToText(); } catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                        if (paperOk)
                        {
                            try
                            {
                                var pt = tp.paperTransform;
                                if (pt != null)
                                {
                                    var lp = pt.localPosition;
                                    lp.x = px; lp.y = py; lp.z = pz;
                                    pt.localPosition = lp;
                                }
                            }
                            catch { }
                        }
                        try
                        {
                            var ta = tp.typerAnimator;
                            if (ta != null && !string.IsNullOrEmpty(tp.typingBoolName))
                                try { ta.SetBool(tp.typingBoolName, animTyping); } catch { }
                        }
                        catch { }
                        try { tp._animTypingState = animTyping; } catch { }
                        if ((++_applyDiag % 10) == 1)
                            CoopRuntime.LogSource?.LogInfo($"[TeleprinterV2] applied state ptype={ptype} printing={printing} keepAnim={keepLocalAnimation} revealed={targetRev} animTyping={animTyping} rich='{Truncate(rich)}'");
                        break;
                    }
                    case EvClearAll:
                        if (tp != null) { try { tp.ClearAll(); } catch { } }
                        break;
                    case EvClearAlarm:
                        if (tp != null) { try { tp.ClearAlarm(); } catch { } }
                        break;
                }
            }
            finally { IsApplying = false; }
        }
        catch (Exception ex) { CoopRuntime.LogSource?.LogWarning($"[TeleprinterSyncV2] Apply: {ex.Message}"); }
    }

    private static object GetTmpText(Teleprinter tp)
    {
        try
        {
            var p = typeof(Teleprinter).GetField("_tmp",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
            if (p == null)
            {
                foreach (var fi in typeof(Teleprinter).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance))
                {
                    if (fi.FieldType.Name.Contains("TMP_Text")) { p = fi; break; }
                }
            }
            if (p != null)
            {
                var tmpObj = p.GetValue(tp);
                if (tmpObj != null) return tmpObj;
            }
            var texts = tp.GetComponentsInChildren<TMPro.TMP_Text>(true);
            if (texts != null && texts.Length > 0)
            {
                foreach (var t in texts)
                    if (t != null && t.text != null && t.text.Length > 0) return t;
                return texts[0];
            }
        }
        catch { }
        return null;
    }

    private static Teleprinter FindPrinter(byte ptype)
    {
        try
        {
            var type = (Teleprinter.Teleprinters)ptype;
            return Teleprinter.GetTeleprinter(type);
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max = 240)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    public void OnSessionStarted() { }
    public void OnSessionEnded() { Reset(); }
    public void Reset() { _lastRich.Clear(); }
}
