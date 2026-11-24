# 使用 UTF-8 BOM；只报告文件名与类别，不回显可能敏感的原文。
[CmdletBinding()]
param([string]$ReleaseDirectory = 'release')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReleaseDir = [IO.Path]::GetFullPath((Join-Path $taskRoot $ReleaseDirectory))
if (-not $taskReleaseDir.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '发布检查目录超出仓库范围。' }
Push-Location $taskRoot
try {
    $taskFiles = @(git ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '请先在本目录初始化 Git 仓库。' }
    $taskProblems = [Collections.Generic.List[string]]::new()
    $taskStrictUtf8 = [Text.UTF8Encoding]::new($false,$true)
    foreach ($taskFile in $taskFiles) {
        $taskFile = $taskFile.Trim('"')
        if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { continue }
        if ($taskFile -match '(^|/)(bin|obj|artifacts|dist|fixture-data|captures|diagnostics|logs)(/|$)|\.(zip|7z|pdb|pfx|pem|key|log|bak)$|(^|/)\.env($|\.)') { $taskProblems.Add("运行或敏感文件：$taskFile") }
        if ($taskFile -match '(^|/)(settings|emoji-settings)\.json$') { $taskProblems.Add("未授权配置：$taskFile") }
        if ([IO.Path]::GetExtension($taskFile) -notin @('.cs','.xaml','.csproj','.json','.md','.ps1','.mjs','.js','.html','.css','.yml','.txt','.manifest','.svg','') -and $taskFile -notin @('.gitignore','.gitattributes')) { continue }
        # Unicode 原始数据及第三方许可保留原始归属信息。
        if ($taskFile -match '^release/licenses/|^assets/unicode/|(^|/)LICENSE($|\.)') { continue }
        try { $taskText = $taskStrictUtf8.GetString([IO.File]::ReadAllBytes((Join-Path $taskRoot $taskFile))) }
        catch { $taskProblems.Add("UTF-8 解码失败：$taskFile"); continue }
        $taskPatterns = @{
            '私人绝对路径' = '(?i)(?<![a-z0-9_])[a-z]:[\\/](?!/)|/(?:Users|home)/[^/\s]+/'
            '访问令牌' = '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{24,})'
            '私钥' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
        }
        foreach ($taskKind in $taskPatterns.Keys) { if ($taskText -match $taskPatterns[$taskKind]) { $taskProblems.Add("${taskKind}：$taskFile") } }
        if ($taskText.Contains([char]0xFFFD)) { $taskProblems.Add("存在替代乱码字符：$taskFile") }
    }
    $taskBaseExpected = @('AntigravityPrefix.exe','使用说明.md','THIRD-PARTY-NOTICES.txt','version.json','SHA256SUMS.txt','licenses/MingCute-LICENSE.txt','licenses/Unicode-LICENSE.txt','licenses/Runtime-LICENSE.txt','licenses/Runtime-NOTICES.txt','licenses/WindowsDesktop-LICENSE.txt')
    $taskAllowed = $taskBaseExpected + @('default-emojis.json')
    $taskVersionPath = Join-Path $taskReleaseDir 'version.json'
    $taskReleaseVer = if (Test-Path -LiteralPath $taskVersionPath -PathType Leaf) {
        try { (Get-Content -LiteralPath $taskVersionPath -Raw -Encoding utf8 | ConvertFrom-Json).version } catch { '' }
    } else { '' }
    $taskRequired = if ($taskReleaseVer -and $taskReleaseVer -ne '0.1.3') { $taskAllowed } else { $taskBaseExpected }
    foreach ($taskItem in Get-ChildItem -LiteralPath $taskReleaseDir -Recurse -File) {
        $taskRelative = $taskItem.FullName.Substring($taskReleaseDir.Length + 1).Replace('\','/')
        if ($taskRelative -notin $taskAllowed) { $taskProblems.Add("发布目录多余文件：$taskRelative") }
    }
    foreach ($taskExpectedFile in $taskRequired) { if (-not (Test-Path -LiteralPath (Join-Path $taskReleaseDir $taskExpectedFile))) { $taskProblems.Add("发布文件缺失：$taskExpectedFile") } }
    # 发布文件按字节保存，校验和必须在 Git 检出后仍然有效。
    foreach ($taskChecksum in Get-Content -LiteralPath (Join-Path $taskReleaseDir 'SHA256SUMS.txt') -Encoding utf8) {
        if ($taskChecksum -notmatch '^([0-9a-f]{64})  (.+)$') { $taskProblems.Add('发布校验和格式无效。'); continue }
        $taskDigest = $Matches[1]
        $taskName = $Matches[2]
        if ($taskName -notin $taskAllowed) { $taskProblems.Add('发布校验和含非白名单路径。'); continue }
        if ((Get-FileHash -LiteralPath (Join-Path $taskReleaseDir $taskName) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskDigest) { $taskProblems.Add("发布校验和不一致：$taskName") }
    }
    if ($taskProblems.Count -gt 0) { $taskProblems | ForEach-Object { Write-Output $_ }; throw ('提交前检查未通过：' + $taskProblems.Count + ' 项。') }
    Write-Host ('提交前检查通过，共 ' + $taskFiles.Count + ' 个文件；发布目录符合白名单。')
} finally { Pop-Location }
