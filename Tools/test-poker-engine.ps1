param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Fixtures)
$ErrorActionPreference = 'Stop'
$toolkitRoot = Split-Path $PSScriptRoot -Parent
Push-Location $toolkitRoot
try {
    $runtime = $env:POKER_DOTNET
    if (-not $runtime) {
        $localRuntime = Join-Path $toolkitRoot '.runtime/dotnet/dotnet.exe'
        if (Test-Path $localRuntime) { $runtime = $localRuntime } else { $runtime = 'dotnet' }
    }
    $arguments = @('test', 'Tests/BattleToolkit.Tests.csproj', '--configuration', 'Release', '--logger', 'console;verbosity=normal')
    if ($Fixtures.Count -gt 0) {
        $arguments += @('--filter', (($Fixtures | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'))
    }
    & $runtime @arguments
    $result = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $result
