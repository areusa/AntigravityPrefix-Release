using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AntigravityPrefix;

public partial class MainWindow
{
    enum UpdateUiState { Idle, Checking, UpToDate, UpdateAvailable, Downloading, Applying, Error }

    UpdateService? updateService;
    UpdateUiState updateUiState = UpdateUiState.Idle;
    UpdateCheckResult? availableUpdate;
    CancellationTokenSource? updateCheckCts;
    CancellationTokenSource? updateDownloadCts;
    string updateStateMessage = "";
    string? updateStateDetail;

    internal bool IsUpdateBusy => updateUiState is UpdateUiState.Downloading or UpdateUiState.Applying;
    UpdateService EnsureUpdateService() => updateService ??= new UpdateService();
    string UpdatesRootFolder() => Path.Combine(dataFolder, "updates");

    void InitializeUpdater()
    {
        var currentVer = EnsureUpdateService().CurrentVersion.ToDisplayString();
        UpdateVersionText.Text = currentVer;

        if (demoHandle == 0 && TestDataRoot is null)
        {
            var cleanupDir = startupUpdateContext?.CleanupDir;
            var updatesRoot = UpdatesRootFolder();
            _ = Task.Run(() => UpdateApplier.CleanupAfterUpdateAsync(AppContext.BaseDirectory, cleanupDir, updatesRoot));
        }

        if (startupUpdateContext?.UpdatedFrom is { } fromVer)
        {
            string detail = string.IsNullOrWhiteSpace(fromVer) ? $"已完成自更新：{currentVer}" : $"已从 {fromVer} 更新至 {currentVer}";
            ShowStatus(new(StatusKind.Completed, $"已更新至 {currentVer}", detail));
            messageUntil = DateTime.UtcNow.AddSeconds(6);
            updateUiState = UpdateUiState.UpToDate;
            updateStateMessage = "已是最新";
            updateStateDetail = detail;
        }
        else if (startupUpdateContext?.UpdateFailed is { } failReason)
        {
            ShowStatus(new(StatusKind.Error, "更新失败，已回滚原版本", failReason));
            messageUntil = DateTime.UtcNow.AddSeconds(8);
            updateUiState = UpdateUiState.Error;
            updateStateMessage = "更新已回滚";
            updateStateDetail = failReason;
        }

        RefreshUpdateBarUi();
        if (demoHandle == 0 && TestDataRoot is null && autoCheckUpdate)
            _ = RunStartupSilentUpdateCheckAsync();
    }

    void DisposeUpdater()
    {
        try { updateCheckCts?.Cancel(); } catch { }
        try { updateDownloadCts?.Cancel(); } catch { }
        updateCheckCts?.Dispose(); updateCheckCts = null;
        updateDownloadCts?.Dispose(); updateDownloadCts = null;
        updateService?.Dispose(); updateService = null;
    }

    void ReportUpdateIssue(string message, string? detail = null)
    {
        if (EditingEmoji)
        {
            EditorStatus(message, true);
        }
        else
        {
            ShowStatus(new(StatusKind.Error, message, detail ?? message));
            messageUntil = DateTime.UtcNow.AddSeconds(6);
        }
    }

