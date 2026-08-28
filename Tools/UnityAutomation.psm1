Set-StrictMode -Version Latest

function Test-IsMacOSHost {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::OSX)
}

function Get-UnityProjectRequirement {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction Stop).Path
    $versionFile = Join-Path $resolvedProjectPath "ProjectSettings/ProjectVersion.txt"
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        throw "不是有效的 Unity 工程（缺少 ProjectSettings/ProjectVersion.txt）：$resolvedProjectPath"
    }

    $contents = [System.IO.File]::ReadAllLines($versionFile)
    $versionMatch = $contents | Select-String -Pattern '^m_EditorVersion:\s*(\S+)\s*$' | Select-Object -First 1
    $revisionMatch = $contents | Select-String -Pattern '^m_EditorVersionWithRevision:\s*\S+\s+\(([^)]+)\)\s*$' | Select-Object -First 1
    if ($null -eq $versionMatch) {
        throw "无法从 ProjectVersion.txt 读取 m_EditorVersion：$versionFile"
    }

    [pscustomobject]@{
        ProjectPath = $resolvedProjectPath
        Version = $versionMatch.Matches[0].Groups[1].Value
        Revision = if ($null -eq $revisionMatch) { "" } else { $revisionMatch.Matches[0].Groups[1].Value }
    }
}

function Assert-UnityEditorMatchesRequirement {
    param(
        [Parameter(Mandatory = $true)]
        [string]$EditorPath,
        [Parameter(Mandatory = $true)]
        [psobject]$Requirement
    )

    if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
        throw "Unity Editor 不存在：$EditorPath"
    }

    $resolvedPath = (Resolve-Path -LiteralPath $EditorPath).Path
    $productVersion = (Get-Item -LiteralPath $resolvedPath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        $pathSegments = $resolvedPath.Replace('\', '/').Split(
            @('/'), [System.StringSplitOptions]::RemoveEmptyEntries)
        if ($pathSegments -notcontains $Requirement.Version) {
            throw "当前平台不提供 Editor ProductVersion，且路径中没有项目要求的精确版本目录 $($Requirement.Version)：$resolvedPath"
        }
    }
    else {
        $expectedPrefix = [Regex]::Escape($Requirement.Version)
        if ($productVersion -notmatch "^$expectedPrefix(?:_|$)") {
            throw "Unity Editor 版本不匹配：项目要求 $($Requirement.Version)，但 $resolvedPath 是 $productVersion。"
        }

        if (-not [string]::IsNullOrWhiteSpace($Requirement.Revision) -and
            $productVersion -match '_' -and
            $productVersion -notmatch ([Regex]::Escape($Requirement.Revision))) {
            throw "Unity Editor revision 不匹配：项目要求 $($Requirement.Revision)，但 $resolvedPath 是 $productVersion。"
        }
    }

    return $resolvedPath
}

function Find-UnityCli {
    $configuredPath = $env:UNITY_CLI_PATH
    if (-not [string]::IsNullOrWhiteSpace($configuredPath)) {
        if (-not (Test-Path -LiteralPath $configuredPath -PathType Leaf)) {
            throw "UNITY_CLI_PATH 指向的文件不存在：$configuredPath"
        }

        return (Resolve-Path -LiteralPath $configuredPath).Path
    }

    $command = Get-Command unity -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) { return $null }
    return $command.Source
}

function Find-EditorThroughUnityCli {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CliPath,
        [Parameter(Mandatory = $true)]
        [psobject]$Requirement
    )

    try {
        $raw = & $CliPath editors path $Requirement.Version --json --non-interactive --no-banner 2>$null
        if ($LASTEXITCODE -ne 0 -or $null -eq $raw) { return $null }

        $response = ($raw | Out-String) | ConvertFrom-Json
        if ($null -eq $response -or -not $response.success -or [string]::IsNullOrWhiteSpace($response.data.path)) {
            return $null
        }

        $editorPath = if ($env:OS -eq 'Windows_NT') {
            Join-Path $response.data.path 'Editor/Unity.exe'
        }
        elseif (Test-IsMacOSHost) {
            Join-Path $response.data.path 'Unity.app/Contents/MacOS/Unity'
        }
        else {
            Join-Path $response.data.path 'Editor/Unity'
        }

        return Assert-UnityEditorMatchesRequirement -EditorPath $editorPath -Requirement $Requirement
    }
    catch {
        Write-Warning "Unity CLI 已安装，但无法用它解析项目 Editor：$($_.Exception.Message)"
        return $null
    }
}

