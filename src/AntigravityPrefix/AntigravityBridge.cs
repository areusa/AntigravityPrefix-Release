using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.UIA3;

namespace AntigravityPrefix;

public sealed partial class AntigravityBridge(nint demoWindow, LocalLog log) : IDisposable
{
    readonly UIA3Automation automation = new();
    public nint OwnWindow { get; set; }
    internal bool TrackingMode { get; set; }
    internal WindowIdentity? TrackedTarget { get; set; }
    nint lastWindow;
    Scope? cached;
    readonly Dictionary<string, AutomationElement> blocked = new();
    readonly Dictionary<nint, DateTime> wakeCooldown = new();
    string? lastIssue;
    DateTime nextMetadata;
    string? referenceTitle;
    string lastProbeLog = "";
    internal Action? DemoBeforeProbe { get; set; }
    const string InlineSaveId = "inline:enter";
    sealed record Scope(AutomationElement Container, AutomationElement Editor, AutomationElement? Save, EditorBinding Binding, string? MainDocId = null);
    sealed record TreeSlice(List<AutomationElement> Elements, bool Complete, long Milliseconds);

    nint ResolveWindow()
    {
        if (TrackingMode)
        {
            if (TrackedTarget is not { } target || !target.IsCurrent() || !Native.IsWindowVisible(target.Handle) || Native.IsIconic(target.Handle) || TrackingNative.Cloaked(target.Handle)) return 0;
            if (demoWindow == 0 && WindowIdentity.Read(target.Handle) != target) return 0;
            var active = Native.GetForegroundWindow();
            if (active != OwnWindow && !Native.BelongsToWindow(active, target.Handle)) return 0;
            if (lastWindow != target.Handle) { cached = null; referenceTitle = null; nextMetadata = DateTime.MinValue; }
            return lastWindow = target.Handle;
        }
        if (demoWindow != 0) return Native.IsWindow(demoWindow) ? demoWindow : 0;
        var foreground = Native.GetForegroundWindow();
        if (foreground != 0 && (foreground == OwnWindow || Native.BelongsToWindow(foreground, OwnWindow)))
            return IsAntigravity(lastWindow) ? lastWindow : 0;
        var owner = foreground != 0 ? Native.OwnerRoot(foreground) : 0;
        nint result = owner != 0 && IsAntigravity(owner) ? owner
            : foreground != 0 && IsAntigravity(foreground) ? foreground : 0;
        if (result == 0) return 0;
        if (lastWindow != result) { cached = null; referenceTitle = null; nextMetadata = DateTime.MinValue; }
        return lastWindow = result;
    }
    static bool IsAntigravity(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
        var cls = new StringBuilder(256);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString().Contains("Console", StringComparison.OrdinalIgnoreCase)) return false;
        try { return WindowIdentity.Read(hwnd) is not null; }
        catch { return false; }
    }
    static string Id(AutomationElement node) => string.Join(".", node.Properties.RuntimeId.Value);
    static string Name(AutomationElement node) => node.Properties.Name.ValueOrDefault ?? "";
    static string Class(AutomationElement node) => node.Properties.ClassName.ValueOrDefault ?? "";
    static string AutomationId(AutomationElement node) => node.Properties.AutomationId.ValueOrDefault ?? "";
    static string AriaRole(AutomationElement node) => node.Properties.AriaRole.ValueOrDefault ?? "";
    static string AriaProps(AutomationElement node) => node.Properties.AriaProperties.ValueOrDefault ?? "";
    static bool Visible(AutomationElement node) => !node.Properties.IsOffscreen.ValueOrDefault;
    static bool Selected(AutomationElement element) =>
        (element.Patterns.SelectionItem.TryGetPattern(out var pattern) && pattern.IsSelected.Value) || Recognition.IsCurrentMarker(AriaProps(element));
    static bool IsAntigravitySidebarNode(AutomationElement node) =>
        AriaRole(node) == "navigation" && (Name(node).Equals("Sidebar", StringComparison.OrdinalIgnoreCase) || Recognition.HasCssToken(Class(node), "bg-sidebar"));
    static bool IsAntigravityBreadcrumbNode(AutomationElement node) =>
        AriaRole(node) == "navigation" && Name(node).Equals("Breadcrumb", StringComparison.OrdinalIgnoreCase);
    static bool IsAntigravityExcludedZone(AutomationElement node)
    {
        var name = Name(node);
        if (name.Equals("Side Panel", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory Viewer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory Viewer header", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Conversation turns", StringComparison.OrdinalIgnoreCase)) return true;
        return AutomationId(node).Equals("antigravity.agentSidePanelInputBox", StringComparison.Ordinal);
    }
    static bool IsAntigravityChatContent(AutomationElement node)
    {
        if (IsAntigravityExcludedZone(node)) return true;
        var cls = Class(node);
        return cls.Contains("scrollbar-hide", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("md-table-bleed", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("thread-scroll", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("bili-", StringComparison.OrdinalIgnoreCase);
    }
    static bool IsSidebarLeaf(AutomationElement node) =>
        IsAntigravityExcludedZone(node)
        || node.Properties.ControlType.ValueOrDefault is ControlType.Button or ControlType.Hyperlink or ControlType.Text or ControlType.Image or ControlType.Edit;

    TreeSlice Slice(AutomationElement root, int limit, int milliseconds, int maxDepth = 18, bool stopDocuments = true, bool pruneContent = true, Func<AutomationElement, bool>? stopAt = null)
    {
        var nodes = new List<AutomationElement>();
        var queue = new Queue<(AutomationElement Node, int Depth)>();
        queue.Enqueue((root, 0));
        var watch = Stopwatch.StartNew();
        var complete = true;
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        while (queue.Count > 0)
        {
            if (nodes.Count >= limit || watch.ElapsedMilliseconds >= milliseconds) { complete = false; break; }
            var (node, depth) = queue.Dequeue();
            nodes.Add(node);
            var type = node.Properties.ControlType.ValueOrDefault;
            if (stopDocuments && depth > 0 && type == ControlType.Document) continue;
            if (pruneContent && depth > 0 && IsAntigravityChatContent(node)) continue;
            if (depth > 0 && stopAt?.Invoke(node) == true) continue;
            var child = walker.GetFirstChild(node);
            if (depth >= maxDepth) { if (child is not null) complete = false; continue; }
            var siblings = 0;
            while (child is not null)
            {
                if (++siblings > limit || watch.ElapsedMilliseconds >= milliseconds) { complete = false; break; }
                queue.Enqueue((child, depth + 1));
                child = walker.GetNextSibling(child);
            }
        }
        return new TreeSlice(nodes, complete, watch.ElapsedMilliseconds);
    }

    bool TryWakeAccessibility(nint hwnd)
    {
        if (demoWindow != 0 || hwnd == 0) return false;
        var now = DateTime.UtcNow;
        if (wakeCooldown.TryGetValue(hwnd, out var next) && now < next) return false;
        if (wakeCooldown.Count > 32) wakeCooldown.Clear();
        wakeCooldown[hwnd] = now.AddMilliseconds(1500);
        bool woke = Native.WakeAccessibility(hwnd);
        log.Write("AX唤醒", woke ? "已通过标准可访问性接口唤醒渲染树。" : "已尝试唤醒但未取得可访问接口。");
        return woke;
    }
    bool HasOwnedNativeRoot(AutomationElement node, nint hwnd, uint pid)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var native = node;
        for (int depth = 0; native is not null && depth < 28; depth++)
        {
            if (native.Properties.ProcessId.ValueOrDefault != pid) return false;
            var handle = native.Properties.NativeWindowHandle.ValueOrDefault;
            if (handle != 0) return Native.BelongsToWindow(handle, hwnd);
            native = walker.GetParent(native);
        }
        return false;
    }
    bool IsInExcludedZone(AutomationElement node, AutomationElement? stopRoot = null, bool excludeSidebar = false)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        string? stopId = stopRoot is null ? null : Id(stopRoot);
        var current = node;
        for (int depth = 0; current is not null && depth < 16; depth++)
        {
            if (IsAntigravityExcludedZone(current) || (excludeSidebar && IsAntigravitySidebarNode(current))) return true;
            if (stopId is not null && Id(current) == stopId) break;
            if (current.Properties.ControlType.ValueOrDefault == ControlType.Document) break;
            current = walker.GetParent(current);
        }
        return false;
    }

    List<AutomationElement> RelatedRoots(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var result = new List<AutomationElement> { automation.FromHandle(hwnd) };
        foreach (var popup in Native.OwnedWindows(hwnd, pid)) result.Add(automation.FromHandle(popup));
        return result;
    }
    bool IsDialog(AutomationElement node, nint main)
    {
        var role = node.Properties.AriaRole.ValueOrDefault;
        if (role == "dialog" || node.Properties.IsDialog.ValueOrDefault) return true;
        return node.Properties.ControlType.ValueOrDefault == ControlType.Window &&
            node.Properties.NativeWindowHandle.ValueOrDefault != main && Recognition.IsRenameLabel(Name(node));
    }
    Scope? InspectDialog(AutomationElement container, nint hwnd)
    {
        if (!Visible(container) || !container.Properties.IsEnabled.ValueOrDefault || !IsDialog(container, hwnd))
        { log.Write("识别拒绝-对话框", "候选不可见、禁用或不具备对话框语义。"); return null; }
        if (!IsHostScope(container, hwnd))
        { log.Write("识别拒绝-宿主", "候选未通过宿主文档或原生归属核验。"); return null; }
        var tree = Slice(container, 100, 650, 14, true, false);
        if (!tree.Complete) { lastIssue = "输入框读取不完整，请保存诊断"; log.Write("重命名框", "局部结构未完整读取，拒绝绑定"); return null; }
        var renameSemantic = Recognition.IsRenameLabel(Name(container)) || tree.Elements.Any(node =>
            (node.Properties.ControlType.ValueOrDefault == ControlType.Text || node.Properties.ControlType.ValueOrDefault == ControlType.Header)
            && Recognition.IsRenameLabel(Name(node)));
        if (!renameSemantic) { log.Write("识别拒绝-语义", "对话框没有明确的重命名标题。"); return null; }
        openDialogEvidence?.Add(Id(container));
        var edits = tree.Elements.Where(node => node.Properties.ControlType.ValueOrDefault == ControlType.Edit && Visible(node)
            && node.Properties.IsEnabled.ValueOrDefault && !node.Properties.IsPassword.ValueOrDefault
            && node.Patterns.Value.IsSupported && !node.Patterns.Value.Pattern.IsReadOnly.Value).ToList();
        // 原生“保存”在名称尚未改变时可以处于禁用状态；提交前再确认已启用。
        var saves = tree.Elements.Where(node => node.Properties.ControlType.ValueOrDefault == ControlType.Button && Visible(node)
            && Recognition.IsSaveLabel(Name(node)) && node.Patterns.Invoke.IsSupported).ToList();
        if (!Recognition.IsUnambiguousRenameScope(true, renameSemantic, edits.Count, saves.Count, tree.Complete))
        {
            lastIssue = edits.Count > 1 ? "重命名框含多个输入框，已禁用" : "未找到唯一编辑与保存操作，已禁用";
            log.Write("重命名框", $"编辑框数量={edits.Count}，保存操作数量={saves.Count}，拒绝歧义");
            return null;
        }
        var value = edits[0].Patterns.Value.Pattern.Value.Value;
        if (!Recognition.IsValidTitle(value)) { log.Write("识别拒绝-文本", "输入值不满足单行非空标题规则；未记录原文。"); return null; }
        var binding = new EditorBinding(hwnd, automation.FromHandle(hwnd).Properties.ProcessId.ValueOrDefault,
            Id(container), Id(edits[0]), Id(saves[0]), value);
        return new Scope(container, edits[0], saves[0], binding);
    }
    bool IsHostScope(AutomationElement container, nint hwnd)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var node = container;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (container.Properties.ProcessId.ValueOrDefault != pid) return false;
        // 独立原生 Window（含夹具）直接核对自身句柄；网页容器不得借祖先句柄走此分支。
        var containerHandle = container.Properties.NativeWindowHandle.ValueOrDefault;
        if (container.Properties.ControlType.ValueOrDefault == ControlType.Window && containerHandle != 0)
            return Native.BelongsToWindow(containerHandle, hwnd);
        if (demoWindow != 0) return false;
        for (var depth = 0; node is not null && depth < 28; depth++)
        {
            if (node.Properties.ProcessId.ValueOrDefault != pid) return false;
            if (IsAntigravityExcludedZone(node)) return false;
            if (node.Properties.ControlType.ValueOrDefault == ControlType.Document)
            {
                var main = MainDocument(automation.FromHandle(hwnd), hwnd);
                if (main is null || Id(main) != Id(node))
                {
                    lastIssue = "已排除内置网页，请打开会话重命名";
                    log.Write("识别拒绝-文档", main is null ? "未完整定位唯一主文档。" : "候选属于另一文档；不接管内置网页。");
                    return false;
                }
                return true;
            }
            node = walker.GetParent(node);
        }
        return false;
    }
    Scope? InspectInlineEditor(AutomationElement editor, nint hwnd, AutomationElement? knownMainDoc = null, string? knownMainDocId = null)
    {
        if (!Visible(editor) || !editor.Properties.IsEnabled.ValueOrDefault) return null;
        if (editor.Properties.ControlType.ValueOrDefault != ControlType.Edit || editor.Properties.IsPassword.ValueOrDefault) return null;
        if (!editor.Patterns.Value.TryGetPattern(out var valuePattern) || valuePattern.IsReadOnly.Value) return null;
        bool multiline = Recognition.IsMultilineAria(AriaProps(editor));
        var editorName = Name(editor);
        if (multiline || !Recognition.IsRenameLabel(editorName)) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (editor.Properties.ProcessId.ValueOrDefault != pid) return null;
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        bool inTrustedRenameZone = false;
        bool inExcludedZone = false;
        AutomationElement? foundDoc = null;
        var ancestor = walker.GetParent(editor);
        for (int depth = 0; ancestor is not null && depth < 24; depth++)
        {
            if (ancestor.Properties.ProcessId.ValueOrDefault != pid)
            {
                log.Write("识别拒绝-焦点", "输入框祖先不属于目标进程。");
                return null;
            }
            if (IsAntigravityExcludedZone(ancestor)) inExcludedZone = true;
            if (IsAntigravitySidebarNode(ancestor) || IsAntigravityBreadcrumbNode(ancestor) || IsDialog(ancestor, hwnd))
                inTrustedRenameZone = true;
            if (ancestor.Properties.ControlType.ValueOrDefault == ControlType.Document)
            {
                foundDoc = ancestor;
                break;
            }
            var handle = ancestor.Properties.NativeWindowHandle.ValueOrDefault;
            if (demoWindow != 0 && handle != 0 && Native.BelongsToWindow(handle, hwnd))
            {
                inTrustedRenameZone = true;
                break;
            }
            ancestor = walker.GetParent(ancestor);
        }
        string? verifiedMainDocId = null;
        if (demoWindow != 0)
        {
            if (foundDoc is not null || inExcludedZone || !inTrustedRenameZone) return null;
        }
        else
        {
            if (foundDoc is null)
            {
                log.Write("识别拒绝-内联输入框", "未找到所属文档边界。");
                return null;
            }
            var foundDocId = Id(foundDoc);
            string? expectedDocId = knownMainDocId ?? (knownMainDoc is not null ? Id(knownMainDoc) : null);
            if (expectedDocId is null)
            {
                var mainDoc = MainDocument(automation.FromHandle(hwnd), hwnd);
                expectedDocId = mainDoc is null ? null : Id(mainDoc);
            }
            bool sameMainDoc = expectedDocId is not null && expectedDocId == foundDocId;
            bool verified = WindowIdentity.Read(hwnd) is not null;
            bool ownedRoot = HasOwnedNativeRoot(foundDoc, hwnd, pid);
            if (!Recognition.IsTrustedAntigravityInlineEditor(sameMainDoc, verified, ownedRoot, inTrustedRenameZone, inExcludedZone, editorName, true, multiline, false))
            {
                log.Write("识别拒绝-内联输入框", $"主文档一致={sameMainDoc};官方窗口={verified};原生归属={ownedRoot};信任区域={inTrustedRenameZone};排除区域={inExcludedZone}");
                return null;
            }
            verifiedMainDocId = expectedDocId;
        }
        var value = valuePattern.Value.Value;
        if (!Recognition.IsValidTitle(value)) { log.Write("识别拒绝-文本", "输入值不满足单行非空标题规则；未记录原文。"); return null; }
        var id = Id(editor);
        openDialogEvidence?.Add(id);
        var binding = new EditorBinding(hwnd, (int)pid, id, id, InlineSaveId, value, InlineCommit: true);
        return new Scope(editor, editor, null, binding, verifiedMainDocId);
    }
    Scope? InspectScope(Scope scope, nint hwnd, string? knownMainDocId = null) =>
        scope.Binding.InlineCommit ? InspectInlineEditor(scope.Editor, hwnd, knownMainDocId: knownMainDocId) : InspectDialog(scope.Container, hwnd);
    Scope? FocusedScope(nint hwnd, string? knownMainDocId = null)
    {
        try
        {
            var current = automation.FocusedElement();
            if (current is null) return null;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (current.Properties.ProcessId.ValueOrDefault != pid)
            { log.Write("识别拒绝-焦点", "焦点控件不属于目标进程，改用受限发现。"); return null; }
            var inline = InspectInlineEditor(current, hwnd, knownMainDocId: knownMainDocId);
            if (inline is not null) return inline;
            var walker = automation.TreeWalkerFactory.GetControlViewWalker();
            for (var depth = 0; current is not null && depth < 24; depth++)
            {
                if (current.Properties.ProcessId.ValueOrDefault != pid)
                { log.Write("识别拒绝-焦点", "焦点祖先不属于目标进程，改用受限发现。"); return null; }
                if (IsDialog(current, hwnd))
                {
                    // 同进程另一窗口的焦点不是当前目标，必须核对原生归属。
                    if (HasOwnedNativeRoot(current, hwnd, pid)) return InspectDialog(current, hwnd);
                    return null;
                }
                current = walker.GetParent(current);
            }
            log.Write("识别拒绝-焦点", "焦点祖先中没有可核验重命名框，改用受限发现。");
            return null;
        }
        catch (Exception ex) when (IsTransientOpenError(ex))
        {
            log.Write("识别瞬态", $"焦点读取瞬态失效(0x{ex.HResult:X8})，转受限发现。");
            return null;
        }
    }
    // Antigravity 同窗口含多个 RootWebArea；必须核验唯一主文档签名，不进入内置网页或长对话正文。
    AutomationElement? MainDocument(AutomationElement root, nint hwnd)
    {
        if (demoWindow != 0) return null;
        if (WindowIdentity.Read(hwnd) is null) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        TreeSlice ReadShell() => Slice(root, 120, 350, 12, stopDocuments: true, pruneContent: false);
        var shell = ReadShell();
        var documents = shell.Elements.Where(x => x.Properties.ControlType.ValueOrDefault == ControlType.Document).ToArray();
        if (documents.Length == 0 && TryWakeAccessibility(hwnd))
        {
            Thread.Sleep(120);
            shell = ReadShell();
            documents = shell.Elements.Where(x => x.Properties.ControlType.ValueOrDefault == ControlType.Document).ToArray();
        }
        if (!shell.Complete || documents.Length == 0)
        {
            log.Write("主文档边界", $"完整={shell.Complete};顶层文档数量={documents.Length};未取得可核验文档。");
            return null;
        }
        var matched = new List<AutomationElement>();
        foreach (var doc in documents)
        {
            if (!HasOwnedNativeRoot(doc, hwnd, pid)) continue;
            var inner = Slice(doc, 90, 260, maxDepth: 3, stopDocuments: true, pruneContent: false);
            bool hasStandaloneTheme = inner.Elements.Any(n => Recognition.HasCssToken(Class(n), "theme-standalone"));
            bool hasRootContainer = inner.Elements.Any(n => AutomationId(n) == "root");
            bool hasShellLandmark = inner.Elements.Any(n => IsAntigravitySidebarNode(n) || IsAntigravityBreadcrumbNode(n)
                || (n.Properties.ControlType.ValueOrDefault == ControlType.Button && Recognition.HasCssToken(Class(n), "agy-button")));
            if (Recognition.IsAntigravityMainShell(true, true, hasStandaloneTheme, hasRootContainer, hasShellLandmark))
                matched.Add(doc);
        }
        if (matched.Count != 1)
        {
            log.Write("主文档边界", $"顶层文档={documents.Length};匹配主文档={matched.Count};拒绝猜测。");
            return null;
        }
        return matched[0];
    }
    List<Scope> FindScopes(nint hwnd)
    {
        var found = new List<Scope>();
        if (demoWindow != 0)
        {
            foreach (var root in RelatedRoots(hwnd))
            {
                if (IsDialog(root, hwnd))
                {
                    var direct = InspectDialog(root, hwnd);
                    if (direct is not null) found.Add(direct);
                    continue;
                }
                var slice = Slice(root, 260, 650, 18);
                foreach (var node in slice.Complete ? slice.Elements : [])
                {
                    if (IsDialog(node, hwnd))
                    {
                        var scope = InspectDialog(node, hwnd);
                        if (scope is not null) found.Add(scope);
                    }
                    else if (node.Properties.ControlType.ValueOrDefault == ControlType.Edit && Recognition.IsRenameLabel(Name(node)))
                    {
                        var inline = InspectInlineEditor(node, hwnd);
                        if (inline is not null) found.Add(inline);
                    }
                }
            }
            return found.GroupBy(x => x.Binding.Key).Select(x => x.First()).ToList();
        }
        var mainDoc = MainDocument(automation.FromHandle(hwnd), hwnd);
        if (mainDoc is null) return found;
        var shell = Slice(mainDoc, 140, 320, maxDepth: 4, stopDocuments: true, pruneContent: true, stopAt: node => IsAntigravitySidebarNode(node) || IsAntigravityBreadcrumbNode(node));
        foreach (var node in shell.Elements)
            if (IsDialog(node, hwnd))
            {
                var scope = InspectDialog(node, hwnd);
                if (scope is not null) found.Add(scope);
            }
        foreach (var breadcrumb in shell.Elements.Where(IsAntigravityBreadcrumbNode))
        {
            var area = Slice(breadcrumb, 40, 200, maxDepth: 6, stopDocuments: true, pruneContent: false);
            foreach (var node in area.Elements.Where(n => n.Properties.ControlType.ValueOrDefault == ControlType.Edit))
            {
                var inline = InspectInlineEditor(node, hwnd, mainDoc);
                if (inline is not null) found.Add(inline);
            }
        }
        foreach (var sidebar in shell.Elements.Where(IsAntigravitySidebarNode))
        {
            var area = Slice(sidebar, 360, 550, maxDepth: 7, stopDocuments: true, pruneContent: false, stopAt: IsSidebarLeaf);
            if (!area.Complete) log.Write("局部探测", "侧边栏受限读取已截断；只允许焦点直达既有输入框，不按列表推测。");
            foreach (var node in area.Elements.Where(n => n.Properties.ControlType.ValueOrDefault == ControlType.Edit))
            {
                var inline = InspectInlineEditor(node, hwnd, mainDoc);
                if (inline is not null) found.Add(inline);
            }
        }
        return found.GroupBy(x => x.Binding.Key).Select(x => x.First()).ToList();
    }

    void ReadMetadata(nint hwnd, Action<WorkStatus>? progress)
    {
        if (DateTime.UtcNow < nextMetadata) return;
        nextMetadata = DateTime.UtcNow.AddSeconds(8);
        progress?.Invoke(new(StatusKind.Processing, "正在读取主会话…"));
        var document = MainDocument(automation.FromHandle(hwnd), hwnd);
        referenceTitle = document is null ? null : Name(document);
        log.Write("主会话", document is null ? "未取得唯一 Antigravity 主文档；真实会话 ID 未确认" : "已取得参考标题；无可信的当前会话 ID 来源，不进行标题映射");
        if (document is null) return;
        progress?.Invoke(new(StatusKind.Processing, "正在核对 Antigravity 结构…"));
        var structure = Slice(document, 90, 300, maxDepth: 4, stopDocuments: true, pruneContent: true);
        bool hasSidebar = structure.Elements.Any(IsAntigravitySidebarNode);
        bool hasBreadcrumb = structure.Elements.Any(IsAntigravityBreadcrumbNode);
        log.Write("主界面结构", $"节点={structure.Elements.Count}，完整={structure.Complete}，侧栏={hasSidebar}，顶部导航={hasBreadcrumb}；未暴露选中态标记，不按标题猜测");
    }
    public ProbeResult Probe(Action<WorkStatus>? progress = null)
    {
        if (demoWindow != 0) DemoBeforeProbe?.Invoke();
        lastIssue = null;
        var hwnd = ResolveWindow();
        if (hwnd == 0)
        {
            cached = null;
            return new(null, null, new(StatusKind.NeedsAction, "请在 Antigravity 中打开重命名框"));
        }
        Scope? candidate;
        try
        {
            candidate = FocusedScope(hwnd);
            if (candidate is null && cached is not null && cached.Binding.Window == hwnd)
            {
                try { candidate = InspectScope(cached, hwnd); } catch { cached = null; }
            }
            if (candidate is null)
            {
                ReadMetadata(hwnd, progress);
                var scopes = FindScopes(hwnd);
                if (scopes.Count > 1) return new(null, referenceTitle, new(StatusKind.NeedsAction, "发现多个重命名框，请只保留一个"));
                candidate = scopes.SingleOrDefault();
            }
        }
        catch (Exception ex) when (IsTransientOpenError(ex))
        {
            cached = null;
            log.Write("识别瞬态", $"探测期间控件重建(0x{ex.HResult:X8})，本轮跳过。");
            return new(null, referenceTitle, new(StatusKind.NeedsAction, "原生界面正在更新，请稍候…"));
        }
        if (candidate is null)
        {
            cached = null;
            foreach (var pending in blocked.ToArray())
            {
                try { if (HasClosed(pending.Value)) blocked.Remove(pending.Key); }
                catch { /* 无法读取时保留封锁，不能把读取失败视为关闭。 */ }
            }
            LogProbe("等待手动打开原生重命名框");
            return new(null, referenceTitle, new(StatusKind.NeedsAction, lastIssue ?? "请先打开 Antigravity 的重命名框", "点击编辑按钮自动打开，或在会话右上角 ⋯ / 侧栏 ⋯ 选择 Rename。若已打开仍未识别，请保存诊断。不会按同名标题查找。"));
        }
        cached = candidate;
        if (blocked.ContainsKey(candidate.Binding.Key))
            return new(null, referenceTitle, new(StatusKind.NeedsAction, "请先关闭此输入框，再重新打开", "上次操作已写入或尝试提交，禁止自动重复。请先检查原生界面。"));
        LogProbe("已绑定用户打开的唯一重命名框；不依赖会话标题或会话列表排序");
        return new(candidate.Binding, referenceTitle, new(StatusKind.Ready, "已识别重命名框，可选择前缀"));
    }
    void LogProbe(string message)
    {
        if (lastProbeLog == message) return;
        lastProbeLog = message; log.Write("识别结果", message);
    }
    static string DescribeWindow(nint hwnd)
    {
        if (hwnd == 0) return "0x0";
        var cls = new StringBuilder(128);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        string proc = "";
        try { using var p = Process.GetProcessById((int)pid); proc = System.IO.Path.GetFileName(p.MainModule?.FileName ?? ""); } catch { }
        return $"0x{hwnd:X}(cls={cls},proc={proc})";
    }
    internal static T? PollTransient<T>(Func<T?> read, Func<T, bool> accept, int timeoutMilliseconds, int delayMilliseconds, CancellationToken cancellation, Action<Exception>? onTransient = null, Func<int>? sleepMilliseconds = null) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var value = read();
                if (value is not null && accept(value)) return value;
            }
            catch (Exception ex) when (IsTransientOpenError(ex))
            {
                onTransient?.Invoke(ex);
            }
            if (watch.ElapsedMilliseconds >= timeoutMilliseconds) return null;
            int wait = sleepMilliseconds?.Invoke() ?? delayMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }
    nint ResolveWindowWithRetry()
    {
        var resolved = ResolveWindow();
        if (resolved == 0 && Native.GetForegroundWindow() == 0)
        {
            Thread.Sleep(35);
            resolved = ResolveWindow();
        }
        return resolved;
    }
    Scope? TryInspectMatchingScope(Scope reference, EditorBinding expectedBinding, string? knownMainDocId, string stage)
    {
        bool directTransient = false;
        Scope? direct = null;
        try
        {
            direct = InspectScope(reference, expectedBinding.Window, knownMainDocId);
        }
        catch (Exception ex) when (IsTransientOpenError(ex))
        {
            directTransient = true;
            log.Write("重命名瞬态", $"阶段={stage};动作=InspectScope;HResult=0x{ex.HResult:X8}");
        }
        if (direct is not null)
        {
            if (Recognition.IsSameBinding(expectedBinding, direct.Binding)) return direct;
            if (direct.Binding.ScopeId == expectedBinding.ScopeId && direct.Binding.EditorId == expectedBinding.EditorId) return null;
        }
        var focused = FocusedScope(expectedBinding.Window, knownMainDocId);
        if (focused is not null && Recognition.IsSameBinding(expectedBinding, focused.Binding)) return focused;
        if (directTransient || direct is null)
        {
            try
            {
                var scopes = FindScopes(expectedBinding.Window);
                if (scopes.Count == 1 && Recognition.IsSameBinding(expectedBinding, scopes[0].Binding)) return scopes[0];
            }
            catch (Exception ex) when (IsTransientOpenError(ex))
            {
                log.Write("重命名瞬态", $"阶段={stage};动作=FindScopes;HResult=0x{ex.HResult:X8}");
            }
        }
        return null;
    }
    Scope Revalidate(EditorBinding expected)
    {
        var resolved = ResolveWindowWithRetry();
        if (resolved != expected.Window)
        {
            log.Write("重命名核验失败", $"原因=窗口不匹配;expected=0x{expected.Window:X};resolved=0x{resolved:X};fg={DescribeWindow(Native.GetForegroundWindow())};own=0x{OwnWindow:X};last=0x{lastWindow:X};tracking={TrackingMode};cached={cached is not null}");
            throw new InvalidOperationException("目标窗口已变化，请重新打开重命名框");
        }
        Scope? ReadOnce(out bool sawTransient)
        {
            sawTransient = false;
            Scope? found = null;
            if (cached is not null)
            {
                try { found = InspectScope(cached, expected.Window); }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    cached = null;
                    sawTransient = true;
                    log.Write("重命名瞬态", $"阶段=写入前核验;动作=InspectScope;HResult=0x{ex.HResult:X8}");
                }
                catch { cached = null; }
            }
            if (found is null) found = FocusedScope(expected.Window);
            if (found is null)
            {
                try
                {
                    var scopes = FindScopes(expected.Window);
                    if (scopes.Count == 1) found = scopes[0];
                }
                catch (Exception ex) when (IsTransientOpenError(ex))
                {
                    sawTransient = true;
                    log.Write("重命名瞬态", $"阶段=写入前核验;动作=FindScopes;HResult=0x{ex.HResult:X8}");
                }
            }
            return found;
        }
        var watch = Stopwatch.StartNew();
        Scope? current;
        while (true)
        {
            current = ReadOnce(out bool sawTransient);
            if (current is not null || !sawTransient || watch.ElapsedMilliseconds >= 600) break;
            Thread.Sleep(50);
        }
        if (current is null || !Recognition.IsSameBinding(expected, current.Binding))
        {
            log.Write("重命名核验失败", $"原因=绑定不匹配;found={current is not null};sameWin={current?.Binding.Window == expected.Window};sameScope={current?.Binding.ScopeId == expected.ScopeId};sameEdit={current?.Binding.EditorId == expected.EditorId};sameVal={current?.Binding.OriginalValue == expected.OriginalValue};lenExp={expected.OriginalValue.Length};lenAct={current?.Binding.OriginalValue.Length ?? -1};expScope={expected.ScopeId};actScope={current?.Binding.ScopeId}");
            throw new InvalidOperationException("输入框身份或内容已变化，本次未提交");
        }
        cached = current;
        return current;
    }
    bool HasEditorFocus(AutomationElement editor, string editorId)
    {
        try { if (editor.Properties.HasKeyboardFocus.TryGetValue(out bool focused) && focused) return true; } catch { }
        try { var focused = automation.FocusedElement(); return focused is not null && Id(focused) == editorId; } catch { return false; }
    }
    internal RenameResult Rename(EditorBinding expected, string? icon, Action<WorkStatus> progress, CancellationToken cancellation = default, PrefixSnapshot? rules = null, Func<bool>? rulesCurrent = null)
    {
        progress(new(StatusKind.Processing, "正在核对输入框身份…"));
        var scope = Revalidate(expected);
        rules ??= PrefixSnapshot.Default;
        if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未写入。");
        var newTitle = PrefixRules.Apply(expected.OriginalValue, icon, rules);
        if (!Recognition.IsValidTitle(newTitle)) throw new InvalidOperationException("处理后标题为空或无效，本次未提交");
        if (newTitle == expected.OriginalValue)
        {
            log.Write("重命名", "名称无需改变，未写入也未提交");
            return new(newTitle, new(StatusKind.Completed, "名称无需修改，可继续选择前缀"));
        }
        cancellation.ThrowIfCancellationRequested();
        if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未写入。");
        blocked[expected.Key] = scope.Container;
        var knownMainDocId = scope.MainDocId;
        progress(new(StatusKind.Processing, "正在修改名称…"));
        log.Write("重命名", "已校验窗口、输入框及原始文本，开始写入");
        try
        {
            scope.Editor.Patterns.Value.Pattern.SetValue(newTitle);
        }
        catch (Exception ex) when (IsTransientOpenError(ex))
        {
            // UIA SetValue 发送后元素可能立刻重绘失效；绝不重放 SetValue，只做严格回读核验。
            log.Write("重命名瞬态", $"阶段=SetValue;HResult=0x{ex.HResult:X8};已禁止二次写入，转入写入后严格核验。");
        }
        var expectedAfter = expected with { OriginalValue = newTitle };
        var updated = PollTransient(
            () => TryInspectMatchingScope(scope, expectedAfter, knownMainDocId, "写入后核验"),
            candidate => candidate.Binding.InlineCommit || candidate.Save?.Properties.IsEnabled.ValueOrDefault == true,
            1500,
            50,
            cancellation,
            ex => log.Write("重命名瞬态", $"阶段=写入后核验;HResult=0x{ex.HResult:X8}"));
        if (updated is null || !Recognition.IsSameBinding(expectedAfter, updated.Binding))
            throw new InvalidOperationException("写入后内容或控件发生变化，未提交");
        if (ResolveWindowWithRetry() != expected.Window) throw new InvalidOperationException("前台窗口已变化，未提交");
        cancellation.ThrowIfCancellationRequested();
        if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未保存。");
        if (!updated.Binding.InlineCommit)
        {
            if (updated.Save is null || !updated.Save.Properties.IsEnabled.ValueOrDefault)
                throw new InvalidOperationException("原生保存按钮仍不可用，未提交");
            progress(new(StatusKind.Processing, "正在提交原生重命名…"));
            updated.Save.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            if (!Native.BelongsToWindow(Native.GetForegroundWindow(), expected.Window))
            {
                Native.SetForegroundWindow(expected.Window);
                var fgWait = Stopwatch.StartNew();
                while (!Native.BelongsToWindow(Native.GetForegroundWindow(), expected.Window) && fgWait.ElapsedMilliseconds < 300)
                    Thread.Sleep(25);
                if (!Native.BelongsToWindow(Native.GetForegroundWindow(), expected.Window))
                    throw new InvalidOperationException("前台窗口未处于目标宿主，未发送回车提交");
            }
            if (!HasEditorFocus(updated.Editor, expectedAfter.EditorId))
            {
                try { updated.Editor.Focus(); } catch { }
                var focusWait = Stopwatch.StartNew();
                while (!HasEditorFocus(updated.Editor, expectedAfter.EditorId) && focusWait.ElapsedMilliseconds < 250)
                    Thread.Sleep(35);
            }
            var focusedCheck = PollTransient(
                () => TryInspectMatchingScope(updated, expectedAfter, knownMainDocId, "提交前核验"),
                candidate => HasEditorFocus(candidate.Editor, expectedAfter.EditorId),
                600,
                45,
                cancellation,
                ex => log.Write("重命名瞬态", $"阶段=提交前核验;HResult=0x{ex.HResult:X8}"));
            if (focusedCheck is null || !Recognition.IsSameBinding(expectedAfter, focusedCheck.Binding) || !HasEditorFocus(focusedCheck.Editor, expectedAfter.EditorId))
                throw new InvalidOperationException("写入后输入框未保持焦点或内容已变化，未发送回车提交");
            updated = focusedCheck;
            var modWait = Stopwatch.StartNew();
            while (!RenameKeys.ModifiersReleased() && modWait.ElapsedMilliseconds < 300) Thread.Sleep(25);
            if (!RenameKeys.ModifiersReleased()) throw new InvalidOperationException("检测到修饰键尚未松开，未发送回车提交");
            cancellation.ThrowIfCancellationRequested();
            if (rulesCurrent?.Invoke() == false) throw new InvalidOperationException("emoji 配置已经改变，本次未保存。");
            progress(new(StatusKind.Processing, "正在按回车提交原生重命名…"));
            if (!RenameKeys.SendEnter()) throw new InvalidOperationException("Windows 未允许发送回车提交，请检查原生输入框");
            log.Write("重命名", "已校验前台、焦点与写入文本，并发送回车提交；等待原输入框关闭。");
        }
        progress(new(StatusKind.Processing, "正在确认提交状态…"));
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 1800)
        {
            if (HasClosed(updated.Container) || HasClosed(updated.Editor)) return Submitted(newTitle);
            Thread.Sleep(80);
        }
        log.Write("重命名", "已尝试提交，但输入框未关闭；不自动重试");
        return new(newTitle, new(StatusKind.NeedsAction, "保存未确认，请检查原生输入框"));
    }
    RenameResult Submitted(string name)
    {
        cached = null;
        log.Write("重命名", "已提交重命名且原输入框关闭；不声称已核实持久化会话名");
        return new(name, new(StatusKind.Submitted, "已提交，请核对原生会话名"));
    }
    static bool HasClosed(AutomationElement node)
    {
        try { return !Visible(node); }
        catch (ElementNotAvailableException) { return true; }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80040201)) { return true; }
    }
    public string Diagnostic()
    {
        var output = new StringBuilder($"会话前缀 {typeof(App).Assembly.GetName().Version?.ToString(3)} · 局部只读诊断\n可能包含名称，仅本地保存。\n仅反映本次控件树；不按名称猜测会话，不等于持久化写入验收。\n");
        var hwnd = ResolveWindow();
        if (hwnd == 0) return output.AppendLine("没有可绑定的 Antigravity 窗口。先打开原生重命名框，再点击诊断。").ToString();
        var cls = new StringBuilder(256);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var exePath = proc.MainModule?.FileName ?? "";
            var exeName = System.IO.Path.GetFileName(exePath);
            var ver = string.IsNullOrEmpty(exePath) ? null : FileVersionInfo.GetVersionInfo(exePath);
            var dir = System.IO.Path.GetDirectoryName(exePath);
            bool hasAsar = !string.IsNullOrEmpty(dir) && System.IO.File.Exists(System.IO.Path.Combine(dir, "resources", "app.asar"));
            output.AppendLine($"窗口身份：HWND=0x{hwnd:X} 类={cls} 进程名={exeName} 产品={ver?.ProductName} 公司={ver?.CompanyName} 版本={ver?.FileVersion} app.asar={hasAsar}");
        }
        catch { output.AppendLine($"窗口身份：HWND=0x{hwnd:X} 类={cls}（进程信息不可读）"); }

        void Dump(string label, AutomationElement rootNode, int limit, int maxDepth = 12, bool stopDocuments = true, Func<AutomationElement, bool>? stopAt = null)
        {
            var slice = Slice(rootNode, limit, 850, maxDepth, stopDocuments, true, stopAt);
            output.AppendLine($"区域={label} 节点={slice.Elements.Count} 完整={slice.Complete} 耗时={slice.Milliseconds}ms");
            foreach (var node in slice.Elements)
                output.AppendLine($"类型={node.Properties.ControlType.ValueOrDefault} | 标识={AutomationId(node)} | 名称={Name(node)} | 类={Class(node)} | 角色={AriaRole(node)} | ARIA属性={AriaProps(node)} | 控件标识={Id(node)}");
        }
        var root = automation.FromHandle(hwnd);
        int docsBefore = Slice(root, 120, 300, 12, stopDocuments: true, pruneContent: false).Elements.Count(x => x.Properties.ControlType.ValueOrDefault == ControlType.Document);
        AutomationElement? mainDoc = MainDocument(root, hwnd);
        int docsAfter = Slice(root, 120, 300, 12, stopDocuments: true, pruneContent: false).Elements.Count(x => x.Properties.ControlType.ValueOrDefault == ControlType.Document);
        output.AppendLine($"顶层文档计数：唤醒前={docsBefore} 唤醒后={docsAfter} 主文档唯一通过={mainDoc is not null}");
        Dump("原生窗口边界", root, 75, 10, true);
        if (mainDoc is not null)
        {
            Dump("主文档浅壳（maxDepth=4）", mainDoc, 90, 4, true, IsAntigravitySidebarNode);
            var shell = Slice(mainDoc, 90, 300, 4, true, true);
            foreach (var btn in shell.Elements.Where(n => n.Properties.ControlType.ValueOrDefault == ControlType.Button && Name(n).Contains("More", StringComparison.OrdinalIgnoreCase)))
                output.AppendLine($"Header候选：名称={Name(btn)} Expand={btn.Patterns.ExpandCollapse.IsSupported} Invoke={btn.Patterns.Invoke.IsSupported} ARIA={AriaProps(btn)} Excluded={IsInExcludedZone(btn, mainDoc)}");
        }
        try
        {
            var focused = automation.FocusedElement();
            if (focused is not null)
            {
                output.AppendLine($"当前焦点：类型={focused.Properties.ControlType.ValueOrDefault} 名称={Name(focused)} 角色={AriaRole(focused)} ARIA={AriaProps(focused)} Readonly={focused.Patterns.Value.TryGetPattern(out var vp) && vp.IsReadOnly.Value}");
                var walker = automation.TreeWalkerFactory.GetControlViewWalker();
                var anc = focused;
                for (int d = 0; anc is not null && d < 12; d++)
                {
                    output.AppendLine($"  ^ d={d} 类型={anc.Properties.ControlType.ValueOrDefault} id={AutomationId(anc)} role={AriaRole(anc)} name={Name(anc)} cls={Class(anc)}");
                    anc = walker.GetParent(anc);
                }
            }
        }
        catch { }
        if (cached is not null) Dump("已绑定重命名框", cached.Container, 60, 8);
        return output.ToString();
    }
    public void Dispose() => automation.Dispose();
}

