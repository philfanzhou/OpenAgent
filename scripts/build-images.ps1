[CmdletBinding()]
param(
    [string]$EnvFile = '',
    [string]$DockerMode = '',
    [string]$TarDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dockerCommand = @()

function Test-LocalDocker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return $false
    }

    & docker info --format '{{.ServerVersion}}' *> $null
    return $LASTEXITCODE -eq 0
}

function Resolve-DockerCommand {
    if ($DockerMode -eq 'auto') {
        if (-not [string]::IsNullOrWhiteSpace($env:WSL_DISTRO_NAME) -and (Test-LocalDocker)) {
            $script:dockerCommand = @('docker')
        }
        elseif (Test-LocalDocker) {
            $script:dockerCommand = @('docker')
        }
        elseif (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
            $script:dockerCommand = @('wsl.exe', 'docker')
        }
        else {
            throw 'auto docker mode could not find docker or wsl.exe'
        }
        return
    }

    if ($DockerMode -in @('docker', 'local')) {
        if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
            throw 'docker mode requires the docker command'
        }
        $script:dockerCommand = @('docker')
        return
    }

    if ($DockerMode -in @('wsl', 'wsl-docker')) {
        if (-not [string]::IsNullOrWhiteSpace($env:WSL_DISTRO_NAME) -and (Get-Command docker -ErrorAction SilentlyContinue)) {
            $script:dockerCommand = @('docker')
        }
        elseif (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
            $script:dockerCommand = @('wsl.exe', 'docker')
        }
        else {
            throw 'wsl-docker mode requires WSL or wsl.exe'
        }
        return
    }

    throw "unsupported docker mode '$DockerMode' (use auto, docker, or wsl-docker)"
}

function Import-EnvironmentFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "environment file does not exist: $Path"
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*(?:#.*)?$') {
            continue
        }

        if ($line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)\s*$') {
            throw "invalid environment file entry: $line"
        }

        $name = $Matches[1]
        $value = $Matches[2].Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
}

function Invoke-Docker {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    if ($script:dockerCommand.Count -eq 1) {
        & $script:dockerCommand[0] @Arguments
    }
    else {
        & $script:dockerCommand[0] $script:dockerCommand[1] @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "docker command failed with exit code $LASTEXITCODE"
    }
}

function Convert-ToWslPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    # wsl.exe 转发参数时会吞掉反斜杠，先统一成正斜杠（wslpath 同样接受 D:/ 形式）。
    $normalized = $Path.Replace('\', '/')
    $converted = (& wsl.exe wslpath -a -u $normalized).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($converted)) {
        throw "failed to convert path for WSL Docker: $Path"
    }
    return $converted.Replace('\', '/')
}

