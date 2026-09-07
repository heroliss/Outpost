Set-StrictMode -Version Latest

function Get-McpPayload {
    param([Parameter(Mandatory = $true)]$Response)
    if (-not $Response.success) { throw 'MCP 返回失败，不能把它当成测试证据。' }
    return $Response.data
}

function Get-OrdinalNameSet {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Names)
    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name) -or -not $set.Add($name)) {
            throw "测试名单含空身份或重复身份：'$name'。"
        }
    }
    return ,$set
}

function New-UnityMcpTestPlan {
    <#
    .SYNOPSIS
    从当前 mode 的完整发现结果生成精确 fixture 请求；不执行 Unity 或保存场景。
    .DESCRIPTION
    Discovery 是 unity_testing_list_tests 的 JSON 对象（success/data 层），不是 MCP content 包装。
    Fixture 是完整类型名，按 Ordinal 前缀匹配其叶用例；不使用正则或 filter 别名。
    拒绝截断、零测试、不可运行或重复身份，避免错误范围进入 Runner。
    #>
    param(
        [Parameter(Mandatory = $true)]$Discovery,
        [Parameter(Mandatory = $true)][ValidateSet('EditMode', 'PlayMode')][string]$Mode,
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Fixture
    )
    $data = Get-McpPayload $Discovery
    if ($data.mode -cne $Mode) { throw '测试发现 mode 与请求不一致。' }
    if ($data.truncated) { throw '测试发现已截断，请缩小 nameFilter 或提高 maxResults 后重新发现。' }
    if (@($data.tests).Count -ne $data.totalTests) { throw '测试发现数量与名单不一致。' }
    $selected = @($data.tests | Where-Object { $_.fullName.StartsWith($Fixture + '.', [StringComparison]::Ordinal) })
    if ($selected.Count -eq 0) { throw "未发现 fixture 的叶用例：$Fixture" }
    if (@($selected | Where-Object { $_.runState -cne 'Runnable' }).Count -gt 0) {
        throw '选中范围含不可运行用例；先明确 Ignore / Explicit 等状态再安排运行。'
    }
    [string[]]$names = @($selected.fullName | Sort-Object -CaseSensitive)
    $null = Get-OrdinalNameSet $names
    return [pscustomobject]@{
        schemaVersion = 1
        fixture = $Fixture
        mode = $Mode
        expectedCount = $names.Count
        expectedTests = $names
        runnerParameters = [pscustomobject]@{ mode = $Mode; testNames = $names }
    }
}

function Test-UnityMcpTestEvidence {
    <#
    .SYNOPSIS
    逐项比较计划与同一个已结束 job，返回已核验摘要；证据缺失或范围漂移时抛错。
    .DESCRIPTION
    Job 取自 get_job(includeDetails:true, includeFailedOnly:false)。调用方保存真实 dispatch 返回的
    JobId；本函数不接受只含总数的绿灯，不启动/重跑 job，也不修改源证据。
    证据完整且用例失败时返回 Failed；只有全部预期叶用例恰好执行一次且 Passed 才返回 Passed。
    #>
    param(
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)]$Job,
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$JobId
    )
    if ($Plan.schemaVersion -ne 1) { throw '不支持的测试计划版本。' }
    $data = Get-McpPayload $Job
    if ($data.jobId -cne $JobId) { throw '结果 job id 与本次实际 dispatch 不一致。' }
    if ($data.mode -cne $Plan.mode) { throw '实际执行 mode 与计划不一致。' }
    if ($data.status -cnotin @('succeeded', 'failed')) { throw "测试尚无可验收终态：$($data.status)。" }
    [string[]]$expectedNames = @($Plan.expectedTests)
    if ($expectedNames.Count -eq 0 -or $expectedNames.Count -ne $Plan.expectedCount) { throw '预期测试名单为空或数量矛盾。' }
    $expected = Get-OrdinalNameSet $expectedNames
    $actualTests = @($data.tests)
    if ($actualTests.Count -eq 0 -or $actualTests.Count -ne $data.summary.total) {
        throw '终态缺少完整逐用例明细；不要只根据 summary 宣称通过。请取得完整证据，不自动重跑。'
    }
    [string[]]$actualNames = @($actualTests.fullName)
    $actual = Get-OrdinalNameSet $actualNames
    if (-not $expected.SetEquals($actual)) {
        $missing = @($expectedNames | Where-Object { -not $actual.Contains($_) })
        $extra = @($actualNames | Where-Object { -not $expected.Contains($_) })
        throw "测试范围漂移；缺少：[$($missing -join ', ')]；额外：[$($extra -join ', ')]。"
    }
    $passed = @($actualTests | Where-Object { $_.status -ceq 'Passed' }).Count
    $failed = @($actualTests | Where-Object { $_.status -cin @('Failed', 'Inconclusive') }).Count
    $skipped = @($actualTests | Where-Object { $_.status -ceq 'Skipped' }).Count
    if ($passed + $failed + $skipped -ne $actualTests.Count -or
        $data.summary.passed -ne $passed -or $data.summary.failed -ne $failed -or $data.summary.skipped -ne $skipped) {
        throw '逐用例结果与汇总计数不一致或含未知状态。'
    }
    $verdict = if ($data.status -ceq 'succeeded' -and $passed -eq $expected.Count) { 'Passed' } else { 'Failed' }
    return [pscustomobject]@{
        verdict = $verdict
        jobId = $JobId
        mode = $Plan.mode
        fixture = $Plan.fixture
        expected = $expected.Count
        actual = $actual.Count
        passed = $passed
        failed = $failed
        skipped = $skipped
        duration = $data.summary.duration
    }
}

Export-ModuleMember -Function New-UnityMcpTestPlan, Test-UnityMcpTestEvidence
