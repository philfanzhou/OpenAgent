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

    $converted = (& wsl.exe wslpath -a -u $Path).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($converted)) {
        throw "failed to convert path for WSL Docker: $Path"
    }
    return $converted.Replace('\', '/')
}

function Export-ImageTar {
    param(
        [Parameter(Mandatory = $true)][string]$Image,
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string]$OutputDirectory
    )

    Invoke-Docker -Arguments @(
        'save', '--output', (Join-Path $OutputDirectory $FileName), $Image
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

    $engineImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ENGINE_IMAGE)) { 'openagent-engine:latest' } else { $env:OPENAGENT_ENGINE_IMAGE }
    $routerImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ROUTER_IMAGE)) { 'openagent-router:latest' } else { $env:OPENAGENT_ROUTER_IMAGE }
    $chatImage = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_CHAT_IMAGE)) { 'openagent-chat:latest' } else { $env:OPENAGENT_CHAT_IMAGE }
    $publicHost = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_PUBLIC_HOST)) { 'localhost' } else { $env:OPENAGENT_PUBLIC_HOST }
    $publicScheme = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_PUBLIC_SCHEME)) { 'https' } else { $env:OPENAGENT_PUBLIC_SCHEME }
    $routerPort = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ROUTER_PORT)) { '8082' } else { $env:OPENAGENT_ROUTER_PORT }
    $enginePort = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_ENGINE_PORT)) { '8083' } else { $env:OPENAGENT_ENGINE_PORT }
    $routerUrl = "${publicScheme}://$publicHost`:$routerPort"
    $engineUrl = "${publicScheme}://$publicHost`:$enginePort"
    $tenantId = if ([string]::IsNullOrWhiteSpace($env:OPENAGENT_TENANT_ID)) { 'development' } else { $env:OPENAGENT_TENANT_ID }

    # 构建应用镜像；不启动或修改任何容器。
    Invoke-Docker -Arguments @(
        'build', '--tag', $engineImage,
        '--file', (Join-Path $dockerRepoRoot 'Backend/src/OpenAgent.Engine.Host/Dockerfile'),
        $dockerRepoRoot
    )
    Invoke-Docker -Arguments @(
        'build', '--tag', $routerImage,
        '--file', (Join-Path $dockerRepoRoot 'Backend/src/OpenAgent.Router/Dockerfile'),
        $dockerRepoRoot
    )
    Invoke-Docker -Arguments @(
        'build', '--tag', $chatImage,
        '--build-arg', "VITE_OPENAGENT_ROUTER_BASE_URL=$routerUrl",
        '--build-arg', "VITE_OPENAGENT_ENGINE_BASE_URL=$engineUrl",
        '--build-arg', "VITE_OPENAGENT_TENANT_ID=$tenantId",
        '--file', (Join-Path $dockerRepoRoot 'Frontend/OpenAgent.Chat/Dockerfile'),
        $dockerRepoRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($dockerTarDirectory)) {
        Export-ImageTar -Image $engineImage -FileName 'openagent-engine.tar' -OutputDirectory $dockerTarDirectory
        Export-ImageTar -Image $routerImage -FileName 'openagent-router.tar' -OutputDirectory $dockerTarDirectory
        Export-ImageTar -Image $chatImage -FileName 'openagent-chat.tar' -OutputDirectory $dockerTarDirectory
    }
}
catch {
    Write-Error $_
    exit 1
}
