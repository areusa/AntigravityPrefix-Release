using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AntigravityPrefix;

internal static class UpdateApplier
{
    internal static StartupUpdateContext ParseStartupUpdateContext(string[] args)
    {
        string? updatedFrom = ReadArg(args, "--updated-from");
        string? cleanupDir = ReadArg(args, "--cleanup-dir");
        string? updateFailed = ReadArg(args, "--update-failed");
        return new StartupUpdateContext(updatedFrom, cleanupDir, updateFailed);
    }

    internal static void LaunchHelper(StagedUpdate staged, string targetDir, string oldVersion, bool relaunch = true)
    {
        var psi = new ProcessStartInfo(staged.StagedExePath)
        {
            WorkingDirectory = targetDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--target-dir");
        psi.ArgumentList.Add(targetDir);
        psi.ArgumentList.Add("--stage-dir");
        psi.ArgumentList.Add(staged.StageDir);
        psi.ArgumentList.Add("--old-version");
        psi.ArgumentList.Add(oldVersion);
        if (!relaunch) psi.ArgumentList.Add("--no-relaunch");
        Process.Start(psi);
    }

    internal static int Run(string[] args) => Apply(args, null);

    internal static int Apply(string[] args, Action<string>? beforeInstallTarget, string? logFolderOverride = null)
    {
        string? pidText = ReadArg(args, "--parent-pid");
        string? targetDir = ReadArg(args, "--target-dir");
        string? stageDir = ReadArg(args, "--stage-dir");
        string oldVersion = ReadArg(args, "--old-version") ?? "";
        bool noRelaunch = args.Contains("--no-relaunch");

        var logFolder = logFolderOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntigravityPrefix", "logs");
        var log = new LocalLog(logFolder);

        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentPid)
            || parentPid <= 0
            || string.IsNullOrWhiteSpace(targetDir)
            || string.IsNullOrWhiteSpace(stageDir)
            || !Directory.Exists(targetDir)
            || !Directory.Exists(stageDir))
        {
            log.Write("更新助手", "参数无效或目录不存在，未执行替换");
            return 2;
        }

        string targetExe = Path.Combine(targetDir, UpdateService.ReleaseExeName);
        var backups = new List<(string Dest, string Backup)>();
        var created = new List<string>();
        bool targetMovedAside = false;

