$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'UnityTestEvidence.psm1') -Force
$assertions = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:assertions++
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-True $rejected $Message
}
function Clone($Value) { return $Value | ConvertTo-Json -Depth 20 | ConvertFrom-Json }

$fixture = 'Game.Tests.Route+Driver'
$discovery = [pscustomobject]@{ success = $true; data = [pscustomobject]@{
    mode = 'EditMode'; truncated = $false; totalTests = 3; tests = @(
        [pscustomobject]@{ fullName = "$fixture.Move(137,`"a+b`",1.0f)"; runState = 'Runnable' },
        [pscustomobject]@{ fullName = "$fixture.Cancel"; runState = 'Runnable' },
        [pscustomobject]@{ fullName = "$($fixture)Other.Cancel"; runState = 'Runnable' }
    )
} }
$plan = New-UnityMcpTestPlan -Discovery $discovery -Mode EditMode -Fixture $fixture
Assert-True ($plan.expectedCount -eq 2) '精确 fixture 不应吞入名字相似的另一个 fixture。'
Assert-True ($plan.runnerParameters.testNames -ccontains $discovery.data.tests[0].fullName) '参数化用例身份被当作正则改变。'
Assert-True ($null -eq $plan.runnerParameters.PSObject.Properties['filter']) '不应再次生成未生效的 filter 别名。'
$snapshot = $discovery | ConvertTo-Json -Depth 20
Assert-Rejected { New-UnityMcpTestPlan $discovery PlayMode $fixture } '错误 mode 被放行。'
Assert-Rejected { New-UnityMcpTestPlan $discovery EditMode 'Missing' } '空 fixture 被放行。'
$bad = Clone $discovery; $bad.data.truncated = $true
Assert-Rejected { New-UnityMcpTestPlan $bad EditMode $fixture } '截断发现被放行。'
$bad = Clone $discovery; $bad.data.tests[0].runState = 'Explicit'
Assert-Rejected { New-UnityMcpTestPlan $bad EditMode $fixture } '不可运行用例被放行。'
$bad = Clone $discovery; $bad.data.tests[1].fullName = $bad.data.tests[0].fullName
Assert-Rejected { New-UnityMcpTestPlan $bad EditMode $fixture } '重复身份被放行。'
Assert-True (($discovery | ConvertTo-Json -Depth 20) -ceq $snapshot) '计划函数修改了发现源数据。'

$job = [pscustomobject]@{ success = $true; data = [pscustomobject]@{
    jobId = 'owned-job'; mode = 'EditMode'; status = 'succeeded'
    summary = [pscustomobject]@{ total = 2; passed = 2; failed = 0; skipped = 0; duration = 0.02 }
    tests = @($plan.expectedTests | ForEach-Object { [pscustomobject]@{ fullName = $_; status = 'Passed' } })
} }
$result = Test-UnityMcpTestEvidence $plan $job 'owned-job'
Assert-True ($result.verdict -ceq 'Passed' -and $result.actual -eq 2) '完整一致证据未通过。'
Assert-Rejected { Test-UnityMcpTestEvidence $plan $job 'another-job' } '旧 job 被误认成本轮。'
$bad = Clone $job; $bad.data.mode = 'PlayMode'
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '执行 mode 漂移未被拒绝。'
$bad = Clone $job; $bad.data.status = 'running'
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '启动中的 job 被当作通过。'
$bad = Clone $job; $bad.data.tests = @()
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '仅有 summary 的绿灯被当作充分证据。'
$bad = Clone $job; $bad.data.tests[1].fullName = 'WrongFixture.Passed'
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '数量相同但身份错误的结果被放行。'
$bad = Clone $job; $bad.data.tests[1].fullName = $bad.data.tests[0].fullName
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '重复结果被误认成全部用例。'
$bad = Clone $job; $bad.data.summary.passed = 1
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '汇总与明细矛盾被放行。'
$bad = Clone $job; $bad.data.summary.total = 0; $bad.data.tests = @()
Assert-Rejected { Test-UnityMcpTestEvidence $plan $bad 'owned-job' } '零测试终态被放行。'
$bad = Clone $job; $bad.data.tests[0].status = 'Failed'; $bad.data.summary.passed = 1; $bad.data.summary.failed = 1; $bad.data.status = 'failed'
Assert-True ((Test-UnityMcpTestEvidence $plan $bad 'owned-job').verdict -ceq 'Failed') '真实测试失败被冒充为 Passed。'
$bad = Clone $job; $bad.data.tests[0].status = 'Skipped'; $bad.data.summary.passed = 1; $bad.data.summary.skipped = 1
Assert-True ((Test-UnityMcpTestEvidence $plan $bad 'owned-job').verdict -ceq 'Failed') '跳过用例被冒充为全部通过。'
Write-Output "UnityTestEvidence contract tests passed: $assertions assertions."
