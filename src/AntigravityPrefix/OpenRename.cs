using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;

namespace AntigravityPrefix;

internal sealed class OpenRenameBlockedException(string code, string detail) : InvalidOperationException(detail)
{
    internal string Code { get; } = code;
    internal WindowIdentity? RecoveryTarget { get; init; }
    internal string RequestId { get; init; } = "";
    internal static string Explain(string code)
    {
        string reason = code switch
        {
            "MAIN_DOCUMENT_UNPROVEN" => "未能确认唯一 Antigravity 主界面文档，请稍候或保存诊断。",
            "SIDEBAR_TRUNCATED" or "ROW_TRUNCATED" => "侧边栏识别未完成，不能安全确认目标。",
            "SELECTION_UNPROVEN" or "SELECTION_CHANGED" => "未能确认唯一选中会话，不按名称猜测。",
            "MENU_BUTTON_AMBIGUOUS" => "检测到多个会话操作菜单（如分屏视图），不能安全确认目标会话。",
            "MENU_BUTTON_UNPROVEN" or "RENAME_COMMAND_MISSING" or "RENAME_COMMAND_AMBIGUOUS" => "当前界面没有可确认的会话菜单操作，或未能打开 Rename 项。",
            "FOREGROUND_BLOCKED" or "WINDOW_CHANGED" => "目标窗口未取得前台或已切换。",
            "MODIFIERS_HELD" => "请先松开键盘按键，再点击编辑按钮。",
            "SHORTCUT_BLOCKED" => "Windows 未允许触发重命名操作。",
            "EDITOR_BLOCKED" => "上次提交尚未确认，请关闭原输入框后重开。",
            "EDITOR_NOT_CONFIRMED" or "EDITOR_STABILIZING_TIMEOUT" => "尚未确认重命名框，仍在检查晚到的输入框。",
            "MULTIPLE_EDITORS" => "存在多个输入范围，不能安全确认目标。",
            _ => "当前界面结构暂不兼容，自动打开已停止。"
        };
        return reason + "\n请手动点击会话右上角 ⋯ → Rename（或侧栏会话 ⋯ → Rename）；3 秒后可重试，右侧可展开。";
    }
}