internal static class Native
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    internal static void RoundCorners(nint hwnd, double radius = 14)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return;
        // 透明分层窗口由 WPF 的覆盖率抗锯齿描绘轮廓，不能再叠加二值 GDI 硬裁切。
        if ((TrackingNative.GetWindowLongPtrW(hwnd, -20).ToInt64() & 0x80000) != 0)
        {
            var noRound = 1; DwmSetWindowAttribute(hwnd, 33, ref noRound, sizeof(int));
            var previous = CreateRectRgn(0, 0, 0, 0);
            bool clipped = previous != 0 && GetWindowRgn(hwnd, previous) > 0;
            if (previous != 0) DeleteObject(previous);
            if (clipped) SetWindowRgn(hwnd, 0, true);
            return;
        }
        var value = 2; DwmSetWindowAttribute(hwnd, 33, ref value, sizeof(int));
        if (!TrackingNative.GetWindowRect(hwnd, out var bounds)) return;
        var diameter = (int)Math.Round(radius * 2 * Math.Max(96, TrackingNative.GetDpiForWindow(hwnd)) / 96d);
        var region = CreateRoundRectRgn(0, 0, bounds.Right - bounds.Left + 1, bounds.Bottom - bounds.Top + 1, diameter, diameter);
        var existing = CreateRectRgn(0, 0, 0, 0);
        bool same = existing != 0 && GetWindowRgn(hwnd, existing) > 0 && EqualRgn(existing, region);
        if (existing != 0) DeleteObject(existing);
        if (same) { DeleteObject(region); return; }
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        // 成功后 region 所有权交给系统；不再手工释放，防止重复释放。
    }
    [DllImport("gdi32.dll")] static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern bool EqualRgn(nint first, nint second);
    [DllImport("user32.dll")] static extern int GetWindowRgn(nint hwnd, nint region);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    internal delegate bool EnumChildProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint parameter);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(nint hwnd, int id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? acc);
    static readonly Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    internal static bool WakeAccessibility(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return false;
        bool any = ProbeAccessible(hwnd);
        var children = new List<nint>();
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (IsWindowVisible(child))
            {
                var cls = new StringBuilder(64);
                if (GetClassName(child, cls, cls.Capacity) > 0 && cls.ToString() == "Chrome_RenderWidgetHostHWND")
                    children.Add(child);
            }
            return true;
        }, 0);
        foreach (var child in children) any |= ProbeAccessible(child);
        return any;
    }
    static bool ProbeAccessible(nint target)
    {
        try
        {
            var iid = IidAccessible;
            int hr = AccessibleObjectFromWindow(target, unchecked((int)0xFFFFFFFC), ref iid, out var acc);
            if (acc is not null && Marshal.IsComObject(acc)) Marshal.ReleaseComObject(acc);
            return hr == 0;
        }
        catch { return false; }
    }
    internal static nint OwnerRoot(nint hwnd) => GetAncestor(hwnd, 3);
    internal static bool BelongsToWindow(nint hwnd, nint target)
    {
        for (var count = 0; hwnd != 0 && count < 16; count++)
        {
            if (hwnd == target || GetAncestor(hwnd, 2) == target) return true;
            hwnd = GetWindow(hwnd, 4);
        }
        return false;
    }
    internal static List<nint> OwnedWindows(nint target, uint processId)
    {
        var result = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != target && IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == processId && BelongsToWindow(hwnd, target)) result.Add(hwnd);
            }
            return true;
        }, 0);
        return result;
    }
}
