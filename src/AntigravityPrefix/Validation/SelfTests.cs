using System.Text;

namespace AntigravityPrefix;

internal static class SelfTests
{
    internal static string Run()
    {
        var report = new StringBuilder();
        var count = 0;
        void Equal(string expected, string actual)
        {
            if (actual != expected) throw new InvalidOperationException($"预期「{expected}」，实际「{actual}」");
            count++;
        }
        try
        {
            foreach (var first in PrefixRules.Icons)
            {
                Equal(first + " 材质整理", PrefixRules.Apply("材质整理", first));
                Equal(first + " 材质整理", PrefixRules.Apply(first + " 材质整理", first));
                Equal("材质整理", PrefixRules.Apply(first + " 材质整理", null));
                foreach (var second in PrefixRules.Icons)
                    Equal(second + " 材质整理", PrefixRules.Apply(first + " 材质整理", second));
            }
            Equal("原名", PrefixRules.Apply("原名", null));
            Equal("📄  正文  尾部 ", PrefixRules.Apply("💭  正文  尾部 ", "📄"));
            Equal("文中 💭 图标 ✌️", PrefixRules.Apply("文中 💭 图标 ✌️", null));
            Equal("○", PrefixRules.Apply("○", null));
            Equal("💭\u00a0正文", PrefixRules.Apply("💭\u00a0正文", null));
            Equal("✌ 正文", PrefixRules.Apply("✌ 正文", null));
            Equal("📄 💭正文", PrefixRules.Apply("💭正文", "📄"));
            foreach (var icon in PrefixRules.Icons) Equal(icon, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(icon)));
            void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); count++; }
            CompatibilityValidation.Run(Check);
            UpdateValidation.Run(Check);
            var binding = new EditorBinding((nint)101, 999, "原生框A", "输入框A", "保存A", "同名会话");
            Check(Recognition.IsSameBinding(binding, binding with { }), "相同绑定应通过");
            Check(!Recognition.IsSameBinding(binding, binding with { EditorId = "输入框B" }), "同名但不同输入框不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { ScopeId = "原生框B" }), "不同重命名框不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { Window = (nint)102 }), "同名但不同窗口不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { ProcessId = 998 }), "不同进程不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { SaveId = "保存B" }), "保存操作替换后不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { OriginalValue = "用户刚修改的内容" }), "内容变化后不能通过");
            Check(!Recognition.IsSameBinding(binding, binding with { InlineCommit = true }), "提交模式变化不能复用绑定");
            Check(Recognition.IsRenameLabel("重命名聊天"), "应识别中文语义");
            Check(Recognition.IsRenameLabel("Rename thread…"), "应识别英文省略号");
            foreach (var text in new[] { "名称", "搜索", "同名会话", "Conversation", "Conversation: sample-thread" }) Check(!Recognition.IsRenameLabel(text), "不能按输入值或播报推断重命名框");
            Check(Recognition.IsUnambiguousRenameScope(true, true, 1, 1, true), "唯一完整范围应通过");
            Check(!Recognition.IsUnambiguousRenameScope(false, true, 1, 1, true), "非对话框必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, false, 1, 1, true), "没有重命名语义必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 2, 1, true), "多个输入框必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 1, 2, true), "多个保存按钮必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 1, 0, true), "没有保存按钮必须拒绝");
            Check(!Recognition.IsUnambiguousRenameScope(true, true, 1, 1, false), "截断扫描必须拒绝");
            Check(!Recognition.IsCurrentMarker("class=data-selected current=false"), "不能将 CSS 或假值作为选中");
            Check(Recognition.IsCurrentMarker("readonly=true;current=page"), "应识别显式 Aria 状态，但它不证明业务身份");
            foreach (var text in new[] { "", "  ", "标题\n第二行", "标题\r第二行", new string('字', 2049) }) Check(!Recognition.IsValidTitle(text), "无效文本必须拒绝");
            Check(Recognition.IsValidTitle("💭 同名会话 ✌️"), "应保留完整字符");
            Check(Recognition.IsTrustedHostDocument(true, true), "主文档及应用结构证据同时存在才可通过");
            Check(!Recognition.IsTrustedHostDocument(false, true), "嵌套网页不能因仿造类名通过");
            Check(!Recognition.IsTrustedHostDocument(true, false), "只有文档标题不能证明应用上下文");
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                foreach (var icon in PrefixRules.Icons)
                {
                    var image = NativeEmoji.Render(icon, scale);
                    var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
                    image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                    Check(image.PixelWidth == (int)Math.Ceiling(28 * scale), "彩色字形应匹配显示缩放");
                    Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] > 0), "字形不能渲染为空");
                    Check(pixels[3] == 0, "字形边角应透明");
                    if (icon != "○") Check(Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4 + 3] > 100 && Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2])) - Math.Min(pixels[i * 4], Math.Min(pixels[i * 4 + 1], pixels[i * 4 + 2])) > 20), "应存在系统原生彩色字形像素");
                }
            var pin = new System.Windows.Controls.CheckBox { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.CheckBox)), IsChecked = true };
            pin.ApplyTemplate();
            var pinBorder = (System.Windows.Controls.Border)pin.Template.FindName("PinBox", pin);
            Check(pin.FocusVisualStyle is null, "置顶开关不应出现系统焦点装饰框");
            Check(pinBorder.BorderThickness == new System.Windows.Thickness(0), "置顶开关不应绘制外边框");
            Check(pin.Template.Triggers.OfType<System.Windows.Trigger>().Any(x => x.Property == System.Windows.UIElement.IsMouseOverProperty), "置顶开关应有与相邻按钮一致的 hover 背景");
            Check(pin.Template.Triggers.OfType<System.Windows.Trigger>().Any(x => x.Property == System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty), "置顶开关应有按压反馈");
            Check(Recognition.IsMenuLabel("更多操作") && Recognition.IsMenuLabel("More options"), "应识别明确的菜单入口");
            Check(!Recognition.IsMenuLabel("同名会话") && !Recognition.IsMenuLabel("搜索"), "不能把会话标题或搜索当作菜单入口");
            Check(OpenRenameBlockedException.Explain("MAIN_DOCUMENT_UNPROVEN").Contains("主界面文档"), "应区分主文档未确认错误");
            Check(OpenRenameBlockedException.Explain("SIDEBAR_TRUNCATED").Contains("侧边栏识别未完成"), "应明确显示真实截断原因而非只显示感叹号");
            Check(OpenRenameBlockedException.Explain("MENU_BUTTON_UNPROVEN").Contains("菜单操作"), "应区分条目扫描与菜单能力失败");
            Check(OpenRenameBlockedException.Explain("MENU_BUTTON_AMBIGUOUS").Contains("分屏"), "应提示分屏多菜单冲突");
            Check(OpenRenameBlockedException.Explain("未知错误").Contains("More actions") || OpenRenameBlockedException.Explain("未知错误").Contains("Rename"), "未知错误也应保留 Antigravity 手动指引");
            Check(AntigravityBridge.IsTransientOpenError(new System.Runtime.InteropServices.COMException("测试", unchecked((int)0x80040201))), "控件失效应允许只读重新发现");
            Check(!AntigravityBridge.IsTransientOpenError(new System.Runtime.InteropServices.COMException("测试", unchecked((int)0x80070005))), "权限拒绝不能当作控件重建重试");
            Check(!AntigravityBridge.IsTransientOpenError(new InvalidOperationException("身份变化")), "身份变化不能当作瞬态错误忽略");
            int transientCount = 0, readCount = 0;
            var recoveredCom = AntigravityBridge.PollTransient(
                () => ++readCount <= 2 ? throw new System.Runtime.InteropServices.COMException("瞬态", unchecked((int)0x80040201)) : "ok",
                v => v == "ok", 500, 0, CancellationToken.None, _ => ++transientCount);
            Check(recoveredCom == "ok" && readCount == 3 && transientCount == 2, "连续 0x80040201 瞬态失效后应恢复并返回匹配结果");
            transientCount = 0; readCount = 0;
            var recoveredEna = AntigravityBridge.PollTransient(
                () => ++readCount == 1 ? throw new FlaUI.Core.Exceptions.ElementNotAvailableException() : "ok",
                v => v == "ok", 500, 0, CancellationToken.None, _ => ++transientCount);
            Check(recoveredEna == "ok" && readCount == 2 && transientCount == 1, "ElementNotAvailableException 后应恢复并返回匹配结果");
            transientCount = 0; readCount = 0; bool propagatedAccess = false;
            try
            {
                AntigravityBridge.PollTransient<string>(() => { ++readCount; throw new System.Runtime.InteropServices.COMException("拒绝访问", unchecked((int)0x80070005)); },
                    _ => true, 500, 0, CancellationToken.None, _ => ++transientCount);
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070005)) { propagatedAccess = true; }
            Check(propagatedAccess && readCount == 1 && transientCount == 0, "0x80070005 权限异常必须首轮立即抛出");
            transientCount = 0; readCount = 0; bool propagatedInvalid = false;
            try
            {
                AntigravityBridge.PollTransient<string>(() => { ++readCount; throw new InvalidOperationException("非瞬态"); },
                    _ => true, 500, 0, CancellationToken.None, _ => ++transientCount);
            }
            catch (InvalidOperationException) { propagatedInvalid = true; }
            Check(propagatedInvalid && readCount == 1 && transientCount == 0, "InvalidOperationException 必须首轮立即抛出");
            var timedOut = AntigravityBridge.PollTransient(() => "stale", v => v == "new", 30, 5, CancellationToken.None);
            Check(timedOut is null, "超时未满足匹配条件应返回 null");
            using var canceledCts = new CancellationTokenSource();
            canceledCts.Cancel();
            readCount = 0; bool canceled = false;
            try { AntigravityBridge.PollTransient(() => { ++readCount; return "ok"; }, _ => true, 500, 0, canceledCts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && readCount == 0, "已取消令牌必须在读取前立即抛出取消异常");
            Check(System.Runtime.InteropServices.Marshal.SizeOf<RenameKeys.Input>() == 40, "Windows x64 INPUT 布局必须正确");
            Check(string.Join(",", RenameKeys.EnterSequence().Select(i => i.Data.Keyboard.Key)) == "13,13", "提交只发送 Enter 并立即释放");
            Check(RenameKeys.EnterSequence()[0].Data.Keyboard.Flags == 0 && RenameKeys.EnterSequence()[1].Data.Keyboard.Flags == 2, "Enter 提交不能遗漏松开事件");
            Check(!pin.Template.Triggers.OfType<System.Windows.Trigger>().Any(x => x.Property == System.Windows.UIElement.IsKeyboardFocusedProperty), "置顶模板不应按焦点绘制选中框");
            CompactLayoutValidation.Run(Check);
            EmojiValidation.Rules(Check);
            FeedbackValidation.Rules(Check);
            MotionValidation.Rules(Check);
            MotionSamplingValidation.Rules(Check);
            MotionValidation.Offscreen(Check);
            SharedLayoutValidation.Run(Check);
            CornerValidation.Run(Check);
            IconValidation.Run(Check);
            Check(WindowIdentity.IsAntigravityProcess("Chrome_WidgetWin_1", "Antigravity.exe", "Antigravity", "Google", true), "应识别官方 Antigravity 桌面进程");
            Check(!WindowIdentity.IsAntigravityProcess("Chrome_WidgetWin_1", "chrome.exe", "Google Chrome", "Google", true), "不能把浏览器进程当作 Antigravity");
            Check(!WindowIdentity.IsAntigravityProcess("CabinetWClass", "explorer.exe", "Windows Explorer", "Microsoft", true), "不能把文件资源管理器当作 Antigravity");
            Check(!WindowIdentity.IsAntigravityProcess("Chrome_WidgetWin_1", "Antigravity.exe", "OtherApp", "Google", true), "产品名不符必须拒绝");
            Check(!WindowIdentity.IsAntigravityProcess("Chrome_WidgetWin_1", "Antigravity.exe", "Antigravity", "ThirdParty", true), "厂商标识不符必须拒绝");
            Check(!WindowIdentity.IsAntigravityProcess("Chrome_WidgetWin_1", "Antigravity.exe", "Antigravity", "Google", false), "缺少 app.asar 标识必须拒绝");
            Check(!WindowIdentity.IsAntigravityProcess("ConsoleWindowClass", "Antigravity.exe", "Antigravity", "Google", true), "控制台窗口类必须拒绝");
            Check(MainWindow.ShouldSuppressFullActivation(false, false), "完整模式非编辑态应保持不激活，防止内联输入框失焦");
            Check(!MainWindow.ShouldSuppressFullActivation(false, true), "emoji 列表编辑态应允许正常激活");
            Check(!MainWindow.ShouldSuppressFullActivation(true, false), "小条模式由 WindowTracker 单独接管不激活");
            Check(!MainWindow.ShouldSuppressFullActivation(true, true), "小条模式不走完整模式激活分支");
            foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 3 })
                foreach (int origin in new[] { -2560, 0, 2560 })
                {
                    var frame = new TrackingNative.Rect(origin + 100, 50, origin + 1500, 950);
                    var work = new TrackingNative.Rect(origin, 0, origin + 1920, 1080);
                    var placed = WindowTracker.Place(frame, work, scale, 304, 40);
                    Check(Math.Abs(placed.Left + placed.Right - frame.Left - frame.Right) <= 1, "小条应按物理像素居中，兼容负屏幕坐标");
                    Check(placed.Right - placed.Left == (int)Math.Round(304 * scale), "小条宽度应按宿主 DPI 换算");
                    Check(placed.Top >= work.Top && placed.Bottom <= work.Bottom, "小条顶边应保留在工作区内");
                }
            var tiny = WindowTracker.Place(new(-2000, -8, -1850, 900), new(-1920, 0, 0, 1040), 1.5, 304, 40);
            Check(tiny.Left >= -1920 && tiny.Top >= 0 && tiny.Right <= 0, "极窄和最大化边界不得把小条推到屏幕之外");
            Check(WindowTracker.PlaceAboveFlags == 0x0213u, "PlaceAbove 必须包含 SWP_NOOWNERZORDER(0x0200)，防止重排小条扰动宿主层级");
            const nint simBar = 1001, simTopPrev = 2001, simNormalPrev = 2002;
            Check(WindowTracker.ResolveInsertAfter(simBar, simTopPrev, 0, false, false, true, false) == WindowTracker.HwndTop, "非置顶且非置顶宿主前邻为置顶窗时，应回到 HWND_TOP（非置顶顶端），不能使用 HWND_NOTOPMOST 无效空操作");
            Check(WindowTracker.ResolveInsertAfter(simBar, 0, 0, false, false, false, false) == WindowTracker.HwndTop, "非置顶且非置顶宿主位于 Z 序最前(before=0)时，应使用 HWND_TOP 紧贴其上");
            Check(WindowTracker.ResolveInsertAfter(simBar, simTopPrev, 0, false, true, true, false) == WindowTracker.HwndNoTopmost, "小条误入置顶带且非置顶宿主前邻为置顶窗时，必须返回 HWND_NOTOPMOST");
            Check(WindowTracker.ResolveInsertAfter(simBar, simNormalPrev, 0, false, false, false, false) == simNormalPrev, "非置顶宿主前邻为普通窗口时，应插在其后");
            Check(WindowTracker.ResolveInsertAfter(simBar, simBar, simNormalPrev, false, false, false, false) is null, "小条已紧贴非置顶宿主且均非置顶时，应跳过多余重排");
            Check(WindowTracker.ResolveInsertAfter(simBar, simBar, simTopPrev, false, true, false, true) == WindowTracker.HwndNoTopmost, "小条误入置顶带且前邻置顶时，即使紧邻宿主也不得跳过，必须降回 HWND_NOTOPMOST");
            Check(WindowTracker.ResolveInsertAfter(simBar, simBar, simNormalPrev, false, true, false, false) == simNormalPrev, "小条误入置顶带且前邻为普通窗口时，应降回该普通窗口之后");
            Check(WindowTracker.ResolveInsertAfter(simBar, simBar, simTopPrev, true, true, true, true) is null, "置顶宿主与置顶小条相邻时应跳过重排");
            Check(WindowTracker.ResolveInsertAfter(simBar, 0, 0, true, false, false, false) == WindowTracker.HwndTopmost, "置顶宿主前邻为空时小条使用 HWND_TOPMOST");
            Check(WindowTracker.ResolveInsertAfter(simBar, simTopPrev, 0, true, false, true, false) == simTopPrev, "置顶宿主有前邻时小条插入该前邻之后");
            var app = System.Windows.Application.Current;
            var originalShutdownMode = app.ShutdownMode;
            app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            System.Windows.Window? topHi = null, topLo = null, hostWin = null, otherWin = null, barWin = null;
            nint hBar = 0;
            try
            {
                static int Rank(nint hwnd)
                {
                    int rank = 0, found = -1;
                    TrackingNative.EnumWindows((h, _) => { if (h == hwnd) { found = rank; return false; } rank++; return true; }, 0);
                    return found;
                }
                System.Windows.Window OffscreenWin(bool topmost) => new()
                {
                    Left = -32000,
                    Top = -32000,
                    Width = 2,
                    Height = 2,
                    WindowStyle = System.Windows.WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    Opacity = 0,
                    Topmost = topmost
                };
                topHi = OffscreenWin(true);
                topLo = OffscreenWin(true);
                hostWin = OffscreenWin(false);
                otherWin = OffscreenWin(false);
                barWin = OffscreenWin(false);
                topHi.Show(); topLo.Show(); otherWin.Show(); hostWin.Show(); barWin.Show();
                var hTopHi = new System.Windows.Interop.WindowInteropHelper(topHi).Handle;
                var hTopLo = new System.Windows.Interop.WindowInteropHelper(topLo).Handle;
                var hHost = new System.Windows.Interop.WindowInteropHelper(hostWin).Handle;
                var hOther = new System.Windows.Interop.WindowInteropHelper(otherWin).Handle;
                hBar = new System.Windows.Interop.WindowInteropHelper(barWin).Handle;
                TrackingNative.SetWindowPos(hTopHi, WindowTracker.HwndTopmost, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hTopLo, hTopHi, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowLongPtrW(hBar, -20, (nint)((TrackingNative.GetWindowLongPtrW(hBar, -20).ToInt64() & ~0x00040000L) | 0x08000080L));
                TrackingNative.SetWindowLongPtrW(hBar, -8, 0);

                // 场景 1：bar 落在 other 之后且 host 位于非置顶最前（其上为多层置顶窗），PlaceAbove 必须将无 owner 的 bar 提到 hHost 之上、hTopLo 之下。
                TrackingNative.SetWindowPos(hOther, WindowTracker.HwndTop, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hBar, hOther, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hHost, WindowTracker.HwndTop, 0, 0, 0, 0, 0x13);
                WindowTracker.PlaceAbove(hBar, hHost);
                Check(!WindowTracker.IsTopmost(hBar) && !WindowTracker.IsTopmost(hHost) && TrackingNative.GetWindow(hBar, 4) == 0
                    && Rank(hTopLo) < Rank(hBar) && Rank(hBar) < Rank(hHost) && Rank(hHost) < Rank(hOther),
                    "无 owner 场景1：PlaceAbove 必须将非置顶小条稳定插入宿主紧上方且保持无置顶、无 owner");

                // 场景 2：第三方窗口 other 升至宿主上方，若 bar 原本位于 other 之上也必须回插到 other 之后、host 之上。
                TrackingNative.SetWindowPos(hBar, WindowTracker.HwndTop, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hOther, hBar, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hHost, hOther, 0, 0, 0, 0, 0x13);
                WindowTracker.PlaceAbove(hBar, hHost);
                Check(!WindowTracker.IsTopmost(hBar) && !WindowTracker.IsTopmost(hHost) && Rank(hOther) < Rank(hBar) && Rank(hBar) < Rank(hHost),
                    "无 owner 场景2：other 升至宿主上方后，PlaceAbove 将 bar 维持在 other 之后、host 之上且均非置顶");

                // 场景 3：host 位于 other 之后，破坏前置条件令 bar 误带 WS_EX_TOPMOST -> 必须降回非置顶层并落在 other 之后、host 之上。
                TrackingNative.SetWindowPos(hOther, WindowTracker.HwndTop, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hHost, hOther, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hBar, WindowTracker.HwndTopmost, 0, 0, 0, 0, 0x13);
                Check(WindowTracker.IsTopmost(hBar), "破坏前置条件：先将小条强制置顶以验证被遮挡宿主下的自愈降层");
                WindowTracker.PlaceAbove(hBar, hHost);
                Check(!WindowTracker.IsTopmost(hBar) && !WindowTracker.IsTopmost(hHost) && Rank(hOther) < Rank(hBar) && Rank(hBar) < Rank(hHost),
                    "无 owner 场景3：host 在 other 之后时，误置顶小条必须自愈降回 other 之后、host 之上且不污染宿主");

                // 场景 4：host 位于非置顶最前，bar 误带 WS_EX_TOPMOST -> 必须降回 hTopLo 之下、hHost 之上。
                TrackingNative.SetWindowPos(hHost, WindowTracker.HwndTop, 0, 0, 0, 0, 0x13);
                TrackingNative.SetWindowPos(hBar, WindowTracker.HwndTopmost, 0, 0, 0, 0, 0x13);
                Check(WindowTracker.IsTopmost(hBar), "破坏前置条件：先将小条强制置顶以验证非置顶最前宿主下的自愈降层");
                WindowTracker.PlaceAbove(hBar, hHost);
                Check(!WindowTracker.IsTopmost(hBar) && !WindowTracker.IsTopmost(hHost) && Rank(hTopLo) < Rank(hBar) && Rank(hBar) < Rank(hHost),
                    "无 owner 场景4：host 在非置顶最前时，误置顶小条必须自愈降回 hTopLo 之下、hHost 之上且不污染宿主");
            }
            finally
            {
                if (hBar != 0 && Native.IsWindow(hBar)) TrackingNative.SetWindowLongPtrW(hBar, -8, 0);
                barWin?.Close(); otherWin?.Close(); hostWin?.Close(); topLo?.Close(); topHi?.Close();
                app.ShutdownMode = originalShutdownMode;
            }
            report.AppendLine($"通过：{count} 项规则、身份隔离、UTF-8、彩色字形、置顶样式及 Windows 图标测试。");
            report.AppendLine("真实 Antigravity 重命名框：未直接操作。独立桌面端到端结果另见验收记录。");
        }
        catch (Exception ex) { report.AppendLine("失败：" + ex.Message); }
        return report.ToString();
    }
}