public sealed partial class AntigravityBridge
{
    internal Action<string, int>? DemoOpenProbe { get; set; }
    internal int DemoOpenWaitMilliseconds { get; set; } = 8000;
    HashSet<string>? openDialogEvidence;
    bool lastOpenHadDialog;
    internal static bool IsTransientOpenError(Exception ex) => ex is ElementNotAvailableException || ex is COMException { HResult: unchecked((int)0x80040201) };
    Scope? FindOpenEditor(nint hwnd)
    {
        var evidence = new HashSet<string>();
        openDialogEvidence = evidence;
        try
        {
            // 与常规识别使用同样的焦点祖先校验；全局局部扫描不能替代已证实的原生焦点范围。
            var focused = FocusedScope(hwnd);
            var scopes = FindScopes(hwnd);
            if (focused is not null) scopes.Add(focused);
            scopes = scopes.GroupBy(scope => scope.Binding.Key).Select(group => group.First()).ToList();
            if (scopes.Count > 1 || evidence.Count > 1) throw new OpenRenameBlockedException("MULTIPLE_EDITORS", "存在多个重命名范围，拒绝猜测。");
            var editor = scopes.SingleOrDefault();
            if (editor is not null && blocked.ContainsKey(editor.Binding.Key)) throw new OpenRenameBlockedException("EDITOR_BLOCKED", "已有未确认事务，必须关闭原输入框后重开，不能经打开入口解除封锁。");
            return editor;
        }
        finally { lastOpenHadDialog = evidence.Count > 0; openDialogEvidence = null; }
    }
    internal ProbeResult? RecoverOpen(WindowIdentity target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!target.IsCurrent() || !Native.BelongsToWindow(Native.GetForegroundWindow(), target.Handle)) return null;
        try
        {
            var editor = FindOpenEditor(target.Handle);
            cancellation.ThrowIfCancellationRequested();
            if (editor is null || !target.IsCurrent() || !Native.BelongsToWindow(Native.GetForegroundWindow(), target.Handle)) return null;
            cached = editor;
            return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
        }
        catch (Exception ex) when (IsTransientOpenError(ex)) { cached = null; return null; }
    }
    bool HasRenameMenuContext(AutomationElement node, AutomationElement? stopDoc)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        string? stopId = stopDoc is null ? null : Id(stopDoc);
        bool inMenu = false;
        bool inPopupWindow = false;
        var anc = walker.GetParent(node);
        for (int depth = 0; anc is not null && depth < 12; depth++)
        {
            if (IsAntigravityExcludedZone(anc) || IsAntigravitySidebarNode(anc)) return false;
            var ct = anc.Properties.ControlType.ValueOrDefault;
            var role = AriaRole(anc);
            if (ct == ControlType.Menu || role == "menu" || Recognition.HasCssToken(Class(anc), "agy-overlay"))
                inMenu = true;
            if (stopId is not null && Id(anc) == stopId) return inMenu;
            if (ct == ControlType.Document) return inMenu;
            if (stopDoc is null && ct == ControlType.Window) inPopupWindow = true;
            anc = walker.GetParent(anc);
        }
        return stopDoc is null && (inMenu || inPopupWindow);
    }
    List<AutomationElement> FindRenameMenuItems(nint hwnd, AutomationElement mainDoc)
    {
        var found = new List<AutomationElement>();
        void ScanMenuRoot(AutomationElement root, AutomationElement? docBoundary, bool pruneSidebar)
        {
            var slice = Slice(root, docBoundary is null ? 80 : 200, docBoundary is null ? 200 : 400, 7,
                stopDocuments: true, pruneContent: true, stopAt: pruneSidebar ? IsAntigravitySidebarNode : null);
            foreach (var node in slice.Elements)
            {
                if (!Visible(node) || !node.Properties.IsEnabled.ValueOrDefault) continue;
                var ct = node.Properties.ControlType.ValueOrDefault;
                var role = AriaRole(node);
                if (ct != ControlType.MenuItem && role != "menuitem") continue;
                if (!Recognition.IsRenameMenuItem(Name(node))) continue;
                if (!node.Patterns.Invoke.IsSupported && !node.Patterns.LegacyIAccessible.IsSupported) continue;
                if (!HasRenameMenuContext(node, docBoundary)) continue;
                found.Add(node);
            }
        }
        ScanMenuRoot(mainDoc, mainDoc, true);
        foreach (var popupRoot in RelatedRoots(hwnd).Skip(1))
            ScanMenuRoot(popupRoot, null, false);
        return found.GroupBy(Id).Select(g => g.First()).ToList();
    }
    internal ProbeResult OpenRename(CancellationToken cancellation, Action<WorkStatus>? progress = null)
    {
        string stage = "核验目标窗口";
        string requestId = Guid.NewGuid().ToString("N")[..8];
        var elapsed = Stopwatch.StartNew();
        void Trace(string text) => log.Write("打开时序", $"请求={requestId};耗时={elapsed.ElapsedMilliseconds}ms;阶段={stage};{text}");
        Trace("开始；尚未触发打开动作。");
        try
        {
            nint hwnd = ResolveWindow();
            if (hwnd == 0 && !TrackingMode && demoWindow == 0)
            {
                var candidates = WindowTracker.Candidates(h => WindowIdentity.Read(h)).Where(h => !Native.IsIconic(h) && !TrackingNative.Cloaked(h)).ToList();
                if (candidates.Count == 1) hwnd = candidates[0];
            }
            if (hwnd == 0) throw new OpenRenameBlockedException("WINDOW_UNPROVEN", "没有唯一且经过核验的目标窗口；请先激活目标 Antigravity。");
            var identity = WindowIdentity.Read(hwnd, demoWindow == 0) ?? throw new OpenRenameBlockedException("IDENTITY_LOST", "目标进程身份读取失败。");
            cancellation.ThrowIfCancellationRequested();
            if (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd))
            {
                Native.SetForegroundWindow(hwnd);
                var activation = Stopwatch.StartNew();
                while (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd) && activation.ElapsedMilliseconds < 400)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!identity.IsCurrent()) break;
                    Thread.Sleep(20);
                }
            }
            void Validate()
            {
                cancellation.ThrowIfCancellationRequested();
                if (!identity.IsCurrent())
                    throw new OpenRenameBlockedException("WINDOW_CHANGED", "目标窗口句柄或进程身份已变化，未继续操作。");
                if (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd))
                    throw new OpenRenameBlockedException("WINDOW_CHANGED", "目标窗口身份仍有效，但前台归属已变化，未继续操作。");
            }
            if (!Native.BelongsToWindow(Native.GetForegroundWindow(), hwnd)) throw new OpenRenameBlockedException("FOREGROUND_BLOCKED", "Windows 未允许目标窗口取得前台，未继续操作。");
            lastWindow = hwnd;
            stage = "核验已有重命名框";
            Scope? ReadEditor(string phase, int attempt)
            {
                if (demoWindow != 0) DemoOpenProbe?.Invoke(phase, attempt);
                var editor = FindOpenEditor(hwnd);
                Validate();
                return editor;
            }
            Scope? existing;
            var preflight = Stopwatch.StartNew();
            int attempt = 0;
            while (true)
            {
                Validate();
                try
                {
                    existing = ReadEditor(demoWindow != 0 ? "发送前" : "触发前", ++attempt);
                    if (existing is not null || !lastOpenHadDialog) break;
                    if (preflight.ElapsedMilliseconds >= 2000) throw new OpenRenameBlockedException("EDITOR_STABILIZING_TIMEOUT", "已有重命名范围尚不完整或存在歧义，未继续打开。") { RecoveryTarget = identity, RequestId = requestId };
                    progress?.Invoke(new(StatusKind.Processing, "已有输入框尚未就绪，正在确认…"));
                    if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    cached = null;
                    Trace($"瞬态控件失效;尝试={attempt};HResult=0x{ex.HResult:X8};重新获取原生控件。");
                    if (preflight.ElapsedMilliseconds >= 2000) throw new OpenRenameBlockedException("EDITOR_STABILIZING_TIMEOUT", "触发前界面持续重建，未继续打开。") { RecoveryTarget = identity, RequestId = requestId };
                    progress?.Invoke(new(StatusKind.Processing, "原生界面正在更新，等待稳定…"));
                    if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
                }
            }
            if (existing is not null) { cached = existing; return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀")); }

            if (demoWindow != 0)
            {
                stage = "触发测试夹具默认重命名入口";
                Validate();
                if (!RenameKeys.DemoShortcutKeysReleased()) throw new OpenRenameBlockedException("MODIFIERS_HELD", "检测到修饰键或 R 键尚未松开，未注入按键。");
                Validate();
                if (!RenameKeys.SendDemoShortcut()) throw new OpenRenameBlockedException("SHORTCUT_BLOCKED", "SendInput 未完成整组按键，已尝试释放注入按键；未继续操作。");
                log.Write("打开重命名", "已向隔离演示夹具发送测试快捷键；尚未确认输入框，未写入名称。");
                Trace("演示触发已发送；本请求不再补发按键。");
                stage = "确认原生输入框已打开";
                var demoSettle = Stopwatch.StartNew();
                attempt = 0;
                var waitMilliseconds = DemoOpenWaitMilliseconds;
                while (demoSettle.ElapsedMilliseconds < waitMilliseconds)
                {
                    Validate();
                    Scope? editor = null;
                    try { editor = ReadEditor("发送后", ++attempt); }
                    catch (Exception ex) when (IsTransientOpenError(ex))
                    {
                        cached = null;
                        Trace($"瞬态控件失效;尝试={attempt};HResult=0x{ex.HResult:X8};仅重新识别，不判失败。");
                        progress?.Invoke(new(StatusKind.Processing, "弹窗正在更新，等待输入框就绪…"));
                    }
                    if (editor is not null)
                    {
                        Validate();
                        cached = editor;
                        log.Write("打开重命名", "触发后已绑定唯一且语义明确的夹具重命名框；未写入任何名称。");
                        Trace($"确认成功;发送后等待={demoSettle.ElapsedMilliseconds}ms;尝试={attempt}。");
                        return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
                    }
                    if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
                }
                Trace($"等待上限到达;发送后等待={demoSettle.ElapsedMilliseconds}ms;转入只读晚到检查。");
                throw new OpenRenameBlockedException("EDITOR_NOT_CONFIRMED", "已完成输入框就绪等待，暂未确认；将只读检查晚到弹窗，不补发触发或写入。") { RecoveryTarget = identity, RequestId = requestId };
            }

            stage = "定位唯一 Antigravity 主文档";
            AutomationElement? mainDoc = null;
            var docWait = Stopwatch.StartNew();
            while (docWait.ElapsedMilliseconds < 2000)
            {
                Validate();
                try { mainDoc = MainDocument(automation.FromHandle(hwnd), hwnd); if (mainDoc is not null) break; }
                catch (Exception ex) when (IsTransientOpenError(ex)) { Trace($"主文档瞬态失效;HResult=0x{ex.HResult:X8}"); }
                if (cancellation.WaitHandle.WaitOne(120)) cancellation.ThrowIfCancellationRequested();
            }
            if (mainDoc is null) throw new OpenRenameBlockedException("MAIN_DOCUMENT_UNPROVEN", "未能确认唯一 Antigravity 主界面文档，已停止。") { RecoveryTarget = identity, RequestId = requestId };

            stage = "定位当前会话 More actions 入口";
            Validate();
            var shellSlice = Slice(mainDoc, 110, 400, 5, stopDocuments: true, pruneContent: true, stopAt: IsAntigravitySidebarNode);
            var moreButtons = shellSlice.Elements
                .Where(node => node.Properties.ControlType.ValueOrDefault == ControlType.Button && Visible(node) && node.Properties.IsEnabled.ValueOrDefault)
                .Where(node => Recognition.IsConversationMoreActions(Name(node), AriaProps(node),
                    node.Patterns.ExpandCollapse.IsSupported || node.Patterns.Invoke.IsSupported,
                    IsInExcludedZone(node, mainDoc, excludeSidebar: true)))
                .GroupBy(Id).Select(g => g.First()).ToList();
            if (moreButtons.Count == 0)
                throw new OpenRenameBlockedException("MENU_BUTTON_UNPROVEN", "未找到当前会话右上角 More actions 按钮；请确认已打开具体会话。") { RecoveryTarget = identity, RequestId = requestId };
            if (moreButtons.Count > 1)
                throw new OpenRenameBlockedException("MENU_BUTTON_AMBIGUOUS", "存在多个会话 More actions 按钮（如分屏视图），拒绝猜测。") { RecoveryTarget = identity, RequestId = requestId };
            var moreButton = moreButtons[0];

            stage = "展开 More actions 菜单";
            Validate();
            if (moreButton.Patterns.ExpandCollapse.TryGetPattern(out var expandPattern))
            {
                if (expandPattern.ExpandCollapseState.ValueOrDefault != ExpandCollapseState.Expanded)
                    expandPattern.Expand();
            }
            else
            {
                moreButton.Patterns.Invoke.Pattern.Invoke();
            }
            log.Write("打开重命名", "已展开当前会话 More actions 菜单；正在定位 Rename 项。");
            Trace("More actions 已展开。");

            void TryCollapseMenu()
            {
                try
                {
                    if (moreButton.Patterns.ExpandCollapse.TryGetPattern(out var ec) && ec.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded)
                        ec.Collapse();
                }
                catch { }
            }

            stage = "定位并点击 Rename 菜单项";
            bool triggeredRename = false;
            var menuWait = Stopwatch.StartNew();
            while (menuWait.ElapsedMilliseconds < 2000)
            {
                Validate();
                try
                {
                    var items = FindRenameMenuItems(hwnd, mainDoc);
                    if (items.Count > 1)
                    {
                        TryCollapseMenu();
                        throw new OpenRenameBlockedException("RENAME_COMMAND_AMBIGUOUS", "菜单中存在多个重命名项，拒绝猜测。") { RecoveryTarget = identity, RequestId = requestId };
                    }
                    if (items.Count == 1)
                    {
                        var renameItem = items[0];
                        Validate();
                        if (renameItem.Patterns.Invoke.TryGetPattern(out var invokePattern)) invokePattern.Invoke();
                        else renameItem.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
                        triggeredRename = true;
                        log.Write("打开重命名", "已通过 UIA 触发 Rename 菜单项；等待内联输入框。");
                        Trace($"Rename 菜单项已触发;菜单等待={menuWait.ElapsedMilliseconds}ms。");
                        break;
                    }
                }
                catch (OpenRenameBlockedException) { throw; }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    Trace($"菜单查找瞬态失效;HResult=0x{ex.HResult:X8};继续轮询，不重点击。");
                }
                if (cancellation.WaitHandle.WaitOne(80)) cancellation.ThrowIfCancellationRequested();
            }
            if (!triggeredRename)
            {
                TryCollapseMenu();
                throw new OpenRenameBlockedException("RENAME_COMMAND_MISSING", "More actions 菜单中未找到 Rename 项。") { RecoveryTarget = identity, RequestId = requestId };
            }

            stage = "确认内联重命名输入框已打开";
            var settle = Stopwatch.StartNew();
            attempt = 0;
            while (settle.ElapsedMilliseconds < 6000)
            {
                Validate();
                Scope? editor = null;
                try { editor = ReadEditor("触发后", ++attempt); }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    cached = null;
                    Trace($"瞬态控件失效;尝试={attempt};HResult=0x{ex.HResult:X8};仅重新识别，不重新触发菜单。");
                    progress?.Invoke(new(StatusKind.Processing, "界面正在打开输入框，等待就绪…"));
                }
                if (editor is not null)
                {
                    Validate();
                    cached = editor;
                    log.Write("打开重命名", "已绑定唯一且语义明确的 Antigravity 内联重命名输入框；未写入任何名称。");
                    Trace($"确认成功;触发后等待={settle.ElapsedMilliseconds}ms;尝试={attempt}。");
                    return new(cached.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
                }
                if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
            }
            Trace($"等待上限到达;触发后等待={settle.ElapsedMilliseconds}ms;转入只读晚到检查。");
            throw new OpenRenameBlockedException("EDITOR_NOT_CONFIRMED", "已完成输入框就绪等待，暂未确认；将只读检查晚到输入框，不重新触发或写入。") { RecoveryTarget = identity, RequestId = requestId };
        }
        catch (Exception ex)
        {
            string code = ex is OpenRenameBlockedException blocked ? blocked.Code : "UIA_EXCEPTION";
            string detail = ex is OpenRenameBlockedException ? ex.Message : "系统可访问性调用异常，未继续操作。";
            log.Write("打开重命名失败", $"请求={requestId};耗时={elapsed.ElapsedMilliseconds}ms;阶段={stage};代码={code};原因={detail};异常={ex.GetType().FullName};HResult=0x{ex.HResult:X8};调用栈={new StackTrace(ex, false)}");
            throw;
        }
    }
}

internal static class RenameKeys
{
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputData { [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    internal static Input[] EnterSequence() => [Key(0x0D), Key(0x0D, true)];
    internal static Input[] DemoShortcutSequence() => [Key(0x11), Key(0x12), Key(0x52), Key(0x52, true), Key(0x12, true), Key(0x11, true)];
    static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
    internal static bool ModifiersReleased() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    internal static bool DemoShortcutKeysReleased() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x52 }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    internal static bool SendEnter()
    {
        var input = EnterSequence();
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length) return true;
        var release = new[] { Key(0x0D, true) };
        SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        return false;
    }
    internal static bool SendDemoShortcut()
    {
        var input = DemoShortcutSequence();
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) == input.Length) return true;
        // 仅用于隔离演示夹具：部分注入失败时只释放本组按键。
        var release = new[] { Key(0x52, true), Key(0x12, true), Key(0x11, true) };
        SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        return false;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
