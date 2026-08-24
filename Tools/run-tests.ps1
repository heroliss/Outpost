<#
.SYNOPSIS
  命令行运行 Unity EditMode + PlayMode 全量测试（CI 护栏）。

.DESCRIPTION
  以 batchmode 顺序运行 Unity Test Runner，分别保留 NUnit XML 与 Editor 日志，再汇总退出码。
  全绿退出码 0；有测试失败退出码 1；环境/编译/零测试等基础设施问题退出码 2。

  ⚠ 前提：Unity 编辑器必须先关闭本工程——同一工程不能同时被两个 Unity 实例打开。
  脚本检测到 Temp/UnityLockfile 会直接退出；只有确认是编辑器崩溃残留时才手动删锁文件。

  典型用法：
    powershell -File Tools/run-tests.ps1                         # EditMode + PlayMode 全量
    powershell -File Tools/run-tests.ps1 -TestPlatform PlayMode # 只跑一个平台
    powershell -File Tools/run-tests.ps1 -TestPlatform EditMode

  Unity 路径解析顺序：-UnityPath 参数 → $env:UNITY_EDITOR_PATH → Unity Hub 默认目录 →
  Unity Hub secondaryInstallPath.json → Unity Installer 注册表（按 ProjectVersion 精确匹配）。
#>
param(
    [string]$UnityPath = "",
    [ValidateSet("All", "PlayMode", "EditMode")]
    [string]$TestPlatform = "All"
)

$ErrorActionPreference = "Stop"
$projectPath = Split-Path -Parent $PSScriptRoot

# 同一工程不能被交互式 Editor 与 batchmode 同时打开。这里不自动删除锁文件，避免误伤真实会话。
$lockFile = Join-Path $projectPath "Temp/UnityLockfile"
if (Test-Path -LiteralPath $lockFile) {
    Write-Host "[run-tests] 工程正被 Unity 编辑器占用（Temp/UnityLockfile 存在）。" -ForegroundColor Red
    Write-Host "            请先关闭编辑器；只有确认是崩溃残留时才手动删除锁文件。" -ForegroundColor Red
    exit 2
}

$versionLine = Get-Content -LiteralPath (Join-Path $projectPath "ProjectSettings/ProjectVersion.txt") -TotalCount 1
$projectUnityVersion = ($versionLine -replace "m_EditorVersion:", "").Trim()

