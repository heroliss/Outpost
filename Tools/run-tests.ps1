<#
.SYNOPSIS
  命令行全量跑框架 PlayMode 测试（CI 护栏）。

.DESCRIPTION
  以 batchmode（无界面）启动 Unity 跑 Unity Test Runner，解析 NUnit XML 结果并打印摘要。
  全绿退出码 0；有失败退出码 1；环境问题（找不到 Unity / 工程被编辑器占用）退出码 2。

  ⚠ 前提：Unity 编辑器必须先关闭本工程——同一工程不能同时被两个 Unity 实例打开
  （脚本检测到 Temp/UnityLockfile 会直接报错退出；编辑器崩溃残留的锁文件手动删除即可）。

  典型用法：
    powershell -File Tools/run-tests.ps1                # 跑全套 PlayMode 测试
    powershell -File Tools/run-tests.ps1 -TestPlatform EditMode
  Unity 路径解析顺序：-UnityPath 参数 → $env:UNITY_EDITOR_PATH → Unity Hub 默认安装位置
  （按 ProjectSettings/ProjectVersion.txt 的版本号拼路径）。

  接入 git hook（可选）：在 .git/hooks/pre-push 里调用本脚本即可把"推送前全绿"变成强制；
  日常本地迭代建议手动跑（batchmode 启动一次 Unity 需要一两分钟，不适合每次 commit）。
#>
param(
    [string]$UnityPath = "",
    [ValidateSet("PlayMode", "EditMode")]
    [string]$TestPlatform = "PlayMode"
)

$ErrorActionPreference = "Stop"
$projectPath = Split-Path -Parent $PSScriptRoot   # Tools/ 的上级 = 工程根

# ── 工程占用检测：编辑器开着（或崩溃残留锁文件）时 batchmode 必然失败，先给出清晰指引 ──
$lockFile = Join-Path $projectPath "Temp/UnityLockfile"
if (Test-Path $lockFile) {
    Write-Host "[run-tests] 工程正被 Unity 编辑器占用（Temp/UnityLockfile 存在）。" -ForegroundColor Red
    Write-Host "            请先关闭编辑器再跑；若是编辑器崩溃残留，删除该锁文件即可。" -ForegroundColor Red
    exit 2
}

# ── 定位 Unity.exe：参数 → 环境变量 → Hub 默认安装路径（按工程版本号） ──
if ([string]::IsNullOrWhiteSpace($UnityPath)) { $UnityPath = $env:UNITY_EDITOR_PATH }
if ([string]::IsNullOrWhiteSpace($UnityPath)) {
    $versionLine = Get-Content (Join-Path $projectPath "ProjectSettings/ProjectVersion.txt") -TotalCount 1
    $version = ($versionLine -replace "m_EditorVersion:", "").Trim()
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
}
if (-not (Test-Path $UnityPath)) {
    Write-Host "[run-tests] 找不到 Unity 编辑器：$UnityPath" -ForegroundColor Red
    Write-Host "            用 -UnityPath 参数或环境变量 UNITY_EDITOR_PATH 指定 Unity.exe 完整路径。" -ForegroundColor Red
    exit 2
}

# ── 跑测试：结果与日志落在 Logs/（Unity 自有目录，已被 gitignore） ──
$resultsPath = Join-Path $projectPath "Logs/test-results.xml"
$logPath = Join-Path $projectPath "Logs/test-run.log"
if (Test-Path $resultsPath) { Remove-Item $resultsPath -Force }

Write-Host "[run-tests] Unity: $UnityPath"
Write-Host "[run-tests] 平台: $TestPlatform（batchmode 启动 Unity，通常需要 1-3 分钟）..."
$unityArgs = @(
    "-batchmode",
    "-projectPath", "`"$projectPath`"",
    "-runTests",
    "-testPlatform", $TestPlatform,
    "-testResults", "`"$resultsPath`"",
    "-logFile", "`"$logPath`""
)
$proc = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -PassThru -Wait -NoNewWindow
$unityExit = $proc.ExitCode

# ── 解析 NUnit XML：优先信结果文件（Unity 退出码：0 全过 / 2 有失败 / 其他 = 启动或编译错误） ──
if (-not (Test-Path $resultsPath)) {
    Write-Host "[run-tests] 未产出结果文件（Unity 退出码 $unityExit）——多半是编译错误或启动失败，看日志：$logPath" -ForegroundColor Red
    exit 2
}

[xml]$xml = Get-Content $resultsPath
$run = $xml."test-run"
$total = [int]$run.total; $passed = [int]$run.passed; $failed = [int]$run.failed; $skipped = [int]$run.skipped

if ($failed -gt 0) {
    Write-Host "[run-tests] FAIL: $failed 失败 / $passed 通过 / $total 总计（跳过 $skipped）" -ForegroundColor Red
    foreach ($case in $xml.SelectNodes("//test-case[@result='Failed']")) {
        Write-Host ("  x " + $case.fullname) -ForegroundColor Red
        $msg = $case.failure.message."#cdata-section"
        if ($null -eq $msg) { $msg = $case.failure.message }
        if ($null -ne $msg) { Write-Host ("    " + ($msg.Trim() -replace "`r`n", "`n    ")) -ForegroundColor DarkYellow }
    }
    exit 1
}

Write-Host "[run-tests] PASS: $passed / $total 全部通过（跳过 $skipped，耗时 $($run.duration)s）" -ForegroundColor Green
exit 0
