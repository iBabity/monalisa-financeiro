param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Notes,
    [string]$PanelUrl = 'http://127.0.0.1:5200'
)

$ErrorActionPreference = 'Stop'
$credentials = & (Join-Path $PSScriptRoot 'GerarSenhaMestra.ps1')
$login = Invoke-RestMethod -Uri "$PanelUrl/api/admin/login" -Method Post -ContentType 'application/json' -Body (@{
    username = 'ADMIN'
    pin = $credentials.Senha
} | ConvertTo-Json) -TimeoutSec 10

$headers = @{ Authorization = "Bearer $($login.token)" }
$file = Get-Item -LiteralPath $Installer
Invoke-RestMethod -Uri "$PanelUrl/api/admin/updates" -Method Post -Headers $headers -Form @{
    version = $Version
    notes = $Notes
    file = $file
} -TimeoutSec 600