function Resolve-UnityEditorPath {
    param([string]$ExplicitPath, [string]$Version)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (Test-Path -LiteralPath $ExplicitPath -PathType Leaf) { return (Resolve-Path -LiteralPath $ExplicitPath).Path }
        throw "指定的 Unity 编辑器不存在：$ExplicitPath"
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:UNITY_EDITOR_PATH)) {
        $candidates.Add($env:UNITY_EDITOR_PATH)
    }
    $candidates.Add("C:\Program Files\Unity\Hub\Editor\$Version\Editor\Unity.exe")

    $hubSecondaryConfig = Join-Path $env:APPDATA "UnityHub/secondaryInstallPath.json"
    if (Test-Path -LiteralPath $hubSecondaryConfig -PathType Leaf) {
        try {
            $hubSecondaryRoot = [System.IO.File]::ReadAllText($hubSecondaryConfig) | ConvertFrom-Json
            if (-not [string]::IsNullOrWhiteSpace($hubSecondaryRoot)) {
                $candidates.Add((Join-Path $hubSecondaryRoot "$Version/Editor/Unity.exe"))
            }
        }
        catch {
            Write-Host "[run-tests] 警告：无法读取 Unity Hub 次级安装目录配置：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    foreach ($registryPath in @(
        "HKLM:\SOFTWARE\Unity Technologies\Installer\Unity $Version",
        "HKLM:\SOFTWARE\WOW6432Node\Unity Technologies\Installer\Unity $Version"
    )) {
        $install = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($null -ne $install) {
            $location = $install.'Location x64'
            if ([string]::IsNullOrWhiteSpace($location)) { $location = $install.Location }
            if (-not [string]::IsNullOrWhiteSpace($location)) {
                $candidates.Add((Join-Path $location "Editor/Unity.exe"))
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw "找不到项目要求的 Unity $Version。用 -UnityPath 或 UNITY_EDITOR_PATH 指定 Unity.exe 完整路径。"
}

try {
    $UnityPath = Resolve-UnityEditorPath -ExplicitPath $UnityPath -Version $projectUnityVersion
}
catch {
    Write-Host "[run-tests] $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

function Invoke-UnityTestPlatform {
    param(
        [Parameter(Mandatory = $true)][string]$Platform
    )

    $platformKey = $Platform.ToLowerInvariant()
    $resultsPath = Join-Path $projectPath "Logs/test-results-$platformKey.xml"
    $logPath = Join-Path $projectPath "Logs/test-run-$platformKey.log"
    if (Test-Path -LiteralPath $resultsPath) { Remove-Item -LiteralPath $resultsPath -Force }
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }

    Write-Host "[run-tests] 平台: $Platform（batchmode 启动 Unity，通常需要 1-3 分钟）..."
    $unityArgs = @(
        "-batchmode",
        "-projectPath", "`"$projectPath`"",
        "-runTests",
        "-testPlatform", $Platform,
        "-testResults", "`"$resultsPath`"",
        "-logFile", "`"$logPath`""
    )

    $process = Start-Process -FilePath $UnityPath -ArgumentList $unityArgs -PassThru -Wait -NoNewWindow
    $unityExit = $process.ExitCode

    if (-not (Test-Path -LiteralPath $resultsPath)) {
        Write-Host "[run-tests] $Platform 未产出结果文件（Unity 退出码 $unityExit）。" -ForegroundColor Red
        Write-Host "            多半是编译或启动失败，查看：$logPath" -ForegroundColor Red
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 2; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Duration = 0 }
    }

    # PS 5.1 的 Get-Content 会把无 BOM UTF-8 按 ANSI 解释；File.ReadAllText 可避免中文 CDATA 破坏 XML。
    [xml]$xml = [System.IO.File]::ReadAllText($resultsPath)
    $run = $xml."test-run"
    $total = [int]$run.total
    $passed = [int]$run.passed
    $failed = [int]$run.failed
    $skipped = [int]$run.skipped
    $duration = [double]$run.duration

    if ($total -le 0) {
        Write-Host "[run-tests] $Platform 发现 0 条测试——按基础设施错误处理，避免空跑假绿。" -ForegroundColor Red
        Write-Host "            查看：$logPath" -ForegroundColor Red
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 2; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Duration = $duration }
    }

    if ($failed -gt 0) {
        Write-Host "[run-tests] $Platform FAIL: $failed 失败 / $passed 通过 / $total 总计（跳过 $skipped）" -ForegroundColor Red
        foreach ($case in $xml.SelectNodes("//test-case[@result='Failed']")) {
            Write-Host ("  x " + $case.fullname) -ForegroundColor Red
            $message = $case.failure.message."#cdata-section"
            if ($null -eq $message) { $message = $case.failure.message }
            if ($null -ne $message) {
                Write-Host ("    " + ($message.Trim() -replace "`r`n", "`n    ")) -ForegroundColor DarkYellow
            }
        }
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 1; Total = $total; Passed = $passed; Failed = $failed; Skipped = $skipped; Duration = $duration }
    }

    if ($unityExit -ne 0) {
        Write-Host "[run-tests] $Platform 测试 XML 无失败，但 Unity 以非零退出码 $unityExit 结束——按基础设施错误处理。" -ForegroundColor Red
        Write-Host "            查看：$logPath" -ForegroundColor Red
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 2; Total = $total; Passed = $passed; Failed = 0; Skipped = $skipped; Duration = $duration }
    }

    Write-Host "[run-tests] $Platform PASS: $passed / $total（跳过 $skipped，耗时 $duration s）" -ForegroundColor Green
    return [pscustomobject]@{ Platform = $Platform; ExitCode = 0; Total = $total; Passed = $passed; Failed = 0; Skipped = $skipped; Duration = $duration }
}

$platforms = if ($TestPlatform -eq "All") { @("EditMode", "PlayMode") } else { @($TestPlatform) }
Write-Host "[run-tests] Unity: $UnityPath"
Write-Host "[run-tests] 测试范围: $($platforms -join ' + ')"

$summaries = @()
foreach ($platform in $platforms) {
    $summary = Invoke-UnityTestPlatform -Platform $platform
    $summaries += $summary

    # 编译/启动/零测试属于共同基础设施故障，继续启动下一平台只会重复失败并浪费时间。
    if ($summary.ExitCode -eq 2) { break }
}

$total = ($summaries | Measure-Object -Property Total -Sum).Sum
$passed = ($summaries | Measure-Object -Property Passed -Sum).Sum
$failed = ($summaries | Measure-Object -Property Failed -Sum).Sum
$skipped = ($summaries | Measure-Object -Property Skipped -Sum).Sum
$duration = ($summaries | Measure-Object -Property Duration -Sum).Sum

if ($summaries.ExitCode -contains 2) {
    Write-Host "[run-tests] ERROR: 基础设施错误；已完成 $($summaries.Count)/$($platforms.Count) 个平台。" -ForegroundColor Red
    exit 2
}
if ($summaries.ExitCode -contains 1) {
    Write-Host "[run-tests] FAIL: $failed 失败 / $passed 通过 / $total 总计（跳过 $skipped，累计 $duration s）" -ForegroundColor Red
    exit 1
}

Write-Host "[run-tests] PASS: $passed / $total 全部通过（跳过 $skipped，累计 $duration s）" -ForegroundColor Green
exit 0
