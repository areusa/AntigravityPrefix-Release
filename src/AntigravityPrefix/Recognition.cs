namespace AntigravityPrefix;

public enum StatusKind { Processing, NeedsAction, Ready, Completed, Submitted, Error }
public sealed record WorkStatus(StatusKind Kind, string Text, string? Detail = null);
public sealed record EditorBinding(nint Window, int ProcessId, string ScopeId, string EditorId, string SaveId, string OriginalValue, bool InlineCommit = false)
{
    public string Key => $"{Window}/{ProcessId}/{ScopeId}/{EditorId}/{SaveId}";
}
public sealed record ProbeResult(EditorBinding? Binding, string? ReferenceTitle, WorkStatus Status);
public sealed record RenameResult(string Name, WorkStatus Status);

public static class Recognition
{
    public static bool IsSameBinding(EditorBinding expected, EditorBinding actual) =>
        expected.Window == actual.Window && expected.ProcessId == actual.ProcessId &&
        expected.ScopeId == actual.ScopeId && expected.EditorId == actual.EditorId &&
        expected.SaveId == actual.SaveId && expected.OriginalValue == actual.OriginalValue &&
        expected.InlineCommit == actual.InlineCommit;
    public static bool IsRenameLabel(string? label) => RenameLabels.Contains((label ?? "").Trim().TrimEnd('.', '…'), StringComparer.OrdinalIgnoreCase);
    public static bool IsSaveLabel(string? label) => SaveLabels.Contains((label ?? "").Trim().TrimEnd('.', '…'), StringComparer.OrdinalIgnoreCase);
    public static bool IsMenuLabel(string? label) => new[] { "More", "More options", "More actions", "Thread actions", "Conversation options", "Open menu", "更多", "更多选项", "更多操作", "会话操作", "聊天操作", "打开菜单", "打开会话菜单", "⋯", "..." }.Contains((label ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
    public static bool IsValidTitle(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 && !value.Contains('\r') && !value.Contains('\n');
    public static bool IsUnambiguousRenameScope(bool dialog, bool renameMeaning, int edits, int saves, bool complete) =>
        dialog && renameMeaning && edits == 1 && saves == 1 && complete;
    public static bool IsTrustedHostDocument(bool sameDocument, bool shellMarker) => sameDocument && shellMarker;
    public static bool HasCssToken(string? className, string token) =>
        !string.IsNullOrEmpty(token) && (className ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.Ordinal);
    public static bool IsMultilineAria(string? ariaProperties) =>
        System.Text.RegularExpressions.Regex.IsMatch(ariaProperties ?? "", @"(?:^|[;,\s])multiline\s*[=:]\s*true(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public static bool HasMenuPopupAria(string? ariaProperties) =>
        System.Text.RegularExpressions.Regex.IsMatch(ariaProperties ?? "", @"(?:^|[;,\s])haspopup\s*[=:]\s*menu(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static bool IsAntigravityMainShell(bool verifiedWindow, bool ownedNativeRoot, bool hasStandaloneTheme, bool hasRootContainer, bool hasShellLandmark) =>
        verifiedWindow && ownedNativeRoot && hasStandaloneTheme && hasRootContainer && hasShellLandmark;
    internal static bool IsTrustedAntigravityInlineEditor(bool sameMainDocument, bool verifiedWindow, bool ownedNativeRoot,
        bool inTrustedRenameZone, bool inExcludedZone, string? editorName, bool writableValue, bool multiline, bool isPassword) =>
        sameMainDocument && verifiedWindow && ownedNativeRoot && inTrustedRenameZone && !inExcludedZone
        && IsRenameLabel(editorName) && writableValue && !multiline && !isPassword;
    internal static bool IsConversationMoreActions(string? name, string? ariaProperties, bool hasExpandOrInvoke, bool inExcludedZone) =>
        !inExcludedZone && hasExpandOrInvoke && HasMenuPopupAria(ariaProperties)
        && MoreActionsLabels.Contains((name ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
    internal static bool IsRenameMenuItem(string? name) =>
        RenameMenuLabels.Contains((name ?? "").Trim().TrimEnd('.', '…'), StringComparer.OrdinalIgnoreCase);
    internal static bool IsSameConversationHeaderBand(double bcLeft, double bcTop, double bcHeight, double btnLeft, double btnTop, double btnHeight, bool missingBounds)
    {
        if (missingBounds || bcHeight <= 0 || btnHeight <= 0) return true;
        double tolerance = Math.Max(16, Math.Max(bcHeight, btnHeight) * 0.6);
        return btnLeft >= bcLeft && Math.Abs(btnTop - bcTop) <= tolerance;
    }
    internal static bool IsSidePaneHeaderBoundary(bool isContainer, string? name)
    {
        if (isContainer) return true;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;
        return SidePaneBoundaryLabels.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            || trimmed.StartsWith("Close ", StringComparison.OrdinalIgnoreCase);
    }
    internal enum InlineCommitPlan { DirectForegroundEnter, AlreadyClosedOnBlur, BackgroundThenBorrowFocus }
    internal static bool IsConversationTitleButton(string? name, bool hasInvoke, bool inBreadcrumb, bool inExcludedZone)
    {
        if (!inBreadcrumb || inExcludedZone || !hasInvoke) return false;
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;
        return trimmed.StartsWith("Conversation:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("会话:", StringComparison.Ordinal)
            || trimmed.StartsWith("会话：", StringComparison.Ordinal);
    }
    internal static InlineCommitPlan PlanInlineCommit(bool hostIsForeground, bool editorAlreadyClosed) =>
        editorAlreadyClosed ? InlineCommitPlan.AlreadyClosedOnBlur
        : hostIsForeground ? InlineCommitPlan.DirectForegroundEnter
        : InlineCommitPlan.BackgroundThenBorrowFocus;
    public static bool IsCurrentMarker(string? aria) => System.Text.RegularExpressions.Regex.IsMatch(aria ?? "", @"(?:^|[;,\s])(?:selected\s*[=:]\s*true|current\s*[=:]\s*(?:true|page))(?:$|[;,\s])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // 不能使用泛化的“名称”或输入框文本推测重命名语义。
    static readonly string[] RenameLabels = ["Rename", "Rename chat", "Rename thread", "Rename conversation", "Edit title", "重命名", "重命名会话", "重命名聊天", "为聊天重命名", "修改聊天名称", "编辑标题"];
    static readonly string[] SaveLabels = ["Save", "Save changes", "Confirm", "Rename", "Done", "保存", "保存更改", "确定", "确认", "重命名", "完成"];
    static readonly string[] MoreActionsLabels = ["More actions", "更多操作"];
    static readonly string[] RenameMenuLabels = ["Rename", "Rename conversation", "Rename chat", "Rename thread", "重命名", "重命名会话", "重命名聊天"];
    static readonly string[] SidePaneBoundaryLabels = ["New tab", "Maximize Pane", "Restore Pane", "Toggle Side Panel", "新建标签页", "最大化窗格", "切换侧边栏"];
}
