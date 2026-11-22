namespace AntigravityPrefix;

// 仅重放结构证据，不创建窗口、不连接 UIA，也不读写真实会话。
internal static class CompatibilityValidation
{
    internal static void Run(Action<bool, string> check)
    {
        bool Shell(bool verified = true, bool owned = true, bool theme = true, bool root = true, bool landmark = true) =>
            Recognition.IsAntigravityMainShell(verified, owned, theme, root, landmark);

        check(Shell(), "完整主文档壳证据应通过识别");
        check(!Shell(verified: false), "非官方 Antigravity 进程窗口必须拒绝");
        check(!Shell(owned: false), "非宿主所属原生句柄不能识别为主文档");
        check(!Shell(theme: false), "缺少 theme-standalone（如内置网页 Document）必须拒绝");
        check(!Shell(root: false), "缺少 id='root' 主容器（如空白 Document）必须拒绝");
        check(!Shell(landmark: false), "缺少 Sidebar/Breadcrumb/agy-button 标志必须拒绝");

        check(Recognition.HasCssToken(" z-50\ttheme-standalone\ncompact ", "theme-standalone"), "CSS whitespace 分隔不应破坏 token 识别");
        foreach (var css in new string?[] { null, "", "theme-standalone-fake", "prefix-theme-standalone", "Theme-Standalone" })
            check(!Recognition.HasCssToken(css, "theme-standalone"), "CSS token 必须是完整且区分大小写");

        check(Recognition.IsMultilineAria("multiline=true"), "Aria multiline=true 应识别");
        check(Recognition.IsMultilineAria("readonly=false;MULTILINE:TRUE"), "Aria 大写与冒号分隔 multiline 应识别");
        foreach (var aria in new string?[] { null, "", "multiline=false", "notmultiline=true" })
            check(!Recognition.IsMultilineAria(aria), "单行或无关属性不能误判为多行");

        bool Inline(bool sameDoc = true, bool verified = true, bool owned = true, bool trustedZone = true,
            bool excludedZone = false, string? name = "Edit title", bool writable = true, bool multiline = false, bool password = false) =>
            Recognition.IsTrustedAntigravityInlineEditor(sameDoc, verified, owned, trustedZone, excludedZone, name, writable, multiline, password);

        check(Inline(), "Sidebar 内联 Edit title 单行可写输入框应通过");
        check(Inline(name: "编辑标题"), "中文内联编辑标题应通过");
        check(!Inline(sameDoc: false), "内置网页 Document 中的同名输入框必须拒绝");
        check(!Inline(verified: false), "未通过进程身份核验的窗口内联输入框必须拒绝");
        check(!Inline(owned: false), "非本窗口拥有的 Document 内联输入框必须拒绝");
        check(!Inline(trustedZone: false), "聊天正文或普通区域中的输入框（如对话输入栏）必须拒绝");
        check(!Inline(excludedZone: true), "Side Panel / Directory Viewer 等排除区域必须拒绝");
        foreach (var badName in new string?[] { null, "", "searchInput", "noteText", "搜索", "名称", "Chat input" })
            check(!Inline(name: badName), "非重命名语义输入框不能接管");
        check(!Inline(writable: false), "只读输入框不能绑定");
        check(!Inline(multiline: true), "多行输入框（如提示词输入栏）必须拒绝");
        check(!Inline(password: true), "密码框必须拒绝");

        bool MoreBtn(string? name = "More actions", string? aria = "readonly=true;expanded=false;haspopup=menu", bool pat = true, bool excluded = false) =>
            Recognition.IsConversationMoreActions(name, aria, pat, excluded);

        check(MoreBtn(), "会话顶部右上角 More actions 按钮应通过");
        check(MoreBtn(name: "更多操作"), "中文更多操作按钮应通过");
        foreach (var badBtn in new string?[] { null, "", "More options", "Project options", "Toggle Overview Panel" })
            check(!MoreBtn(name: badBtn), "侧栏条目 More options 等其他菜单按钮不能混用");
        check(!MoreBtn(aria: "readonly=true;expanded=false"), "不带 haspopup=menu 的普通按钮不能误作菜单入口");
        check(!MoreBtn(pat: false), "不支持 Expand 或 Invoke 的节点必须拒绝");
        check(!MoreBtn(excluded: true), "Side Panel / Directory Viewer header 的 More actions 必须拒绝");

        check(Recognition.IsRenameMenuItem("Rename") && Recognition.IsRenameMenuItem("Rename…") && Recognition.IsRenameMenuItem("Rename conversation") && Recognition.IsRenameMenuItem("重命名"), "应识别原生菜单中的 Rename 项");
        foreach (var badMenu in new string?[] { null, "", "Unpin", "Archive", "Split", "Copy", "Edit title" })
            check(!Recognition.IsRenameMenuItem(badMenu), "无关菜单项不能误触发");

        check(Recognition.IsSameConversationHeaderBand(397, 61, 43, 1833, 61, 43, false), "会话同水平顶栏的 More actions 应匹配");
        check(!Recognition.IsSameConversationHeaderBand(397, 61, 43, 3678, 121, 42, false), "右侧页签头部的 More actions 必须被过滤");
        check(!Recognition.IsSameConversationHeaderBand(397, 61, 43, 100, 61, 43, false), "Breadcrumb 左侧控件不应匹配为会话 More actions");
        check(Recognition.IsSameConversationHeaderBand(397, 61, 43, 0, 0, 0, true), "缺失坐标时允许依赖树相邻顺序判定");

        check(Recognition.IsSidePaneHeaderBoundary(true, null), "侧边容器本身应触发右面板边界停止");
        foreach (var boundary in new[] { "New tab", "Maximize Pane", "Toggle Side Panel", "Close Directory", "Close Walkthrough" })
            check(Recognition.IsSidePaneHeaderBoundary(false, boundary), "右面板标签栏控件应阻断后续扫描");
        foreach (var nonBoundary in new string?[] { "More actions", "Toggle Overview Panel", "", null })
            check(!Recognition.IsSidePaneHeaderBoundary(false, nonBoundary), "会话顶栏自身按钮不应被误判为侧边页签边界");

        // 保留通用非空单范围校验（覆盖独立验收窗口对话框分支）。
        check(Recognition.IsUnambiguousRenameScope(true, Recognition.IsRenameLabel("重命名聊天"), 1, 1, true), "独立对话框范围校验应保持兼容");
        check(!Recognition.IsUnambiguousRenameScope(true, Recognition.IsRenameLabel("Chat title"), 1, 1, true), "普通标签不能替代重命名语义");
        var inlineExpected = new EditorBinding((nint)41, 42, "inline-node-1", "inline-node-1", "inline:enter", "中文 ✌️ 空格", InlineCommit: true);
        check(!Recognition.IsSameBinding(inlineExpected, inlineExpected with { ScopeId = "inline-node-2" }), "重命名输入框重建后不能复用原绑定");
        check(!Recognition.IsSameBinding(inlineExpected, inlineExpected with { OriginalValue = "用户已修改" }), "原始内容变化后必须拒绝写入");
        check(!Recognition.IsSameBinding(inlineExpected, inlineExpected with { InlineCommit = false }), "提交机制不同步时不能复用原绑定");
    }
}
