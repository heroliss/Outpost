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

  启动 Adapter 默认自动选择：显式 -UnityPath / UNITY_EDITOR_PATH → 直接 Editor；否则优先使用
  新版 Unity CLI 精确解析并启动 ProjectVersion 对应版本；CLI 不可用时回退 Hub 目录 / 注册表。
#>
param(
    [string]$UnityPath = "",
    [ValidateSet("Auto", "UnityCli", "Direct")]
    [string]$Adapter = "Auto",
    [ValidateSet("All", "PlayMode", "EditMode")]
    [string]$TestPlatform = "All"
)

$ErrorActionPreference = "Stop"
$projectPath = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'UnityAutomation.psm1') -Force

try {
    $unityEnvironment = Get-UnityAutomationEnvironment -ProjectPath $projectPath -UnityPath $UnityPath -Adapter $Adapter
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
    try {
        Write-Host "[run-tests] 平台: $Platform（batchmode 启动 Unity，通常需要 1-3 分钟）..."
        $invocation = Invoke-UnityTests `
            -Environment $unityEnvironment `
            -Platform $Platform `
            -ResultsPath $resultsPath `
            -LogPath $logPath
        $unityExit = $invocation.ExitCode
    }
    catch {
        Write-Host "[run-tests] $Platform 启动失败：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host "            按基础设施错误处理；日志（若已创建）：$logPath" -ForegroundColor Red
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 2; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Duration = 0 }
    }

    if (-not (Test-Path -LiteralPath $resultsPath)) {
        Write-Host "[run-tests] $Platform 未产出结果文件（Unity 退出码 $unityExit）。" -ForegroundColor Red
        Write-Host "            多半是编译或启动失败，查看：$logPath" -ForegroundColor Red
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 2; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Duration = 0 }
    }

    # PS 5.1 的 Get-Content 会把无 BOM UTF-8 按 ANSI 解释；File.ReadAllText 可避免中文 CDATA 破坏 XML。
    try {
        [xml]$xml = [System.IO.File]::ReadAllText($resultsPath)
    }
    catch {
        Write-Host "[run-tests] $Platform 结果文件无法解析：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host "            按基础设施错误处理，查看：$resultsPath / $logPath" -ForegroundColor Red
        return [pscustomobject]@{ Platform = $Platform; ExitCode = 2; Total = 0; Passed = 0; Failed = 0; Skipped = 0; Duration = 0 }
    }
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
Write-Host "[run-tests] Unity: $($unityEnvironment.EditorPath)"
Write-Host "[run-tests] 启动 Adapter: $($unityEnvironment.Adapter)（ProjectVersion $($unityEnvironment.Version)）"
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
