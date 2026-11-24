using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AntigravityPrefix;

internal static class UpdateValidation
{
    sealed class FakeTransport : IUpdateTransport
    {
        internal readonly Dictionary<string, byte[]> Payloads = new(StringComparer.Ordinal);
        internal readonly List<string> RequestedUrls = [];

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            string key = StripQuery(url);
            if (!Payloads.TryGetValue(key, out var bytes))
                throw new UpdateHttpException(HttpStatusCode.NotFound, "404: " + key);
            return Task.FromResult(Encoding.UTF8.GetString(bytes));
        }

        public async Task DownloadFileAsync(string url, string destinationPath, Action<long, long?>? onProgress, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            string key = StripQuery(url);
            if (!Payloads.TryGetValue(key, out var bytes))
                throw new UpdateHttpException(HttpStatusCode.NotFound, "404: " + key);
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
            onProgress?.Invoke(bytes.Length, bytes.Length);
        }

        static string StripQuery(string url)
        {
            int q = url.IndexOf('?');
            return q >= 0 ? url[..q] : url;
        }

        public void Dispose() { }
    }

    internal static void Run(Action<bool, string> check)
    {
        VerifySemVer(check);
        VerifyManifestAndChecksums(check);
        VerifyDualProfileIsolation(check);
        VerifyOfflineUpdaterEndToEnd(check);
        VerifySnapImmediateLayout(check);
        VerifyInitialInteractiveHost(check);
    }

    static void VerifySemVer(Action<bool, string> check)
    {
        check(SemanticVersion.TryParse("0.1.10", out var v10) && SemanticVersion.TryParse("0.1.9", out var v9) && v10.CompareTo(v9) > 0, "多位数语义版本号比较：0.1.10 > 0.1.9");
        check(SemanticVersion.TryParse("v0.1.3+6c0644", out var v3m) && SemanticVersion.TryParse("0.1.3", out var v3) && v3m.CompareTo(v3) == 0, "忽略前导 v 与构建元数据比较版本号");
        check(SemanticVersion.TryParse("0.2.0-alpha.1", out var pre1) && SemanticVersion.TryParse("0.2.0-beta.1", out var pre2) && SemanticVersion.TryParse("0.2.0", out var rel)
            && pre1.CompareTo(pre2) < 0 && pre2.CompareTo(rel) < 0, "预发布版本优先级低于正式版");
        foreach (var bad in new[] { "", " ", "1.2", "1.2.3.4", "01.2.3", "1.2.3+", "1.2.3-", "1.2.3-01", "v" })
            check(!SemanticVersion.TryParse(bad, out _), "非法语义版本号必须拒绝：" + bad);
    }

    static void VerifyManifestAndChecksums(Action<bool, string> check)
    {
        string validSha = new('a', 64);
        string validJson = JsonSerializer.Serialize(new { name = "AntigravityPrefix", version = "0.1.4", platform = "win-x64", executable = "AntigravityPrefix.exe", sha256 = validSha });
        var manifest = UpdateService.ParseManifest(validJson);
        check(manifest.Version.ToString() == "0.1.4" && manifest.Sha256 == validSha, "有效 version.json 解析正确");

        foreach (var bad in new[]
        {
            "{}",
            JsonSerializer.Serialize(new { name = "Other", version = "0.1.4", platform = "win-x64", executable = "AntigravityPrefix.exe", sha256 = validSha }),
            JsonSerializer.Serialize(new { name = "AntigravityPrefix", version = "0.1.4", platform = "linux-x64", executable = "AntigravityPrefix.exe", sha256 = validSha }),
            JsonSerializer.Serialize(new { name = "AntigravityPrefix", version = "0.1.4", platform = "win-x64", executable = "Other.exe", sha256 = validSha }),
            JsonSerializer.Serialize(new { name = "AntigravityPrefix", version = "bad", platform = "win-x64", executable = "AntigravityPrefix.exe", sha256 = validSha }),
            JsonSerializer.Serialize(new { name = "AntigravityPrefix", version = "0.1.4", platform = "win-x64", executable = "AntigravityPrefix.exe", sha256 = new string('A', 64) }),
        })
        {
            bool rejected = false;
            try { UpdateService.ParseManifest(bad); } catch (Exception ex) when (ex is InvalidDataException or JsonException) { rejected = true; }
            check(rejected, "无效 version.json 清单必须拒绝");
        }

        string sums10 = $"{validSha}  AntigravityPrefix.exe\n{new string('b', 64)}  version.json\n";
        string sums11 = sums10 + $"{new string('c', 64)}  default-emojis.json\n{new string('d', 64)}  licenses/MingCute-LICENSE.txt\n";
        check(UpdateService.ParseChecksums(sums10).Count == 2 && UpdateService.ParseChecksums(sums11).ContainsKey("default-emojis.json"), "校验和解析同时兼容 10 文件与 11 文件白名单");

        foreach (var badLine in new[]
        {
            $"{validSha}  ../outside.txt\n{validSha}  AntigravityPrefix.exe\n",
            $"{validSha}  sub\\evil.exe\n{validSha}  AntigravityPrefix.exe\n",
            $"{validSha}  extra.dll\n{validSha}  AntigravityPrefix.exe\n",
            $"{new string('e', 64)}  version.json\n",
            $"{validSha}  AntigravityPrefix.exe\n{validSha}  AntigravityPrefix.exe\n",
        })
        {
            bool rejected = false;
            try { UpdateService.ParseChecksums(badLine); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "校验和越界、非白名单、缺少主程序或重复路径必须拒绝");
        }
    }

    static void VerifyDualProfileIsolation(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "AntigravityPrefix-profile-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dataDir = Path.Combine(root, "data");
            var appDir = Path.Combine(root, "app");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(appDir);

            var defaultStore = new DefaultEmojiStore(appDir, allowRepoSync: false);
            var embeddedDefaults = defaultStore.Load();
            string[] expectedEmbeddedTokens;
            using (var embeddedStream = typeof(DefaultEmojiStore).Assembly.GetManifestResourceStream("AntigravityPrefix.Defaults.emoji-defaults.json"))
            {
                if (embeddedStream is not null)
                {
                    using var doc = JsonDocument.Parse(embeddedStream);
                    expectedEmbeddedTokens = doc.RootElement.GetProperty("Items").EnumerateArray()
                        .Select(e => e.GetProperty("Token").GetString()!)
                        .ToArray();
                }
                else
                {
                    expectedEmbeddedTokens = PrefixRules.Icons;
                }
            }
            check(embeddedDefaults.Items.Select(i => i.Token).SequenceEqual(expectedEmbeddedTokens)
                && defaultStore.RepoSourceFilePath is null,
                "初始内置默认应从内嵌资源加载当前预设且禁用测试源码写回");

            var savedDefault = defaultStore.Save(new[]
            {
                new EmojiItem("custom-a", "🚀"),
                new EmojiItem("custom-b", "💭"),
            });
            check(!savedDefault.SyncedToRepoSource && File.Exists(defaultStore.RuntimeFilePath), "保存内置默认应写入运行目录 default-emojis.json");
            check(!File.Exists(Path.Combine(dataDir, "emoji-settings.json")), "保存内置默认不得创建或覆盖个人快照");

            var personalStore = new EmojiConfigurationStore(dataDir);
            check(!personalStore.Exists, "初始无个人配置时个人快照存在状态应为 false");
            var customPersonal = new PrefixSnapshot(1, new[] { new EmojiItem("p0", "👩‍💻"), new EmojiItem("p1", "🔥") }, PrefixRules.Icons.Concat(["👩‍💻", "🔥"]));
            personalStore.Save(customPersonal);
            check(personalStore.Exists, "首次保存个人快照后存在状态应为 true");
            var defaultBytesBefore = File.ReadAllBytes(defaultStore.RuntimeFilePath);

            var reloadedPersonal = personalStore.Load(defaultStore.Load());
            var reloadedDefault = defaultStore.Load();
            check(reloadedPersonal.Items.Select(i => i.Token).SequenceEqual(["👩‍💻", "🔥"])
                && reloadedDefault.Items.Select(i => i.Token).SequenceEqual(["🚀", "💭"])
                && File.ReadAllBytes(defaultStore.RuntimeFilePath).SequenceEqual(defaultBytesBefore),
                "个人快照与内置默认必须彼此隔离、互不覆盖");

            var merged = PrefixSnapshot.MergeHistory(reloadedDefault, reloadedPersonal);
            check(PrefixRules.Apply("👩‍💻 旧会话", null, merged) == "旧会话"
                && PrefixRules.Apply("👩‍💻 旧会话", "🚀", merged) == "🚀 旧会话",
                "跨配置合并历史应在默认列表中也能准确识别并替换个人历史前缀");
        }
        finally { UpdateService.TryDeleteDirectory(root); }
    }

    static void VerifyOfflineUpdaterEndToEnd(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "AntigravityPrefix-update-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string rawRoot = "https://mock.local/areusa/AntigravityPrefix-Release";
            int deadPid = GetExitedProcessId();

            var newExeBytes = Encoding.ASCII.GetBytes("MZ-NEW-EXE-v0.1.4-PAYLOAD");
            var newDefaultsBytes = Encoding.UTF8.GetBytes("{\n  \"SchemaVersion\": 1,\n  \"UnicodeVersion\": \"17.0\",\n  \"Items\": [\n    { \"Id\": \"default-0\", \"Token\": \"🚀\" }\n  ]\n}\n");
            string newExeSha = ShaHex(newExeBytes);
            string newDefaultsSha = ShaHex(newDefaultsBytes);

            string newVersionJson = JsonSerializer.Serialize(new
            {
                name = "AntigravityPrefix",
                version = "0.1.4",
                platform = "win-x64",
                executable = "AntigravityPrefix.exe",
                sha256 = newExeSha,
            });
            byte[] newVersionBytes = Encoding.UTF8.GetBytes(newVersionJson);
            string newVersionSha = ShaHex(newVersionBytes);

            string sumsStub = $"{newExeSha}  AntigravityPrefix.exe\n{newDefaultsSha}  default-emojis.json\n{newVersionSha}  version.json\n";
            // 暂存真实校验需要 SHA256SUMS.txt 自身条目以外的所有条目，或不包含自身循环依赖；这里按真实格式测试不含 SHA256SUMS.txt 自身。
            byte[] sumsBytes = Encoding.UTF8.GetBytes(sumsStub);

            var transport = new FakeTransport();
            transport.Payloads[$"{rawRoot}/main/release/version.json"] = newVersionBytes;
            transport.Payloads[$"{rawRoot}/v0.1.4/release/SHA256SUMS.txt"] = sumsBytes;
            transport.Payloads[$"{rawRoot}/v0.1.4/release/AntigravityPrefix.exe"] = newExeBytes;
            transport.Payloads[$"{rawRoot}/v0.1.4/release/default-emojis.json"] = newDefaultsBytes;
            transport.Payloads[$"{rawRoot}/v0.1.4/release/version.json"] = newVersionBytes;

            using var service = new UpdateService(new SemanticVersion(0, 1, 3), transport, rawRoot);
            var checkResult = service.CheckForUpdateAsync(bypassCache: true, CancellationToken.None).GetAwaiter().GetResult();
            check(checkResult.HasUpdate && checkResult.Remote.ToString() == "0.1.4", "检查到更高版本 0.1.4 时应提示可更新");

            // 1. 完整发布目录原位更新，不碰个人配置
            var fullDir = Path.Combine(root, "full-release");
            var updatesRoot = Path.Combine(root, "localdata", "updates");
            var dataDir = Path.Combine(root, "localdata");
            var logsDir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(fullDir);
            Directory.CreateDirectory(dataDir);
            var oldExeBytes = Encoding.ASCII.GetBytes("MZ-OLD-EXE-v0.1.3-PAYLOAD");
            File.WriteAllBytes(Path.Combine(fullDir, "AntigravityPrefix.exe"), oldExeBytes);
            File.WriteAllText(Path.Combine(fullDir, "version.json"), "{\"version\":\"0.1.3\"}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fullDir, "default-emojis.json"), "{\"SchemaVersion\":1}", new UTF8Encoding(false));
            var personalBytes = Encoding.UTF8.GetBytes("PERSONAL-DO-NOT-TOUCH");
            var settingsBytes = Encoding.UTF8.GetBytes("{\"Topmost\":false}");
            File.WriteAllBytes(Path.Combine(dataDir, "emoji-settings.json"), personalBytes);
            File.WriteAllBytes(Path.Combine(dataDir, "settings.json"), settingsBytes);

            var stagedFull = service.DownloadAndVerifyAsync(checkResult.Manifest, fullDir, updatesRoot, null, CancellationToken.None).GetAwaiter().GetResult();
            int exitFull = UpdateApplier.Apply(
                ["--apply-update", "--parent-pid", deadPid.ToString(), "--target-dir", fullDir, "--stage-dir", stagedFull.StageDir, "--old-version", "v0.1.3", "--no-relaunch"],
                null,
                logsDir);
            check(exitFull == 0
                && File.ReadAllBytes(Path.Combine(fullDir, "AntigravityPrefix.exe")).SequenceEqual(newExeBytes)
                && File.ReadAllBytes(Path.Combine(fullDir, "default-emojis.json")).SequenceEqual(newDefaultsBytes)
                && File.ReadAllBytes(Path.Combine(fullDir, "SHA256SUMS.txt")).SequenceEqual(sumsBytes)
                && File.ReadAllBytes(Path.Combine(dataDir, "emoji-settings.json")).SequenceEqual(personalBytes)
                && File.ReadAllBytes(Path.Combine(dataDir, "settings.json")).SequenceEqual(settingsBytes),
                "完整发布目录自更新应替换为新版文件，且完整保留个人配置与偏好");

            UpdateApplier.CleanupAfterUpdateAsync(fullDir, stagedFull.StageDir, updatesRoot).GetAwaiter().GetResult();
            check(!Directory.EnumerateFiles(fullDir, "*.old", SearchOption.AllDirectories).Any() && !Directory.Exists(stagedFull.StageDir),
                "更新后应清理 .old 备份与 updates 下暂存目录");

            // 2. 清理路径越界保护
            var outsideDir = Path.Combine(root, "outside-keep");
            Directory.CreateDirectory(outsideDir);
            check(!UpdateApplier.IsSafeChildDirectory(outsideDir, updatesRoot), "清理逻辑必须拒绝删除 updates 根目录以外的路径");
            UpdateApplier.CleanupAfterUpdateAsync(fullDir, outsideDir, updatesRoot).GetAwaiter().GetResult();
            check(Directory.Exists(outsideDir), "越界清理路径不得被删除");

            // 3. 独立单文件模式更新 + tag 404 回退至 main
            var standaloneDir = Path.Combine(root, "standalone");
            Directory.CreateDirectory(standaloneDir);
            File.WriteAllBytes(Path.Combine(standaloneDir, "AntigravityPrefix.exe"), oldExeBytes);
            transport.Payloads.Remove($"{rawRoot}/v0.1.4/release/SHA256SUMS.txt");
            transport.Payloads[$"{rawRoot}/main/release/SHA256SUMS.txt"] = sumsBytes;
            transport.Payloads[$"{rawRoot}/main/release/AntigravityPrefix.exe"] = newExeBytes;
            var stagedSolo = service.DownloadAndVerifyAsync(checkResult.Manifest, standaloneDir, updatesRoot, null, CancellationToken.None).GetAwaiter().GetResult();
            int exitSolo = UpdateApplier.Apply(
                ["--apply-update", "--parent-pid", deadPid.ToString(), "--target-dir", standaloneDir, "--stage-dir", stagedSolo.StageDir, "--old-version", "v0.1.3", "--no-relaunch"],
                null,
                logsDir);
            check(exitSolo == 0
                && File.ReadAllBytes(Path.Combine(standaloneDir, "AntigravityPrefix.exe")).SequenceEqual(newExeBytes)
                && !File.Exists(Path.Combine(standaloneDir, "version.json"))
                && !File.Exists(Path.Combine(standaloneDir, "default-emojis.json")),
                "独立单文件运行态更新时仅替换主程序，且支持 tag 404 回退 main");

            // 4. 校验和不匹配拦截
            transport.Payloads[$"{rawRoot}/main/release/AntigravityPrefix.exe"] = Encoding.ASCII.GetBytes("MZ-CORRUPTED");
            bool mismatchBlocked = false;
            try
            {
                service.DownloadAndVerifyAsync(checkResult.Manifest, standaloneDir, updatesRoot, null, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { mismatchBlocked = true; }
            check(mismatchBlocked, "下载文件 SHA-256 与清单不一致时必须中止并清理");

            // 5. 替换失败自动回滚到原 EXE
            var rollbackDir = Path.Combine(root, "rollback-release");
            Directory.CreateDirectory(rollbackDir);
            File.WriteAllBytes(Path.Combine(rollbackDir, "AntigravityPrefix.exe"), oldExeBytes);
            File.WriteAllText(Path.Combine(rollbackDir, "version.json"), "{\"version\":\"0.1.3\"}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(rollbackDir, "default-emojis.json"), "{\"SchemaVersion\":1}", new UTF8Encoding(false));
            transport.Payloads[$"{rawRoot}/main/release/AntigravityPrefix.exe"] = newExeBytes;
            transport.Payloads[$"{rawRoot}/main/release/version.json"] = newVersionBytes;
            transport.Payloads[$"{rawRoot}/main/release/default-emojis.json"] = newDefaultsBytes;
            var stagedRollback = service.DownloadAndVerifyAsync(checkResult.Manifest, rollbackDir, updatesRoot, null, CancellationToken.None).GetAwaiter().GetResult();
            bool failureInjected = false;
            int exitRollback = UpdateApplier.Apply(
                ["--apply-update", "--parent-pid", deadPid.ToString(), "--target-dir", rollbackDir, "--stage-dir", stagedRollback.StageDir, "--old-version", "v0.1.3", "--no-relaunch"],
                rel => { if (rel == "version.json") { failureInjected = true; throw new IOException("模拟中途写入失败"); } },
                logsDir);
            check(failureInjected
                && exitRollback == 1
                && File.ReadAllBytes(Path.Combine(rollbackDir, "AntigravityPrefix.exe")).SequenceEqual(oldExeBytes)
                && !File.Exists(Path.Combine(rollbackDir, "AntigravityPrefix.exe.old")),
                "中途替换失败时必须自动从 .old 回滚恢复原主程序");
        }
        finally { UpdateService.TryDeleteDirectory(root); }
    }

    static int GetExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit(5000);
        return process.Id;
    }

    static void VerifySnapImmediateLayout(Action<bool, string> check)
    {
        var panel = new EmojiGridPanel();
        var buttons = Enumerable.Range(0, 8)
            .Select(i => new System.Windows.Controls.Button { Uid = "cell-" + i, Width = 35, Height = 35 })
            .ToArray();

        panel.SetButtons(buttons, snapImmediate: false);
        panel.Measure(new System.Windows.Size(EmojiLayout.FullListWidth, 200));
        panel.Arrange(new System.Windows.Rect(0, 0, EmojiLayout.FullListWidth, 200));

        var reordered = buttons.Reverse().ToArray();
        panel.SetButtons(reordered, snapImmediate: true);
        panel.Measure(new System.Windows.Size(EmojiLayout.FullListWidth, 200));
        panel.Arrange(new System.Windows.Rect(0, 0, EmojiLayout.FullListWidth, 200));

        bool allSnapped = !panel.HasMovingCells;
        for (int i = 0; i < reordered.Length; i++)
        {
            double expectedX = i % 6 * 39;
            double expectedY = i / 6 * 39;
            if (reordered[i].RenderTransform is not System.Windows.Media.TranslateTransform shift
                || Math.Abs(shift.X - expectedX) > 1e-6
                || Math.Abs(shift.Y - expectedY) > 1e-6)
            {
                allSnapped = false;
                break;
            }
        }
        check(allSnapped, "snapImmediate=true 切换配置时所有单元格应直接落入最终网格槽位且零弹簧残余速度");
    }

    static void VerifyInitialInteractiveHost(Action<bool, string> check)
    {
        var app = System.Windows.Application.Current;
        var previous = app.ShutdownMode;
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var window = new System.Windows.Window
        {
            Left = -32000,
            Top = -32000,
            Width = 382,
            Height = 217,
            WindowStyle = System.Windows.WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0,
        };
        var bar = new CompactBar(_ => Task.CompletedTask, () => { }, () => { });
        try
        {
            var host = new LivePanelHost(window, new SmoothBorder
            {
                BorderThickness = new System.Windows.Thickness(1),
                Child = new System.Windows.Controls.Grid(),
            }, bar);
            window.Show();
            host.ReserveInitialEnvelope();
            check(host.FullContent.IsHitTestVisible && !host.MiniContent.IsHitTestVisible,
                "初始预留运动空间且未触发高度重排时，完整面板必须保持可交互命中状态");
        }
        finally
        {
            bar.Close();
            window.Close();
            app.ShutdownMode = previous;
        }
    }

    static string ShaHex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
