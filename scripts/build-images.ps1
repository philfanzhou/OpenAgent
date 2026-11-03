[CmdletBinding()]
param(
    [ValidateSet('all', 'engine', 'router', 'runner')]
    [string]$Application = 'all',
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
        if (-not (Test-Path -LiteralPath $resolvedEnvFile -PathType Leaf)) {
            throw "environment file does not exist: $resolvedEnvFile"
        }
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

    $composeArgs = @('--file', (Join-WslPath -Base $dockerRepoRoot -Child 'deploy/build.compose.yml'))
    if (-not [string]::IsNullOrWhiteSpace($EnvFile)) {
        $dockerEnvFile = if ($script:dockerCommand[0] -eq 'wsl.exe') {
            Convert-ToWslPath -Path $resolvedEnvFile
        } else { $resolvedEnvFile }
        $composeArgs += @('--env-file', $dockerEnvFile)
    }
    $applications = if ($Application -eq 'all') { @('engine', 'router', 'runner') } else { @($Application) }
    Invoke-Docker -Arguments (@('compose') + $composeArgs + @('build') + $applications)
    if (-not [string]::IsNullOrWhiteSpace($dockerTarDirectory)) {
        foreach ($buildApp in $applications) {
            $arguments = @('compose') + $composeArgs + @('config', '--images', $buildApp)
            $image = if ($script:dockerCommand.Count -eq 1) {
                & $script:dockerCommand[0] @arguments
            } else {
                & $script:dockerCommand[0] $script:dockerCommand[1] @arguments
            }
            if ($LASTEXITCODE -ne 0) { throw 'failed to resolve image name' }
            Export-ImageTar -Image $image.Trim() -FileName "openagent-$buildApp.tar" `
                -OutputDirectory $dockerTarDirectory -Wsl:($script:dockerCommand[0] -eq 'wsl.exe')
        }
    }
}
catch {
    Write-Error $_
    exit 1
}
