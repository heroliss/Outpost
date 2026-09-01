<#
.SYNOPSIS
  从既有 Blender 几何生成透明背景、中性光照的可选参考图。

.DESCRIPTION
  适合粗 Blockout 细化或重建回归，不把“完整资产渲染后再生成同一 3D”当默认生产流程。
  不修改源 .blend、Blender 用户偏好或 Unity Assets。输出写入已忽略的
  ArtPipelineOutput/AIMeshInput/<AssetId>/，并以 manifest 记录输入与来源 Hash。
#>
param(
    [string]$BlenderPath = "",
    [string]$SourceBlend = "",
    [string]$OutputRoot = "",
    [string]$AssetId = "NW_WaterRecycler_01",
    [ValidateSet(512, 640, 1024)]
    [int]$Resolution = 640
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectPath = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$rendererPath = Join-Path $PSScriptRoot "blender_ai_mesh_input.py"

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
            $displayNameProperty = $entry.PSObject.Properties["DisplayName"]
            $installLocationProperty = $entry.PSObject.Properties["InstallLocation"]
            if ($null -eq $displayNameProperty -or
                $displayNameProperty.Value -notlike "Blender*") { continue }
            if ($null -ne $installLocationProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$installLocationProperty.Value)) {
                $candidates.Add((Join-Path ([string]$installLocationProperty.Value) "blender.exe"))
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
    if (-not (Test-Path -LiteralPath $rendererPath -PathType Leaf)) {
        throw "缺少 AI Mesh 输入渲染脚本：$rendererPath"
    }
    if ([string]::IsNullOrWhiteSpace($AssetId) -or
        $AssetId -notmatch "^[A-Za-z][A-Za-z0-9_]+$") {
        throw "AssetId 必须是稳定的 ASCII 标识符（字母开头，只含字母、数字和下划线）：$AssetId"
    }

    $blender = Resolve-BlenderExecutable -ExplicitPath $BlenderPath
    $resolvedSourceBlend = if ([string]::IsNullOrWhiteSpace($SourceBlend)) {
        Join-Path $projectPath "ArtPipelineOutput/BlenderTexturedProp/$AssetId/$AssetId.blend"
    }
    else {
        [System.IO.Path]::GetFullPath($SourceBlend)
    }
    if (-not (Test-Path -LiteralPath $resolvedSourceBlend -PathType Leaf)) {
        throw "源 .blend 不存在。请先运行 run-blender-textured-prop.ps1：$resolvedSourceBlend"
    }

    $resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        Join-Path $projectPath "ArtPipelineOutput/AIMeshInput"
    }
    else {
        [System.IO.Path]::GetFullPath($OutputRoot)
    }
    $assetOutput = Join-Path $resolvedOutputRoot $AssetId
    [System.IO.Directory]::CreateDirectory($assetOutput) | Out-Null

    Write-Host "[ai-mesh-input] Blender: $blender"
    Write-Host "[ai-mesh-input] Source: $resolvedSourceBlend"
    Write-Host "[ai-mesh-input] Output: $assetOutput"
    $processOutput = & $blender `
        --background $resolvedSourceBlend `
        --disable-autoexec `
        --python $rendererPath `
        -- `
        --output-dir $assetOutput `
        --asset-id $AssetId `
        --resolution $Resolution 2>&1
    $exitCode = $LASTEXITCODE
    $processOutput | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "Blender 以退出码 $exitCode 结束。"
    }

    $manifestPath = Join-Path $assetOutput "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "未产出 manifest：$manifestPath"
    }
    $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($manifest.status -ne "passed" -or
        $manifest.schemaVersion -ne 1 -or
        $manifest.harnessVersion -ne "0.2.0" -or
        $manifest.assetId -ne $AssetId) {
        throw "AI Mesh 输入 manifest 的状态、版本或资产 ID 不符合预期。"
    }
    if (-not $manifest.input.transparentBackground -or
        $manifest.input.groundVisible -or
        $manifest.input.width -ne $Resolution -or
        $manifest.input.height -ne $Resolution -or
        $manifest.input.alpha.min -gt 0.01 -or
        $manifest.input.alpha.max -lt 0.99) {
        throw "AI Mesh 输入的透明背景、地面或尺寸契约不成立。"
    }

    $inputPath = Join-Path $assetOutput $manifest.input.file
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "manifest 声明的输入文件不存在：$inputPath"
    }
    $actualInputHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualSourceHash = (Get-FileHash -LiteralPath $resolvedSourceBlend -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualInputHash -ne $manifest.input.sha256 -or
        $actualSourceHash -ne $manifest.sourceBlend.sha256) {
        throw "AI Mesh 输入或源 .blend 的 Hash 验证失败。"
    }

    Write-Host (
        "[ai-mesh-input] PASS: {0}x{1}, {2} bytes, visible alpha {3:P1}" -f `
            $manifest.input.width, `
            $manifest.input.height, `
            $manifest.input.bytes, `
            $manifest.input.alpha.visiblePixelRatio
    ) -ForegroundColor Green
    Write-Host "[ai-mesh-input] NOTE: 这是既有几何的可选参考夹具；若源 Mesh 已可生产，不应为复刻同一资产而上传。" -ForegroundColor Yellow
    Write-Host "[ai-mesh-input] Input: $inputPath"
    Write-Host "[ai-mesh-input] Manifest: $manifestPath"
    exit 0
}
catch {
    Write-Host "[ai-mesh-input] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
