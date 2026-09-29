$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'EXECUTE ESTE ARQUIVO COMO ADMINISTRADOR.'
}

$displayName = 'Monalisa Painel Central - Porta 5200'
Get-NetFirewallRule -DisplayName $displayName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue

New-NetFirewallRule `
    -DisplayName $displayName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 5200 `
    -Profile Private,Public | Out-Null

Write-Output 'PAINEL CENTRAL LIBERADO NA REDE LOCAL PELA PORTA 5200.'