function Find-EditorDirectly {
    param(
        [string]$ExplicitPath,
        [Parameter(Mandatory = $true)]
        [psobject]$Requirement
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Assert-UnityEditorMatchesRequirement -EditorPath $ExplicitPath -Requirement $Requirement
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:UNITY_EDITOR_PATH)) {
        $candidates.Add($env:UNITY_EDITOR_PATH)
    }

    if ($env:OS -eq 'Windows_NT') {
        $candidates.Add("C:\Program Files\Unity\Hub\Editor\$($Requirement.Version)\Editor\Unity.exe")

        if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
            $hubSecondaryConfig = Join-Path $env:APPDATA 'UnityHub/secondaryInstallPath.json'
            if (Test-Path -LiteralPath $hubSecondaryConfig -PathType Leaf) {
                try {
                    $hubSecondaryRoot = [System.IO.File]::ReadAllText($hubSecondaryConfig) | ConvertFrom-Json
                    if (-not [string]::IsNullOrWhiteSpace($hubSecondaryRoot)) {
                        $candidates.Add((Join-Path $hubSecondaryRoot "$($Requirement.Version)/Editor/Unity.exe"))
                    }
                }
                catch {
                    Write-Warning "无法读取 Unity Hub 次级安装目录配置：$($_.Exception.Message)"
                }
            }
        }

        foreach ($registryPath in @(
            "HKLM:\SOFTWARE\Unity Technologies\Installer\Unity $($Requirement.Version)",
            "HKLM:\SOFTWARE\WOW6432Node\Unity Technologies\Installer\Unity $($Requirement.Version)"
        )) {
            $install = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
            if ($null -eq $install) { continue }

            $location = $install.'Location x64'
            if ([string]::IsNullOrWhiteSpace($location)) { $location = $install.Location }
            if (-not [string]::IsNullOrWhiteSpace($location)) {
                $candidates.Add((Join-Path $location 'Editor/Unity.exe'))
            }
        }
    }
    else {
        $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        if (Test-IsMacOSHost) {
            $candidates.Add("/Applications/Unity/Hub/Editor/$($Requirement.Version)/Unity.app/Contents/MacOS/Unity")
        }
        else {
            $candidates.Add((Join-Path $userProfile "Unity/Hub/Editor/$($Requirement.Version)/Editor/Unity"))
        }
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        return Assert-UnityEditorMatchesRequirement -EditorPath $candidate -Requirement $Requirement
    }

    throw "找不到项目要求的 Unity $($Requirement.Version)。可安装新版 Unity CLI，或用 -UnityPath / UNITY_EDITOR_PATH 指定 Editor 完整路径。"
}

function Get-UnityAutomationEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,
        [string]$UnityPath = "",
        [ValidateSet('Auto', 'UnityCli', 'Direct')]
        [string]$Adapter = 'Auto'
    )

    $requirement = Get-UnityProjectRequirement -ProjectPath $ProjectPath
    $hasExplicitEditor = -not [string]::IsNullOrWhiteSpace($UnityPath) -or
                         -not [string]::IsNullOrWhiteSpace($env:UNITY_EDITOR_PATH)
    if ($Adapter -eq 'UnityCli' -and $hasExplicitEditor) {
        throw "UnityCli Adapter 与 -UnityPath / UNITY_EDITOR_PATH 不能同时使用；请移除显式 Editor 路径，或选择 Direct。"
    }

    if ($Adapter -ne 'Direct' -and -not $hasExplicitEditor) {
        $cliPath = Find-UnityCli
        if ($null -ne $cliPath) {
            $editorPath = Find-EditorThroughUnityCli -CliPath $cliPath -Requirement $requirement
            if ($null -ne $editorPath) {
                return [pscustomobject]@{
                    Adapter = 'UnityCli'
                    CliPath = $cliPath
                    LauncherPath = $cliPath
                    EditorPath = $editorPath
                    ProjectPath = $requirement.ProjectPath
                    Version = $requirement.Version
                    Revision = $requirement.Revision
                }
            }
        }

        if ($Adapter -eq 'UnityCli') {
            throw "Unity CLI 不可用，或未登记项目要求的 Unity $($requirement.Version)。运行 'unity editors --installed' 检查安装状态。"
        }
    }

    $editorPath = Find-EditorDirectly -ExplicitPath $UnityPath -Requirement $requirement
    return [pscustomobject]@{
        Adapter = 'Direct'
        CliPath = $null
        LauncherPath = $editorPath
        EditorPath = $editorPath
        ProjectPath = $requirement.ProjectPath
        Version = $requirement.Version
        Revision = $requirement.Revision
    }
}

function ConvertTo-QuotedProcessArgument {
    param([AllowEmptyString()][string]$Argument)

    $escaped = $Argument -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Assert-UnityProjectNotLocked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $lockFile = Join-Path $ProjectPath 'Temp/UnityLockfile'
    if (Test-Path -LiteralPath $lockFile) {
        throw "工程正被 Unity Editor 占用（$lockFile 存在）。请先关闭该工程；只有确认是崩溃残留时才手动删除锁文件。"
    }
}