function Join-WslPath {
    # Join-Path 会把 WSL 路径里的 '/' 重写成 '\'，再经 wsl.exe 转发就被吞掉；
    # WSL 侧路径必须用纯正斜杠手工拼接。
    param(
        [Parameter(Mandatory = $true)][string]$Base,
        [Parameter(Mandatory = $true)][string]$Child
    )
    return ($Base.TrimEnd('/') + '/' + $Child).Replace('\', '/')
}

function Export-ImageTar {
    param(
        [Parameter(Mandatory = $true)][string]$Image,
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [switch]$Wsl
    )

    $outputPath = if ($Wsl) {
        Join-WslPath -Base $OutputDirectory -Child $FileName
    }
    else {
        Join-Path $OutputDirectory $FileName
    }

    Invoke-Docker -Arguments @(
        'save', '--output', $outputPath, $Image
    )
}

try {
    if (-not [string]::IsNullOrWhiteSpace($EnvFile)) {
        $resolvedEnvFile = if ([IO.Path]::IsPathRooted($EnvFile)) {
            $EnvFile
        }
        else {
            Join-Path (Get-Location) $EnvFile
        }
        Import-EnvironmentFile -Path $resolvedEnvFile
    }

    if ([string]::IsNullOrWhiteSpace($DockerMode)) {
        $DockerMode = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_DOCKER_MODE)) {
            'auto'
        }
        else {
            $env:OPENAGENT_DOCKER_MODE
        }
    }

    Resolve-DockerCommand

    $dockerRepoRoot = $repoRoot
    if ($script:dockerCommand[0] -eq 'wsl.exe') {
        $dockerRepoRoot = Convert-ToWslPath -Path $repoRoot
    }

    if ([string]::IsNullOrWhiteSpace($TarDirectory)) {
        $TarDirectory = $env:OPENAGENT_IMAGE_TAR_DIR
    }

    $dockerTarDirectory = ''
    if (-not [string]::IsNullOrWhiteSpace($TarDirectory)) {
        New-Item -ItemType Directory -Force -Path $TarDirectory | Out-Null
        $TarDirectory = (Resolve-Path -LiteralPath $TarDirectory).Path
        $dockerTarDirectory = if ($script:dockerCommand[0] -eq 'wsl.exe') {
            Convert-ToWslPath -Path $TarDirectory
        }
        else {
            $TarDirectory
        }
    }

    # Engine 镜像内嵌的 OpenCode 版本由 Backend/opencode.version 锁定；
    # OPENAGENT_OPENCODE_VERSION 仅用于升级前临时验证（latest 或严格语义化版本），提交仍以版本文件为准。
    $openCodeVersionFile = Join-Path $repoRoot 'Backend/opencode.version'
    if (-not (Test-Path -LiteralPath $openCodeVersionFile -PathType Leaf)) {
        throw "OpenCode version file does not exist: $openCodeVersionFile"
    }
    $openCodeVersion = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_OPENCODE_VERSION)) {
        (Get-Content -LiteralPath $openCodeVersionFile -Raw).Trim()
    }
    else {
        $env:OPENAGENT_OPENCODE_VERSION
    }
    if ($openCodeVersion -notmatch '^(\d+\.\d+\.\d+|latest)$') {
        throw "invalid OpenCode version '$openCodeVersion' (expected strict semver in Backend/opencode.version, or latest via OPENAGENT_OPENCODE_VERSION override)"
    }

    $engineImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ENGINE_IMAGE)) { 'openagent-engine:latest' } else { $env:OPENAGENT_ENGINE_IMAGE }
    $routerImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ROUTER_IMAGE)) { 'openagent-router:latest' } else { $env:OPENAGENT_ROUTER_IMAGE }
    $chatImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_CHAT_IMAGE)) { 'openagent-chat:latest' } else { $env:OPENAGENT_CHAT_IMAGE }
    $runnerImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_RUNNER_IMAGE)) { 'openagent-runner:latest' } else { $env:OPENAGENT_RUNNER_IMAGE }
    $publicHost = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_PUBLIC_HOST)) { 'localhost' } else { $env:OPENAGENT_PUBLIC_HOST }
    $publicScheme = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_PUBLIC_SCHEME)) { 'https' } else { $env:OPENAGENT_PUBLIC_SCHEME }
    $routerPort = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ROUTER_PORT)) { '8082' } else { $env:OPENAGENT_ROUTER_PORT }
    $enginePort = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ENGINE_PORT)) { '8083' } else { $env:OPENAGENT_ENGINE_PORT }
    $tenantId = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_TENANT_ID)) { 'development' } else { $env:OPENAGENT_TENANT_ID }

    # 本地镜像带可追溯的版本标识；Release 工作流会用正式 tag/revision 覆盖同一组 label。
    $platformVersion = 'dev'
    try {
        $described = & git -C $repoRoot describe --tags --always --dirty 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($described)) {
            $platformVersion = $described.Trim()
        }
    }
    catch {
        # 非 git 检出或 git 不可用时保持 dev。
    }

    # 构建应用镜像；不启动或修改任何容器。
    Invoke-Docker -Arguments @(
        'build', '--tag', $engineImage,
        '--build-arg', "OPENCODE_VERSION=$openCodeVersion",
        '--label', "org.opencontainers.image.version=$platformVersion",
        '--file', (Join-WslPath -Base $dockerRepoRoot -Child 'Backend/src/OpenAgent.Engine.Host/Dockerfile'),
        $dockerRepoRoot
    )
    Invoke-Docker -Arguments @(
        'build', '--tag', $routerImage,
        '--label', "org.opencontainers.image.version=$platformVersion",
        '--file', (Join-WslPath -Base $dockerRepoRoot -Child 'Backend/src/OpenAgent.Router/Dockerfile'),
        $dockerRepoRoot
    )
    # Chat 镜像的浏览器端地址由 Dockerfile 从统一变量派生，与直接 docker build 的接口一致。
    Invoke-Docker -Arguments @(
        'build', '--tag', $chatImage,
        '--build-arg', "OPENAGENT_PUBLIC_SCHEME=$publicScheme",
        '--build-arg', "OPENAGENT_PUBLIC_HOST=$publicHost",
        '--build-arg', "OPENAGENT_ROUTER_PORT=$routerPort",
        '--build-arg', "OPENAGENT_ENGINE_PORT=$enginePort",
        '--build-arg', "OPENAGENT_TENANT_ID=$tenantId",
        '--label', "org.opencontainers.image.version=$platformVersion",
        '--file', (Join-WslPath -Base $dockerRepoRoot -Child 'Frontend/OpenAgent.Chat/Dockerfile'),
        $dockerRepoRoot
    )

    Invoke-Docker -Arguments @(
        'build', '--tag', $runnerImage,
        '--label', "org.opencontainers.image.version=$platformVersion",
        '--file', (Join-WslPath -Base $dockerRepoRoot -Child 'Backend/src/OpenAgent.Runner/Dockerfile'),
        $dockerRepoRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($dockerTarDirectory)) {
        $exportWsl = $script:dockerCommand[0] -eq 'wsl.exe'
        Export-ImageTar -Image $runnerImage -FileName 'openagent-runner.tar' -OutputDirectory $dockerTarDirectory -Wsl:$exportWsl
        Export-ImageTar -Image $engineImage -FileName 'openagent-engine.tar' -OutputDirectory $dockerTarDirectory -Wsl:$exportWsl
        Export-ImageTar -Image $routerImage -FileName 'openagent-router.tar' -OutputDirectory $dockerTarDirectory -Wsl:$exportWsl
        Export-ImageTar -Image $chatImage -FileName 'openagent-chat.tar' -OutputDirectory $dockerTarDirectory -Wsl:$exportWsl
    }
}
catch {
    Write-Error $_
    exit 1
}
