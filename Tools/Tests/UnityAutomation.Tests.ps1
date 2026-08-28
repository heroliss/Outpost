param(
    [string]$ProjectPath = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'UnityAutomation.psm1') -Force
$module = Get-Module UnityAutomation
$assertions = 0

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) { throw $Message }
    $script:assertions++
}

$requirement = Get-UnityProjectRequirement -ProjectPath $ProjectPath
Assert-True ($requirement.Version -match '^\d+\.\d+\.\d+[a-z]\d+$') 'ProjectVersion was not parsed.'

$direct = Get-UnityAutomationEnvironment -ProjectPath $ProjectPath -Adapter Direct
Assert-True ($direct.Adapter -eq 'Direct') 'Direct adapter was not selected.'
Assert-True ($direct.Version -eq $requirement.Version) 'Direct adapter resolved the wrong version.'

$auto = Get-UnityAutomationEnvironment -ProjectPath $ProjectPath -Adapter Auto
Assert-True ($auto.Version -eq $requirement.Version) 'Auto adapter resolved the wrong version.'
Assert-True ($auto.Adapter -in @('UnityCli', 'Direct')) 'Auto adapter returned an unknown adapter.'

$cliEnvironment = [pscustomobject]@{
    Adapter = 'UnityCli'
    LauncherPath = 'unity'
    EditorPath = 'X:\Unity\Editor\Unity.exe'
    ProjectPath = (Join-Path ([System.IO.Path]::GetTempPath()) 'Project With Spaces')
    Version = $requirement.Version
}

$runArguments = & $module {
    New-UnityCliRunArguments `
        -Environment $args[0] `
        -EditorArguments @('-batchmode', '-quit', '-projectPath', $args[0].ProjectPath, '-nographics', '-executeMethod', 'Build.Run')
} $cliEnvironment
Assert-True ($runArguments[0] -eq 'run') 'CLI run command was not selected.'
Assert-True ($runArguments -notcontains '-batchmode') 'CLI-owned -batchmode was forwarded.'
Assert-True ($runArguments -notcontains '-quit') 'CLI-owned -quit was forwarded.'
Assert-True ($runArguments -notcontains '-projectPath') 'CLI-owned -projectPath was forwarded.'
Assert-True ($runArguments -contains '-executeMethod') 'Custom Editor arguments were lost.'

$testArguments = & $module {
    New-UnityCliTestArguments `
        -Environment $args[0] `
        -Platform 'EditMode' `
        -ResultsPath 'X:\Project With Spaces\Logs\results.xml' `
        -LogPath 'X:\Project With Spaces\Logs\editor.log'
} $cliEnvironment
Assert-True ($testArguments[0] -eq 'test') 'CLI test command was not selected.'
Assert-True ($testArguments -contains '--mode') 'CLI test mode was not mapped.'
Assert-True ($testArguments -contains '--output') 'CLI test result path was not mapped.'
Assert-True ($testArguments -notcontains '-runTests') 'Legacy -runTests leaked into unity test.'

$quoted = & $module { ConvertTo-QuotedProcessArgument -Argument 'X:\Path With Spaces\' }
Assert-True ($quoted -eq '"X:\Path With Spaces\\"') 'Trailing backslash was not quoted for Windows command-line parsing.'

try {
    Invoke-UnityEditor -Environment $cliEnvironment -EditorArguments @('-runTests') | Out-Null
    throw 'Generic unity run accepted -runTests.'
}
catch {
    Assert-True ($_.Exception.Message -like '*Invoke-UnityTests*') 'Generic -runTests guard returned the wrong error.'
}

$testRunId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "SSFramework-UnityAutomationTests-$testRunId"
if ([System.IO.Directory]::Exists($testRoot)) {
    throw "Refusing to reuse an existing test root: $testRoot"
}
$lockedProject = Join-Path $testRoot 'LockedProject'
$lockDirectory = Join-Path $lockedProject 'Temp'
$evidenceDirectory = Join-Path $lockedProject 'Logs'
$lockedResults = Join-Path $evidenceDirectory 'results.xml'
$lockedLog = Join-Path $evidenceDirectory 'editor.log'
[System.IO.Directory]::CreateDirectory($lockDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
[System.IO.File]::WriteAllText((Join-Path $lockDirectory 'UnityLockfile'), 'locked')
[System.IO.File]::WriteAllText($lockedResults, 'previous results')
[System.IO.File]::WriteAllText($lockedLog, 'previous log')
try {
    $lockedEnvironment = [pscustomobject]@{
        Adapter = 'Direct'
        LauncherPath = 'not-used'
        EditorPath = 'not-used'
        ProjectPath = $lockedProject
        Version = $requirement.Version
    }
    try {
        Invoke-UnityTests `
            -Environment $lockedEnvironment `
            -Platform EditMode `
            -ResultsPath $lockedResults `
            -LogPath $lockedLog | Out-Null
        throw 'Locked project was accepted.'
    }
    catch {
        Assert-True ($_.Exception.Message -like '*UnityLockfile*') 'Locked project guard returned the wrong error.'
    }
    Assert-True ([System.IO.File]::ReadAllText($lockedResults) -eq 'previous results') 'Lock rejection deleted previous results.'
    Assert-True ([System.IO.File]::ReadAllText($lockedLog) -eq 'previous log') 'Lock rejection deleted previous log.'
}
finally {
    if ([System.IO.Directory]::Exists($testRoot)) {
        [System.IO.Directory]::Delete($testRoot, $true)
    }
}

if ($env:OS -eq 'Windows_NT') {
    try {
        Get-UnityAutomationEnvironment `
            -ProjectPath $ProjectPath `
            -UnityPath "$env:WINDIR\System32\notepad.exe" `
            -Adapter Direct | Out-Null
        throw 'Wrong Editor binary was accepted.'
    }
    catch {
        Assert-True ($_.Exception.Message -like '*版本不匹配*') 'Wrong Editor version guard returned the wrong error.'
    }
}

Write-Output "UnityAutomation contract tests passed: $assertions assertions."