    async Task RunStartupSilentUpdateCheckAsync()
    {
        try { await Task.Delay(1500, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (closed || IsUpdateBusy || updateUiState == UpdateUiState.Checking) return;
        await CheckForUpdateInternalAsync(userInitiated: false);
    }

    async void ClickUpdateAction(object sender, RoutedEventArgs e)
    {
        if (closed) return;
        if (updateUiState == UpdateUiState.Downloading)
        {
            updateDownloadCts?.Cancel();
            return;
        }
        if (updateUiState is UpdateUiState.Applying or UpdateUiState.Checking) return;
        if (operation || panels.Relocating || modeTransition is not null) return;
        if (EditingEmoji && (emojiPhase != EmojiEditPhase.Editing || IconPanel.Dragging)) return;

        if (availableUpdate is { HasUpdate: true })
        {
            await DownloadAndApplyUpdateAsync();
            return;
        }
        await CheckForUpdateInternalAsync(userInitiated: true);
    }

    async Task CheckForUpdateInternalAsync(bool userInitiated)
    {
        if (closed || IsUpdateBusy || updateUiState == UpdateUiState.Checking) return;
        updateCheckCts?.Cancel();
        updateCheckCts?.Dispose();
        updateCheckCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = updateCheckCts.Token;
        var previousState = updateUiState;
        var previousMessage = updateStateMessage;
        var previousDetail = updateStateDetail;

        updateUiState = UpdateUiState.Checking;
        updateStateMessage = "检查中…";
        updateStateDetail = null;
        RefreshUpdateBarUi();

        try
        {
            var result = await EnsureUpdateService().CheckForUpdateAsync(bypassCache: userInitiated, token);
            if (closed) return;
            if (result.HasUpdate)
            {
                availableUpdate = result;
                updateUiState = UpdateUiState.UpdateAvailable;
                updateStateMessage = $"发现 {result.Remote.ToDisplayString()}";
                updateStateDetail = $"点击右侧图标更新到 {result.Remote.ToDisplayString()}（SHA-256 校验后原位替换）";
                log.Write("更新检查", $"发现新版本：当前={result.Current}；远端={result.Remote}");
            }
            else
            {
                availableUpdate = null;
                updateUiState = UpdateUiState.UpToDate;
                updateStateMessage = "已是最新";
                updateStateDetail = $"远端版本 {result.Remote.ToDisplayString()}";
            }
        }
        catch (OperationCanceledException)
        {
            if (closed) return;
            updateUiState = previousState == UpdateUiState.Checking ? UpdateUiState.Idle : previousState;
            updateStateMessage = previousState == UpdateUiState.Checking ? "" : previousMessage;
            updateStateDetail = previousDetail;
        }
        catch (Exception ex)
        {
            if (closed) return;
            log.Write("更新检查", $"失败（手动={userInitiated}）：{ex.Message}");
            if (!userInitiated)
            {
                updateUiState = availableUpdate is { HasUpdate: true } ? UpdateUiState.UpdateAvailable : previousState;
                updateStateMessage = availableUpdate is { HasUpdate: true } ? $"发现 {availableUpdate.Remote.ToDisplayString()}" : previousMessage;
                updateStateDetail = previousDetail;
            }
            else
            {
                updateUiState = UpdateUiState.Error;
                updateStateMessage = "检查失败";
                updateStateDetail = ex.Message;
            }
        }
        finally
        {
            if (!closed) RefreshUpdateBarUi();
        }
    }

    async Task DownloadAndApplyUpdateAsync()
    {
        if (closed || IsUpdateBusy || availableUpdate is not { HasUpdate: true } checkResult) return;
        if (EditingEmoji && HasUnsavedEmojiDraft())
        {
            EditorStatus("请先保存（✓）或取消（✕）当前列表草稿，再执行更新。", true);
            return;
        }
        if (demoHandle != 0 || TestDataRoot is not null)
        {
            ReportUpdateIssue("测试夹具模式不执行原位更新替换。");
            return;
        }
        if (!UpdateService.CanWriteDirectory(AppContext.BaseDirectory))
        {
            updateUiState = UpdateUiState.Error;
            updateStateMessage = "目录只读";
            updateStateDetail = "当前程序所在目录没有写入权限，无法原位替换更新。";
            ReportUpdateIssue("当前程序所在目录只读，无法原位替换更新。", updateStateDetail);
            RefreshUpdateBarUi();
            return;
        }

        try { updateCheckCts?.Cancel(); } catch { }
        updateDownloadCts?.Dispose();
        updateDownloadCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = updateDownloadCts.Token;

        updateUiState = UpdateUiState.Downloading;
        updateStateMessage = "准备下载 0%";
        updateStateDetail = null;
        RefreshUpdateBarUi();
        EnableActions(current is not null);

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            if (closed || updateUiState != UpdateUiState.Downloading) return;
            updateStateMessage = $"下载中 {p.Percent}%";
            updateStateDetail = $"{p.CurrentFile} ({p.FileIndex}/{p.FileCount})";
            RefreshUpdateBarUi();
        });

        try
        {
            var staged = await EnsureUpdateService().DownloadAndVerifyAsync(
                checkResult.Manifest,
                AppContext.BaseDirectory,
                UpdatesRootFolder(),
                progress,
                token);

            if (closed)
            {
                UpdateService.TryDeleteDirectory(staged.StageDir);
                return;
            }

            updateUiState = UpdateUiState.Applying;
            updateStateMessage = "正在重启更新…";
            updateStateDetail = null;
            RefreshUpdateBarUi();
            EnableActions(current is not null);

            log.Write("执行更新", $"开始原位替换：{checkResult.Current.ToDisplayString()} -> {checkResult.Remote.ToDisplayString()}");
            UpdateApplier.LaunchHelper(staged, AppContext.BaseDirectory, checkResult.Current.ToDisplayString());
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                updateUiState = UpdateUiState.UpdateAvailable;
                updateStateMessage = $"发现 {checkResult.Remote.ToDisplayString()}";
                updateStateDetail = null;
                log.Write("执行更新", "已取消下载");
            }
        }
        catch (Exception ex)
        {
            if (!closed)
            {
                updateUiState = UpdateUiState.Error;
                updateStateMessage = "更新失败";
                updateStateDetail = ex.Message;
                ReportUpdateIssue("更新失败：" + ex.Message, ex.Message);
                log.Write("执行更新", "失败：" + ex.Message);
            }
        }
        finally
        {
            updateDownloadCts?.Dispose();
            updateDownloadCts = null;
            if (!closed)
            {
                RefreshUpdateBarUi();
                EnableActions(current is not null);
            }
        }
    }

    void RefreshUpdateBarUi()
    {
        if (closed) return;
        UpdateVersionText.Text = EnsureUpdateService().CurrentVersion.ToDisplayString();

        bool showState = updateUiState != UpdateUiState.Idle && !string.IsNullOrEmpty(updateStateMessage);
        if (showState)
        {
            bool wasCollapsed = UpdateStateText.Visibility != Visibility.Visible;
            UpdateStateText.Text = "· " + updateStateMessage;
            UpdateStateText.ToolTip = updateStateDetail ?? updateStateMessage;
            UpdateStateText.Foreground = new SolidColorBrush(updateUiState switch
            {
                UpdateUiState.UpdateAvailable => Color.FromRgb(182, 231, 124),
                UpdateUiState.UpToDate => Color.FromRgb(182, 231, 124),
                UpdateUiState.Downloading or UpdateUiState.Applying => Color.FromRgb(172, 213, 255),
                UpdateUiState.Error => Color.FromRgb(255, 173, 180),
                _ => Color.FromRgb(181, 181, 192),
            });
            if (wasCollapsed)
            {
                UpdateStateText.Visibility = Visibility.Visible;
                if (IsLoaded && SystemParameters.ClientAreaAnimation)
                    UpdateStateText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                else
                    UpdateStateText.Opacity = 1;
            }
        }
        else
        {
            UpdateStateText.BeginAnimation(OpacityProperty, null);
            UpdateStateText.Visibility = Visibility.Collapsed;
            UpdateStateText.Opacity = 0;
        }

        string iconKey;
        string tooltip;
        string accessibleName;
        Color iconColor;
        bool stateAllows;

        if (updateUiState == UpdateUiState.Downloading)
        {
            iconKey = "CloseGeometry";
            tooltip = "取消下载";
            accessibleName = "取消下载";
            iconColor = Color.FromRgb(255, 173, 180);
            stateAllows = true;
            UpdateActionButton.Background = new SolidColorBrush(Color.FromRgb(52, 31, 38));
        }
        else if (updateUiState == UpdateUiState.Applying)
        {
            iconKey = "DownloadUpdateGeometry";
            tooltip = "正在重启更新…";
            accessibleName = "正在重启更新";
            iconColor = Color.FromRgb(172, 213, 255);
            stateAllows = false;
            UpdateActionButton.ClearValue(BackgroundProperty);
        }
        else if (updateUiState == UpdateUiState.Checking)
        {
            iconKey = "RefreshUpdateGeometry";
            tooltip = "正在检查更新…";
            accessibleName = "正在检查更新";
            iconColor = Color.FromRgb(142, 142, 153);
            stateAllows = false;
            UpdateActionButton.ClearValue(BackgroundProperty);
        }
        else if (availableUpdate is { HasUpdate: true } found)
        {
            iconKey = "DownloadUpdateGeometry";
            tooltip = updateStateDetail ?? $"下载并更新至 {found.Remote.ToDisplayString()}";
            accessibleName = $"下载并更新至 {found.Remote.ToDisplayString()}";
            iconColor = Color.FromRgb(182, 231, 124);
            stateAllows = true;
            UpdateActionButton.Background = new SolidColorBrush(Color.FromRgb(32, 44, 29));
        }
        else
        {
            iconKey = "RefreshUpdateGeometry";
            tooltip = updateStateDetail ?? "检查更新";
            accessibleName = "检查更新";
            iconColor = Color.FromRgb(142, 142, 153);
            stateAllows = true;
            UpdateActionButton.ClearValue(BackgroundProperty);
        }

        bool contextAllows = !operation && !panels.Relocating && modeTransition is null
            && (!EditingEmoji || (emojiPhase == EmojiEditPhase.Editing && !IconPanel.Dragging) || updateUiState == UpdateUiState.Downloading);

        UpdateActionButton.Content = ActionGlyph(iconKey, iconKey == "CloseGeometry" ? 10 : 12, iconColor);
        UpdateActionButton.IsEnabled = stateAllows && contextAllows;
        UpdateActionButton.ToolTip = tooltip;
        AutomationProperties.SetName(UpdateActionButton, accessibleName);
    }
}
