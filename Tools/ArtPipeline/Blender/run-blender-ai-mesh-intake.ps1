<#
.SYNOPSIS
  审计并归档外部 AI 生成的 Blender Mesh 候选。

.DESCRIPTION
  保留源 .blend，不做自动减面、重拓扑或拆件。输出便携贴图、打包 .blend、
  FBX、GLB、URP Metallic-Smoothness 派生图与可校验的 intake-report.json。
#>
param(
    [string]$BlenderPath = "",
    [Parameter(Mandatory = $true)]
    [string]$SourceBlend,
    [string]$OutputRoot = "",
    [string]$AssetId = "NW_FieldKitchen_01",
    [string]$SourceObject = "model"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectPath = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$intakePath = Join-Path $PSScriptRoot "blender_ai_mesh_intake.py"

function Resolve-BlenderExecutable {
    param([string]$ExplicitPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($ExplicitPath, $env:SSFRAMEWORK_BLENDER_PATH, $env:SSFRAMEWORK_BLENDER)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { $candidates.Add($candidate) }
    }

    $command = Get-Command blender -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) { $candidates.Add($command.Source) }

    if ($env:OS -eq "Windows_NT") {
        $registryGlobs = @(
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
            "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
        )
        foreach ($entry in (Get-ItemProperty $registryGlobs -ErrorAction SilentlyContinue)) {
            $displayName = $entry.PSObject.Properties["DisplayName"]
            $installLocation = $entry.PSObject.Properties["InstallLocation"]
            if ($null -eq $displayName -or $displayName.Value -notlike "Blender*") { continue }
            if ($null -ne $installLocation -and
                -not [string]::IsNullOrWhiteSpace([string]$installLocation.Value)) {
                $candidates.Add((Join-Path ([string]$installLocation.Value) "blender.exe"))
            }
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $path = $candidate
        if (Test-Path -LiteralPath $path -PathType Container) {
            $path = Join-Path $path $(if ($env:OS -eq "Windows_NT") { "blender.exe" } else { "blender" })
        }
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    throw "未找到 Blender。请用 -BlenderPath 指定可执行文件，或设置 SSFRAMEWORK_BLENDER_PATH。"
}

try {
    if (-not (Test-Path -LiteralPath $intakePath -PathType Leaf)) {
        throw "缺少 AI Mesh Intake 脚本：$intakePath"
    }
    if ([string]::IsNullOrWhiteSpace($AssetId) -or
        $AssetId -notmatch "^[A-Za-z][A-Za-z0-9_]+$") {
        throw "AssetId 必须是稳定的 ASCII 标识符（字母开头，只含字母、数字和下划线）：$AssetId"
    }

    $blender = Resolve-BlenderExecutable -ExplicitPath $BlenderPath
    $resolvedSourceBlend = [System.IO.Path]::GetFullPath($SourceBlend)
    if (-not (Test-Path -LiteralPath $resolvedSourceBlend -PathType Leaf)) {
        throw "源 .blend 不存在：$resolvedSourceBlend"
    }
    if ([System.IO.Path]::GetExtension($resolvedSourceBlend) -ne ".blend") {
        throw "SourceBlend 必须是 .blend 文件：$resolvedSourceBlend"
    }

    $resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        Join-Path $projectPath "ArtPipelineOutput/AIMeshIntake"
    }
    else {
        [System.IO.Path]::GetFullPath($OutputRoot)
    }
    $assetOutput = Join-Path $resolvedOutputRoot $AssetId
    [System.IO.Directory]::CreateDirectory($assetOutput) | Out-Null

    Write-Host "[ai-mesh-intake] Blender: $blender"
    Write-Host "[ai-mesh-intake] Source: $resolvedSourceBlend"
    Write-Host "[ai-mesh-intake] Output: $assetOutput"
    $processOutput = & $blender `
        --background $resolvedSourceBlend `
        --factory-startup `
        --disable-autoexec `
        --python $intakePath `
        -- `
        --output-dir $assetOutput `
        --asset-id $AssetId `
        --source-object $SourceObject 2>&1
    $exitCode = $LASTEXITCODE
    $processOutput | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "Blender 以退出码 $exitCode 结束。"
    }

    $reportPath = Join-Path $assetOutput "intake-report.json"
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
        throw "未产出 intake-report.json：$reportPath"
    }
    $report = [System.IO.File]::ReadAllText($reportPath) | ConvertFrom-Json
    if ($report.status -ne "inspected" -or
        $report.schemaVersion -ne 1 -or
        $report.harnessVersion -ne "0.1.0" -or
        $report.asset.id -ne $AssetId) {
        throw "Intake 报告状态、版本或资产 ID 不符合预期。"
    }
    if (-not $report.acceptance.allReferencedTexturesArchived -or
        -not $report.acceptance.packedBlendSaved -or
        -not $report.acceptance.fbxExported -or
        -not $report.acceptance.glbExported -or
        -not $report.acceptance.fbxRoundTripRead -or
        -not $report.acceptance.glbRoundTripRead) {
        throw "贴图归档、便携源文件或交换格式验证未通过。"
    }
    if ($report.acceptance.productionApproved -or
        -not $report.acceptance.manualArtReviewStillRequired) {
        throw "Intake 不得自动把外部 AI Mesh 标记为生产资产。"
    }

    foreach ($file in $report.files) {
        $filePath = Join-Path $assetOutput $file.path
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "报告声明的文件不存在：$filePath"
        }
        if ((Get-Item -LiteralPath $filePath).Length -ne [long]$file.bytes) {
            throw "文件大小验证失败：$filePath"
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $file.sha256) {
            throw "文件 Hash 验证失败：$filePath"
        }
    }

    Write-Host (
        "[ai-mesh-intake] INSPECTED: {0} Mesh / {1} vertices / {2} triangles / {3} textures" -f `
            $report.geometry.meshObjectCount, `
            $report.geometry.vertexCount, `
            $report.geometry.triangleCount, `
            $report.textures.Count
    ) -ForegroundColor Green
    foreach ($warning in $report.warnings) {
        Write-Host "[ai-mesh-intake] REVIEW: $warning" -ForegroundColor Yellow
    }
    Write-Host "[ai-mesh-intake] Report: $reportPath"
    exit 0
}
catch {
    Write-Host "[ai-mesh-intake] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
