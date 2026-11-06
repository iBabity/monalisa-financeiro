param(
    [string]$InstallRoot = $PSScriptRoot,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$showcase = Test-Path -LiteralPath (Join-Path $InstallRoot 'showcase-edition.txt')
$noLogin = $showcase -or (Test-Path -LiteralPath (Join-Path $InstallRoot 'no-login.txt'))
$editionFolder = if ($showcase) { 'Monalisa Financeiro Demonstracao' } else { 'Monalisa Financeiro' }
$configurationRoot = Join-Path $env:ProgramData $editionFolder
$dataRoot = Join-Path $configurationRoot 'data'
$serverPath = Join-Path $InstallRoot 'server\Monalisa.Financeiro.Server.exe'
$url = 'http://127.0.0.1:5187/'

function Read-Setting([string]$Name, [string]$Default = '') {
    $path = Join-Path $configurationRoot $Name
    if (-not (Test-Path -LiteralPath $path)) { return $Default }
    return (Get-Content -LiteralPath $path -Raw).Trim()
}

function Test-CorrectServer {
    try {
        $status = Invoke-RestMethod ($url + 'api/license/status') -TimeoutSec 1
        return [bool]$status.showcase -eq $showcase
    } catch {
        return $false
    }
}

try {
    if (-not (Test-Path -LiteralPath $serverPath)) {
        throw 'O SERVIÇO LOCAL DO MONALISA FINANCEIRO NÃO FOI ENCONTRADO.'
    }
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

    if (-not (Test-CorrectServer)) {
        Get-Process -Name 'Monalisa.Financeiro.Server','Monalisa.Financeiro' -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue

        $env:FINANCE_DB = Join-Path $dataRoot 'financeiro.db'
        $networkRole = Read-Setting 'network-role.txt' 'standalone'
        $networkKey = Read-Setting 'network-key.txt'
        $dataServer = Read-Setting 'network-server.txt'
        if ($networkRole -eq 'server') {
            $lanAddress = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
                Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
                ForEach-Object { $_.IPv4Address.IPAddress } |
                Where-Object { $_ -and $_ -notlike '127.*' -and $_ -notlike '169.254.*' } |
                Select-Object -First 1
            if ($lanAddress) {
                $dataServer = "http://${lanAddress}:5188"
                Set-Content -LiteralPath (Join-Path $configurationRoot 'network-server.txt') -Value $dataServer -Encoding ascii
            }
        }
        $env:MONALISA_NETWORK_ROLE = $networkRole
        $env:MONALISA_NETWORK_KEY = $networkKey
        $env:MONALISA_NETWORK_SERVER_URL = $dataServer
        $env:MONALISA_NETWORK_SHARED_PORT = '5188'
        if ($networkRole -eq 'client') { $env:MONALISA_DATA_SERVER_URL = $dataServer }
        else { Remove-Item Env:\MONALISA_DATA_SERVER_URL -ErrorAction SilentlyContinue }
        $env:FINANCE_URL = if ($networkRole -eq 'server') { 'http://127.0.0.1:5187;http://0.0.0.0:5188' } else { 'http://127.0.0.1:5187' }
        if ($showcase) { $env:MONALISA_SHOWCASE = '1' }
        if ($noLogin) { $env:MONALISA_NO_LOGIN = '1' }
        $licenseServer = Read-Setting 'license-server.txt'
        if ($licenseServer) { $env:MONALISA_LICENSE_URL = $licenseServer }

        Start-Process -FilePath $serverPath -WorkingDirectory (Split-Path $serverPath -Parent) -WindowStyle Hidden | Out-Null
        for ($attempt = 0; $attempt -lt 50 -and -not (Test-CorrectServer); $attempt++) {
            Start-Sleep -Milliseconds 200
        }
    }

    if (-not (Test-CorrectServer)) {
        throw 'O SERVIÇO LOCAL NÃO RESPONDEU. FECHE AS VERSÕES ANTIGAS DO MONALISA E TENTE NOVAMENTE.'
    }
    $version = (Get-Item (Join-Path $InstallRoot 'MonalisaFinanceiro.exe') -ErrorAction SilentlyContinue).VersionInfo.FileVersion
    if (-not $NoBrowser) {
        Start-Process ($url + '?v=' + [Uri]::EscapeDataString($version)) | Out-Null
    }
} catch {
    if ($NoBrowser) {
        $logDirectory = Join-Path $configurationRoot 'logs'
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        Add-Content -LiteralPath (Join-Path $logDirectory 'startup-error.log') -Value ("{0}: {1}" -f (Get-Date -Format o), $_.Exception.Message)
        exit 1
    }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'MONALISA FINANCEIRO') | Out-Null
    exit 1
}