        try
        {
            string stageManifestPath = Path.Combine(stageDir, "stage-manifest.json");
            if (!File.Exists(stageManifestPath))
                throw new FileNotFoundException("缺少更新暂存清单。", stageManifestPath);

            var stageManifest = JsonSerializer.Deserialize<StageManifest>(File.ReadAllText(stageManifestPath, Encoding.UTF8))
                ?? throw new InvalidDataException("更新暂存清单为空。");
            if (!SemanticVersion.TryParse(stageManifest.Version, out _) || stageManifest.Files is null || stageManifest.Files.Length == 0)
                throw new InvalidDataException("更新暂存清单版本或文件列表无效。");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var verifiedFiles = new List<(string RelativePath, string StagedPath, string ExpectedSha)>(stageManifest.Files.Length);
            foreach (var entry in stageManifest.Files)
            {
                if (entry is null || !UpdateService.IsSafeAllowedRelativePath(entry.RelativePath) || !UpdateService.IsLowerSha256Hex(entry.Sha256))
                    throw new InvalidDataException("暂存清单含非法路径或校验和。");
                if (!seen.Add(entry.RelativePath))
                    throw new InvalidDataException("暂存清单含重复文件路径。");

                string stagedFile = Path.GetFullPath(Path.Combine(stageDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                string fullStageRoot = Path.GetFullPath(stageDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!stagedFile.StartsWith(fullStageRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(stagedFile))
                    throw new FileNotFoundException("缺少已校验的暂存更新文件。", stagedFile);

                if (!string.Equals(UpdateService.ComputeSha256Hex(stagedFile), entry.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"暂存文件 SHA-256 复核失败：{entry.RelativePath}");
                if (entry.RelativePath == UpdateService.ReleaseExeName && !UpdateService.HasPeHeader(stagedFile))
                    throw new InvalidDataException("暂存主程序文件无效。");

                verifiedFiles.Add((entry.RelativePath, stagedFile, entry.Sha256));
            }

            if (!seen.Contains(UpdateService.ReleaseExeName))
                throw new InvalidDataException("暂存清单缺少主程序文件。");

            WaitForParentExit(parentPid, 10000);
            WaitUntilUnlockedForMove(targetExe, 5000);

            // 主程序优先置于首位完成备份与写入，避免旧进程锁文件导致部分替换。
            var ordered = verifiedFiles
                .OrderBy(f => f.RelativePath == UpdateService.ReleaseExeName ? 0 : 1)
                .ToList();

            string? expectedExeSha = null;
            foreach (var (relPath, stagedFile, expectedSha) in ordered)
            {
                beforeInstallTarget?.Invoke(relPath);

                string dest = Path.GetFullPath(Path.Combine(targetDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
                string fullTargetRoot = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!dest.StartsWith(fullTargetRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("目标文件路径越界。");

                var parentDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                if (File.Exists(dest))
                {
                    string backup = dest + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(dest, backup);
                    backups.Add((dest, backup));
                    if (relPath == UpdateService.ReleaseExeName) targetMovedAside = true;
                }
                else
                {
                    created.Add(dest);
                }

                File.Copy(stagedFile, dest, overwrite: false);
                if (relPath == UpdateService.ReleaseExeName) expectedExeSha = expectedSha;
            }

            if (expectedExeSha is null || !File.Exists(targetExe) || !string.Equals(UpdateService.ComputeSha256Hex(targetExe), expectedExeSha, StringComparison.Ordinal))
                throw new InvalidDataException("替换后主程序 SHA-256 校验未通过。");

            log.Write("更新助手", $"已从 {oldVersion} 更新至 v{stageManifest.Version}（替换 {ordered.Count} 个文件）");

            if (!noRelaunch)
            {
                var relaunch = new ProcessStartInfo(targetExe)
                {
                    WorkingDirectory = targetDir,
                    UseShellExecute = false,
                };
                relaunch.ArgumentList.Add("--updated-from");
                relaunch.ArgumentList.Add(oldVersion);
                relaunch.ArgumentList.Add("--cleanup-dir");
                relaunch.ArgumentList.Add(stageDir);
                Process.Start(relaunch);
            }
            return 0;
        }
        catch (Exception ex)
        {
            log.Write("更新助手", "替换失败，正在回滚：" + ex.Message);
            Rollback(backups, created);

            if (!noRelaunch && (File.Exists(targetExe) || targetMovedAside))
            {
                try
                {
                    if (File.Exists(targetExe))
                    {
                        var failStart = new ProcessStartInfo(targetExe)
                        {
                            WorkingDirectory = targetDir,
                            UseShellExecute = false,
                        };
                        failStart.ArgumentList.Add("--update-failed");
                        failStart.ArgumentList.Add(ex.Message);
                        Process.Start(failStart);
                    }
                }
                catch { }
            }
            return 1;
        }
    }

    internal static async Task CleanupAfterUpdateAsync(string targetDir, string? cleanupDir, string updatesRoot)
    {
        for (int attempt = 0; attempt < 25; attempt++)
        {
            bool anyRemaining = false;
            try
            {
                if (Directory.Exists(targetDir))
                {
                    foreach (var oldFile in Directory.EnumerateFiles(targetDir, "*.old", SearchOption.AllDirectories))
                    {
                        try { File.Delete(oldFile); }
                        catch { anyRemaining = true; }
                    }
                }
            }
            catch { }

            bool stageRemaining = false;
            if (!string.IsNullOrWhiteSpace(cleanupDir) && IsSafeChildDirectory(cleanupDir, updatesRoot))
            {
                if (Directory.Exists(cleanupDir))
                {
                    try { Directory.Delete(cleanupDir, recursive: true); }
                    catch { stageRemaining = true; }
                }
            }

            if (!anyRemaining && !stageRemaining) break;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    internal static bool IsSafeChildDirectory(string candidateDir, string parentRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateDir) || string.IsNullOrWhiteSpace(parentRoot)) return false;
        try
        {
            string root = Path.GetFullPath(parentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string child = Path.GetFullPath(candidateDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return child.StartsWith(root, StringComparison.OrdinalIgnoreCase) && child.Length > root.Length;
        }
        catch { return false; }
    }

    static void WaitForParentExit(int parentPid, int timeoutMs)
    {
        try
        {
            using var process = Process.GetProcessById(parentPid);
            if (!process.WaitForExit(timeoutMs))
                throw new TimeoutException("原程序退出超时，未执行替换。");
        }
        catch (ArgumentException) { }
    }

    static void WaitUntilUnlockedForMove(string filePath, int timeoutMs)
    {
        if (!File.Exists(filePath)) return;
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using (File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return;
            }
            catch (IOException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(150);
            }
        }
    }

    static void Rollback(List<(string Dest, string Backup)> backups, List<string> created)
    {
        foreach (var path in created)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        for (int i = backups.Count - 1; i >= 0; i--)
        {
            var (dest, backup) = backups[i];
            try
            {
                if (File.Exists(backup))
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(backup, dest);
                }
            }
            catch { }
        }
    }

    static string? ReadArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }
}
