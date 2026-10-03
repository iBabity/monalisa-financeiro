param([string]$PanelHost)

$ErrorActionPreference = 'Continue'
$configurationFile = Join-Path $env:ProgramData 'Monalisa Financeiro\license-server.txt'
if ([string]::IsNullOrWhiteSpace($PanelHost) -and (Test-Path $configurationFile)) {
    try { $PanelHost = ([uri](Get-Content $configurationFile -Raw).Trim()).Host } catch {}
}
if ([string]::IsNullOrWhiteSpace($PanelHost)) {
    $PanelHost = Read-Host 'INFORME O IP OU NOME DA MAQUINA PRINCIPAL'
}
if ([string]::IsNullOrWhiteSpace($PanelHost)) { throw 'O endereço da máquina principal é obrigatório.' }
$connection = Get-NetIPConfiguration |
    Where-Object { $_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address } |
    Select-Object -First 1
$localIp = ($connection.IPv4Address.IPAddress -join ', ')
$gateway = ($connection.IPv4DefaultGateway.NextHop -join ', ')
$profile = Get-NetConnectionProfile -InterfaceIndex $connection.InterfaceIndex -ErrorAction SilentlyContinue
$panelTest = Test-NetConnection -ComputerName $PanelHost -Port 5200 -WarningAction SilentlyContinue
$dataTest = Test-NetConnection -ComputerName $PanelHost -Port 5188 -WarningAction SilentlyContinue

Write-Host ''
Write-Host 'DIAGNOSTICO DE REDE - MONALISA FINANCEIRO' -ForegroundColor Cyan
Write-Host "COMPUTADOR: $env:COMPUTERNAME"
Write-Host "IP DESTE COMPUTADOR: $localIp"
Write-Host "GATEWAY: $gateway"
Write-Host "PERFIL: $($profile.NetworkCategory)"
Write-Host "PAINEL ${PanelHost}:5200: $($panelTest.TcpTestSucceeded)"
Write-Host "DADOS ${PanelHost}:5188: $($dataTest.TcpTestSucceeded)"
Write-Host ''
if (-not $panelTest.TcpTestSucceeded -or -not $dataTest.TcpTestSucceeded) {
    Write-Host 'TIRE UMA FOTO DESTA TELA E ENVIE NO CHAT.' -ForegroundColor Yellow
} else {
    Write-Host 'CONEXAO COM A MAQUINA PRINCIPAL CONFIRMADA.' -ForegroundColor Green
}
Write-Host ''
Read-Host 'PRESSIONE ENTER PARA FECHAR'
