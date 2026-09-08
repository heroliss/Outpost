<#
.SYNOPSIS
只读检查项目 AGENTS 链预算和指定入口文档的相对文件行内链接。
.DESCRIPTION
默认只查轻量项目索引，不拼接全部设计文档。输出可保存为 JSON；越过预算、
缺失文档或断链时抛错。只验证相对链接的文件/目录存在；忽略远程地址和带 scheme
的绝对地址，不声称理解章节、重复事实或状态新旧。
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]]$Documents = @('docs/ai-project-index.md'),
    [ValidateRange(1, 2147483647)][int]$BudgetBytes = 32768,
    [ValidateRange(1, 2147483647)][int]$WarningBytes = 28672
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path.TrimEnd([char[]]'\/')
$issues = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

# rg 遵循仓库忽略文件，避免把 Library / 产物中的第三方指令算入项目预算。
$agentPaths = @(& rg --files --hidden -g AGENTS.md -g '!.git' $root)
if ($LASTEXITCODE -ne 0) { throw '未能发现项目 AGENTS.md，请检查 rg 与仓库路径。' }
$agents = @($agentPaths | ForEach-Object {
    $absolute = [IO.Path]::GetFullPath($_)
    [pscustomobject]@{
        path = $absolute
        directory = [IO.Path]::GetDirectoryName($absolute)
        bytes = [Text.Encoding]::UTF8.GetByteCount([IO.File]::ReadAllText($absolute))
    }
})
$chains = @($agents | ForEach-Object {
    $leaf = $_
    $parents = @($agents | Where-Object {
        $leaf.directory.Equals($_.directory, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf.directory.StartsWith($_.directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
    } | Sort-Object { $_.directory.Length })
    $total = ($parents | Measure-Object -Property bytes -Sum).Sum
    $relative = $leaf.path.Substring($root.Length + 1).Replace('\', '/')
    if ($total -gt $BudgetBytes) { $issues.Add("AGENTS 链超出预算：$relative = $total / $BudgetBytes bytes") }
    elseif ($total -ge $WarningBytes) { $warnings.Add("AGENTS 链接近预算：$relative = $total / $BudgetBytes bytes") }
    [pscustomobject]@{ leaf = $relative; bytes = $total; remaining = $BudgetBytes - $total }
})

$checkedLinks = 0
foreach ($document in $Documents) {
    $source = [IO.Path]::GetFullPath((Join-Path $root $document))
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        $issues.Add("入口文档不存在：$document")
        continue
    }
    $markdown = [IO.File]::ReadAllText($source)
    # 只读普通行内链接；跳过示例代码块，引用式链接与章节锚点留给文档审查。
    $markdown = [regex]::Replace($markdown, '(?ms)^\s*```.*?^\s*```[^\r\n]*', '')
    foreach ($match in [regex]::Matches($markdown, '\[[^\]\r\n]*\]\((?:<(?<target>[^>]+)>|(?<target>[^\s)]+))\)')) {
        $target = $match.Groups['target'].Value
        if ($target -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|#|//)') { continue }
        $local = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if ([string]::IsNullOrWhiteSpace($local)) { continue }
        $resolved = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($source)) $local))
        $checkedLinks++
        if (-not (Test-Path -LiteralPath $resolved)) { $issues.Add("断链：$document -> $target") }
    }
}

[pscustomobject]@{
    passed = $issues.Count -eq 0
    budgetBytes = $BudgetBytes
    chains = $chains
    documents = $Documents
    checkedLocalLinks = $checkedLinks
    warnings = @($warnings.ToArray())
    errors = @($issues.ToArray())
}
if ($issues.Count -gt 0) { throw ($issues -join [Environment]::NewLine) }