function Remove-UnityCliOwnedArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$EditorArguments
    )

    $forwarded = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $EditorArguments.Count; $index++) {
        $argument = $EditorArguments[$index]
        if ($argument -ieq '-batchmode' -or $argument -ieq '-quit') {
            continue
        }

        if ($argument -ieq '-projectPath') {
            if ($index + 1 -ge $EditorArguments.Count) {
                throw "-projectPath 缺少路径参数。"
            }

            $index++
            continue
        }

        $forwarded.Add($argument)
    }

    return $forwarded.ToArray()
}

function New-UnityCliRunArguments {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Environment,
        [Parameter(Mandatory = $true)]
        [string[]]$EditorArguments
    )

    $forwardedArguments = Remove-UnityCliOwnedArguments -EditorArguments $EditorArguments
    return @(
        'run',
        $Environment.ProjectPath,
        '--editor-path', $Environment.EditorPath,
        '--non-interactive',
        '--no-banner',
        '--'
    ) + $forwardedArguments
}

function New-UnityCliTestArguments {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Environment,
        [Parameter(Mandatory = $true)]
        [string]$Platform,
        [Parameter(Mandatory = $true)]
        [string]$ResultsPath,
        [Parameter(Mandatory = $true)]
        [string]$LogPath
    )

    return @(
        'test',
        $Environment.ProjectPath,
        '--mode', $Platform,
        '--output', $ResultsPath,
        '--editor-path', $Environment.EditorPath,
        '--non-interactive',
        '--no-banner',
        '--',
        '-nographics',
        '-logFile', $LogPath
    )
}

function Invoke-UnityEditor {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Environment,
        [Parameter(Mandatory = $true)]
        [string[]]$EditorArguments
    )

    Assert-UnityProjectNotLocked -ProjectPath $Environment.ProjectPath

    if ($Environment.Adapter -eq 'UnityCli') {
        if ($EditorArguments -icontains '-runTests') {
            throw "Unity CLI 的通用 run 命令不能可靠承载 -runTests；请改用 Invoke-UnityTests。"
        }

        $cliArguments = New-UnityCliRunArguments -Environment $Environment -EditorArguments $EditorArguments

        & $Environment.LauncherPath @cliArguments | Out-Host
        $exitCode = $LASTEXITCODE
    }
    else {
        $quotedArguments = $EditorArguments | ForEach-Object { ConvertTo-QuotedProcessArgument -Argument $_ }
        $process = Start-Process -FilePath $Environment.EditorPath -ArgumentList $quotedArguments -PassThru -Wait -NoNewWindow
        $exitCode = $process.ExitCode
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Adapter = $Environment.Adapter
        EditorPath = $Environment.EditorPath
        Version = $Environment.Version
    }
}

function Invoke-UnityTests {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Environment,
        [Parameter(Mandatory = $true)]
        [ValidateSet('EditMode', 'PlayMode')]
        [string]$Platform,
        [Parameter(Mandatory = $true)]
        [string]$ResultsPath,
        [Parameter(Mandatory = $true)]
        [string]$LogPath
    )

    Assert-UnityProjectNotLocked -ProjectPath $Environment.ProjectPath

    $resolvedResultsPath = if ([System.IO.Path]::IsPathRooted($ResultsPath)) {
        [System.IO.Path]::GetFullPath($ResultsPath)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $Environment.ProjectPath $ResultsPath))
    }
    $resolvedLogPath = if ([System.IO.Path]::IsPathRooted($LogPath)) {
        [System.IO.Path]::GetFullPath($LogPath)
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path $Environment.ProjectPath $LogPath))
    }

    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedResultsPath)) | Out-Null
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedLogPath)) | Out-Null
    if (Test-Path -LiteralPath $resolvedResultsPath) {
        Remove-Item -LiteralPath $resolvedResultsPath -Force
    }
    if (Test-Path -LiteralPath $resolvedLogPath) {
        Remove-Item -LiteralPath $resolvedLogPath -Force
    }

    if ($Environment.Adapter -eq 'UnityCli') {
        $cliArguments = New-UnityCliTestArguments `
            -Environment $Environment `
            -Platform $Platform `
            -ResultsPath $resolvedResultsPath `
            -LogPath $resolvedLogPath

        & $Environment.LauncherPath @cliArguments | Out-Host
        $exitCode = $LASTEXITCODE
    }
    else {
        $editorArguments = @(
            '-batchmode',
            '-projectPath', $Environment.ProjectPath,
            '-runTests',
            '-testPlatform', $Platform,
            '-testResults', $resolvedResultsPath,
            '-logFile', $resolvedLogPath
        )
        $invocation = Invoke-UnityEditor -Environment $Environment -EditorArguments $editorArguments
        $exitCode = $invocation.ExitCode
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Adapter = $Environment.Adapter
        EditorPath = $Environment.EditorPath
        Version = $Environment.Version
        ResultsPath = $resolvedResultsPath
        LogPath = $resolvedLogPath
    }
}

Export-ModuleMember -Function Get-UnityProjectRequirement, Get-UnityAutomationEnvironment, Invoke-UnityEditor, Invoke-UnityTests
