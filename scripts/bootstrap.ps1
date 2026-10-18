# Windows PowerShell 5.1 compatible. This file is UTF-8 with BOM for Chinese paths/messages.
$ErrorActionPreference = 'Stop'
$toolkitRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$runtimeDir = Join-Path $toolkitRoot '.runtime'
$sdkDir = Join-Path $runtimeDir 'dotnet'
$installOnly = $false
$openBrowser = $true
$hostArguments = @('--root', $toolkitRoot)

function Fail-Launch([string] $Message, [int] $Code = 1) {
    [Console]::Error.WriteLine($Message)
    exit $Code
}

function Test-Sdk10([string] $Executable) {
    if (-not $Executable -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) { return $false }
    try {
        $versions = @(& $Executable --list-sdks 2>$null)
        if ($LASTEXITCODE -ne 0) { return $false }
        return [bool]($versions | Where-Object { $_ -match '^10\.[0-9]+\.[0-9]+ \[' })
    } catch { return $false }
}

try {
    for ($index = 0; $index -lt $args.Length; $index++) {
        switch ($args[$index]) {
            '--install-only' { $installOnly = $true }
            '--check' { $hostArguments += '--check'; $openBrowser = $false }
            '--no-browser' { $openBrowser = $false }
            '--port' {
                $index++
                if ($index -ge $args.Length) { Fail-Launch '错误：--port 缺少端口号。' 2 }
                $hostArguments += @('--port', [string]$args[$index])
            }
            { $_ -eq '--help' -or $_ -eq '-h' } {
                Write-Host '用法：start.cmd [--check | --install-only] [--no-browser] [--port 18765]'
                Write-Host '首次启动自动安装缺失的 .NET 10 SDK；--check 构建并检查，不启动服务。'
                exit 0
            }
            default { Fail-Launch ('错误：未知选项 {0}（可用 --help 查看）。' -f $args[$index]) 2 }
        }
    }

    New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_HOME = Join-Path $runtimeDir 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $runtimeDir 'nuget'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $localDotnet = Join-Path $sdkDir 'dotnet.exe'
    $dotnetExecutable = $null

    if ($env:BATTLE_TOOLKIT_DOTNET) {
        if (-not (Test-Sdk10 $env:BATTLE_TOOLKIT_DOTNET)) {
            Fail-Launch '错误：BATTLE_TOOLKIT_DOTNET 指定的程序没有稳定版 .NET 10 SDK。'
        }
        $dotnetExecutable = $env:BATTLE_TOOLKIT_DOTNET
    } elseif (Test-Sdk10 $localDotnet) {
        $dotnetExecutable = $localDotnet
    } elseif ($env:BATTLE_TOOLKIT_LOCAL_SDK -ne '1') {
        $installedDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($installedDotnet -and (Test-Sdk10 $installedDotnet.Source)) {
            $dotnetExecutable = $installedDotnet.Source
        }
    }

    if (-not $dotnetExecutable) {
        # PROCESSOR_ARCHITEW6432 detects a 64-bit/ARM64 OS even under an x86 shell.
        $machineArchitecture = $env:PROCESSOR_ARCHITEW6432
        if (-not $machineArchitecture) { $machineArchitecture = $env:PROCESSOR_ARCHITECTURE }
        switch ($machineArchitecture) {
            'ARM64' { $sdkArchitecture = 'arm64' }
            'AMD64' { $sdkArchitecture = 'x64' }
            default { Fail-Launch '错误：目前自动安装支持 Windows ARM64 和 Intel/AMD 64 位系统。' }
        }

        Write-Host '首次启动：正在从 Microsoft 下载 .NET 10 SDK（可能需要几分钟）。'
        Write-Host ('安装位置：{0}' -f $sdkDir)
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $installer = Join-Path $runtimeDir 'dotnet-install.ps1'
        $downloadPath = $installer + '.download'
        $proxyAddress = $env:HTTPS_PROXY
        if (-not $proxyAddress) { $proxyAddress = $env:HTTP_PROXY }
        $requestOptions = @{
            Uri = 'https://dot.net/v1/dotnet-install.ps1'
            OutFile = $downloadPath
            UseBasicParsing = $true
            TimeoutSec = 180
        }
        if ($proxyAddress) { $requestOptions.Proxy = $proxyAddress }
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Invoke-WebRequest @requestOptions | Out-Null
                Move-Item -LiteralPath $downloadPath -Destination $installer -Force
                break
            } catch {
                Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
                if ($attempt -eq 3) {
                    Fail-Launch ('下载失败。请检查网络或 HTTPS_PROXY/HTTP_PROXY 设置，然后重新启动。{0}{1}' -f [Environment]::NewLine, $_.Exception.Message)
                }
                Write-Host ('下载暂时失败，正在重试（{0}/3）……' -f ($attempt + 1))
                Start-Sleep -Seconds 2
            }
        }

        # Microsoft's installer calls exit: run it in a child process so our checks still run.
        $installerArguments = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer,
            '-Channel', '10.0', '-Quality', 'GA', '-Architecture', $sdkArchitecture,
            '-InstallDir', $sdkDir, '-NoPath')
        if ($proxyAddress) { $installerArguments += @('-ProxyAddress', $proxyAddress) }
        & (Join-Path $PSHOME 'powershell.exe') @installerArguments
        $installStatus = $LASTEXITCODE
        if ($installStatus -ne 0) {
            Fail-Launch '安装失败。请检查磁盘空间及网络，重新启动会再次尝试，不需要管理员权限。' $installStatus
        }
        if (-not (Test-Sdk10 $localDotnet)) {
            Fail-Launch '错误：安装完成后仍未检测到可用的 .NET 10 SDK，请查看上面的安装日志。'
        }
        $dotnetExecutable = $localDotnet
    }

    if ($dotnetExecutable -eq $localDotnet) { $env:DOTNET_ROOT = $sdkDir }
    $env:PATH = (Split-Path -Parent $dotnetExecutable) + [IO.Path]::PathSeparator + $env:PATH
    Write-Host ('运行环境：{0}' -f $dotnetExecutable)
    if ($installOnly) { exit 0 }

    Set-Location -LiteralPath $toolkitRoot
    $project = Join-Path $toolkitRoot 'Build/BattleToolkit.Host/BattleToolkit.Host.csproj'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        Fail-Launch '错误：工具文件不完整，缺少 Build/BattleToolkit.Host/BattleToolkit.Host.csproj。请完整下载或克隆仓库。'
    }
    Write-Host '正在准备战斗配置与试玩工具……'
    & $dotnetExecutable build $project --configuration Release --nologo -maxcpucount:1 -nodeReuse:false -p:UseSharedCompilation=false
    $buildStatus = $LASTEXITCODE
    if ($buildStatus -ne 0) { Fail-Launch '工具构建失败，请保留上方日志交给程序同事检查。' $buildStatus }
    if ($openBrowser) { $hostArguments += '--open' }
    & $dotnetExecutable (Join-Path $toolkitRoot 'Build/BattleToolkit.Host/bin/Release/net10.0/BattleToolkit.Host.dll') @hostArguments
    $hostStatus = $LASTEXITCODE
    if ($hostStatus -ne 0) { Fail-Launch '工具未能正常运行，请检查上方提示。' $hostStatus }
    exit 0
} catch {
    Fail-Launch ('启动失败：{0}' -f $_.Exception.Message)
}
