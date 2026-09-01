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
    if ($manifest.schemaVersion -ne 3 -or $manifest.harnessVersion -ne "0.4.0") {
        throw "manifest schema 或 Harness 版本不符合当前入口。"
    }
    if ($manifest.geometry.meshObjectCount -ne 3 -or $manifest.geometry.materialSlotCount -ne 3) {
        throw "按材质合并失败：预期 3 Mesh / 3 Material Slot。"
    }
    if (-not $manifest.acceptance.textureSetComplete) {
        throw "PBR 贴图集不完整。"
    }
    if (-not $manifest.acceptance.contactSheetRendered -or
        -not $manifest.acceptance.sourceTopologyAndUvVerified -or
        -not $manifest.acceptance.fbxTopologyAndUvVerified) {
        throw "Contact Sheet、拓扑或 UV 证据未通过。"
    }
    if ($manifest.visualEvidence.contactSheet.width -ne 1536 -or
        $manifest.visualEvidence.contactSheet.height -ne 1024 -or
        $manifest.visualEvidence.contactSheet.columns -ne 3 -or
        $manifest.visualEvidence.contactSheet.rows -ne 2 -or
        $manifest.visualEvidence.contactSheet.panels.Count -ne 6 -or
        ($manifest.visualEvidence.contactSheet.panels -join ",") -ne "hero,front,side,top,wireframe,uv-checker" -or
        -not $manifest.visualEvidence.contactSheet.manualReviewRequired) {
        throw "Contact Sheet 布局或人工复核边界不符合预期。"
    }
    $sourceTopology = $manifest.geometry.quality.topology
    $sourceUv = $manifest.geometry.quality.uv
    $roundTripTopology = $manifest.geometry.fbxRoundTrip.quality.topology
    $roundTripUv = $manifest.geometry.fbxRoundTrip.quality.uv
    if ($sourceTopology.looseVertexCount -ne 0 -or
        $sourceTopology.looseEdgeCount -ne 0 -or
        $sourceTopology.boundaryEdgeCount -ne 0 -or
        $sourceTopology.nonManifoldEdgeCount -ne 0 -or
        $roundTripTopology.looseVertexCount -ne 0 -or
        $roundTripTopology.looseEdgeCount -ne 0 -or
        $roundTripTopology.boundaryEdgeCount -ne 0 -or
        $roundTripTopology.nonManifoldEdgeCount -ne 0 -or
        -not $sourceUv.allMeshesHaveActiveUv -or
        -not $roundTripUv.allMeshesHaveActiveUv -or
        $sourceUv.degenerateUvTriangleCount -ne 0 -or
        $roundTripUv.degenerateUvTriangleCount -ne 0 -or
        $sourceUv.outOfUnitRangeLoopCount -ne 0 -or
        $roundTripUv.outOfUnitRangeLoopCount -ne 0 -or
        $sourceUv.policy -ne "overlap-and-repeat-allowed" -or
        $roundTripUv.policy -ne "overlap-and-repeat-allowed" -or
        $sourceUv.textureResolution -ne $TextureSize -or
        $roundTripUv.textureResolution -ne $TextureSize -or
        $sourceUv.areaWeightedTexelDensityPxPerMeter -le 0 -or
        $roundTripUv.areaWeightedTexelDensityPxPerMeter -le 0 -or
        [Math]::Abs(
            $sourceUv.areaWeightedTexelDensityPxPerMeter -
            $roundTripUv.areaWeightedTexelDensityPxPerMeter
        ) -gt 0.01) {
        throw "来源或 FBX 回读的拓扑 / UV / Texel Density 契约不成立。"
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
        "[blender-textured-prop] PASS: {0} source parts -> {1} meshes / {2} vertices / {3} triangles / {4} texture files / {5} px/m" -f `
            $manifest.geometry.sourcePartCount, `
            $manifest.geometry.meshObjectCount, `
            $manifest.geometry.vertexCount, `
            $manifest.geometry.triangleCount, `
            ($manifest.materials.Count * 4), `
            $manifest.geometry.quality.uv.areaWeightedTexelDensityPxPerMeter
    ) -ForegroundColor Green
    Write-Host "[blender-textured-prop] 预览: $(Join-Path $assetOutput ($AssetId + '_preview.png'))"
    Write-Host "[blender-textured-prop] Contact Sheet: $(Join-Path $assetOutput ($AssetId + '_contact_sheet.png'))"
    exit 0
}
catch {
    Write-Host "[blender-textured-prop] ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
