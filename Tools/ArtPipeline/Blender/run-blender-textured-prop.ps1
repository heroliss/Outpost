<#
.SYNOPSIS
  生成并验证带确定性 PBR 贴图、按材质合并的 Blender 静态设施探针。

.DESCRIPTION
  不修改 Blender 用户偏好、不安装扩展，也不直接写 Unity Assets。
  默认输出到 ArtPipelineOutput/BlenderTexturedProp/<AssetId>/（已忽略）。
#>
param(
    [string]$BlenderPath = "",
    [string]$OutputRoot = "",
    [string]$AssetId = "NW_WaterRecycler_01",
    [ValidateSet(256, 512, 1024, 2048)]
    [int]$TextureSize = 512
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectPath = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$generatorPath = Join-Path $PSScriptRoot "blender_textured_prop.py"

function Resolve-BlenderExecutable {
    param([string]$ExplicitPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($ExplicitPath, $env:SSFRAMEWORK_BLENDER_PATH, $env:SSFRAMEWORK_BLENDER)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) { $candidates.Add($candidate) }
    }

    $command = Get-Command blender -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
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
            if ($null -eq $displayNameProperty -or $displayNameProperty.Value -notlike "Blender*") { continue }
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
    if (-not (Test-Path -LiteralPath $generatorPath -PathType Leaf)) {
        throw "缺少 Blender 生成脚本：$generatorPath"
    }
    if ([string]::IsNullOrWhiteSpace($AssetId) -or $AssetId -notmatch "^[A-Za-z][A-Za-z0-9_]+$") {
        throw "AssetId 必须是稳定的 ASCII 标识符（字母开头，只含字母、数字和下划线）：$AssetId"
    }

    $blender = Resolve-BlenderExecutable -ExplicitPath $BlenderPath
    $resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        Join-Path $projectPath "ArtPipelineOutput/BlenderTexturedProp"
    }
    else {
        [System.IO.Path]::GetFullPath($OutputRoot)
    }
    $assetOutput = Join-Path $resolvedOutputRoot $AssetId
    [System.IO.Directory]::CreateDirectory($assetOutput) | Out-Null

    Write-Host "[blender-textured-prop] Blender: $blender"
    Write-Host "[blender-textured-prop] 输出: $assetOutput"
    $processOutput = & $blender `
        --background `
        --factory-startup `
        --python $generatorPath `
        -- `
        --output-dir $assetOutput `
        --asset-id $AssetId `
        --texture-size $TextureSize 2>&1
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
    if ($manifest.status -ne "passed" -or $manifest.asset.id -ne $AssetId) {
        throw "manifest 状态或资产 ID 不符合预期。"
    }
    if ($manifest.schemaVersion -ne 2 -or $manifest.harnessVersion -ne "0.3.0") {
        throw "manifest schema 或 Harness 版本不符合当前入口。"
    }
    if ($manifest.geometry.meshObjectCount -ne 3 -or $manifest.geometry.materialSlotCount -ne 3) {
        throw "按材质合并失败：预期 3 Mesh / 3 Material Slot。"
    }
    if (-not $manifest.acceptance.textureSetComplete) {
        throw "PBR 贴图集不完整。"
    }
    if (-not $manifest.acceptance.fbxRoundTripVerified -or
        $manifest.geometry.fbxRoundTrip.meshObjectCount -ne 3 -or
        $manifest.geometry.fbxRoundTrip.materialSlotCount -ne 3 -or
        $manifest.geometry.fbxRoundTrip.triangleCount -ne $manifest.geometry.triangleCount -or
        $manifest.geometry.degenerateTriangleCount -ne 0 -or
        $manifest.geometry.fbxRoundTrip.degenerateTriangleCount -ne 0) {
        throw "FBX 导出后回读证据不符合跨工具几何契约。"
    }

    foreach ($file in $manifest.files) {
        $filePath = Join-Path $assetOutput $file.name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "manifest 声明的文件不存在：$filePath"
        }
        $actualLength = (Get-Item -LiteralPath $filePath).Length
        if ($actualLength -le 0 -or $actualLength -ne [long]$file.bytes) {
            throw "文件大小验证失败：$filePath"
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $file.sha256) {
            throw "文件哈希验证失败：$filePath"
        }
    }

    Write-Host (
        "[blender-textured-prop] PASS: {0} source parts -> {1} meshes / {2} vertices / {3} triangles / {4} texture files" -f `
            $manifest.geometry.sourcePartCount, `
            $manifest.geometry.meshObjectCount, `
            $manifest.geometry.vertexCount, `
            $manifest.geometry.triangleCount, `
            ($manifest.materials.Count * 4)
    ) -ForegroundColor Green
    Write-Host "[blender-textured-prop] 预览: $(Join-Path $assetOutput ($AssetId + '_preview.png'))"
    exit 0
}
catch {
    Write-Host "[blender-textured-prop] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
