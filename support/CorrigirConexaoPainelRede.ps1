param([string]$PanelUrl)

$ErrorActionPreference = 'Stop'

$configurationRoot = Join-Path $env:ProgramData 'Monalisa Financeiro'
$configurationFile = Join-Path $configurationRoot 'license-server.txt'
if ([string]::IsNullOrWhiteSpace($PanelUrl) -and (Test-Path $configurationFile)) {
    $PanelUrl = (Get-Content $configurationFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($PanelUrl)) {
    $PanelUrl = Read-Host 'INFORME O ENDERECO DO PAINEL CENTRAL (EX.: http://192.168.1.10:5200)'
}
if ([string]::IsNullOrWhiteSpace($PanelUrl)) { throw 'O endereço do Painel Central é obrigatório.' }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-PanelUrl',('"' + $PanelUrl + '"'))
    exit
}

try {
    $health = Invoke-RestMethod -Uri ($PanelUrl.TrimEnd('/') + '/api/health') -TimeoutSec 5
    if ($health.status -ne 'ok') { throw 'O Painel Central respondeu sem confirmar que está ativo.' }

    New-Item -ItemType Directory -Path $configurationRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $configurationRoot 'license-server.txt'), $PanelUrl.TrimEnd('/'), [Text.Encoding]::ASCII)

    Get-Process -Name 'MonalisaFinanceiro','Monalisa.Financeiro' -ErrorAction SilentlyContinue | Stop-Process -Force
    Add-Type -AssemblyName PresentationFramework
    [Windows.MessageBox]::Show("CONEXÃO CORRIGIDA.`n`nABRA NOVAMENTE O MONALISA FINANCEIRO E TROQUE A LICENÇA.",'MONALISA FINANCEIRO') | Out-Null
} catch {
    Add-Type -AssemblyName PresentationFramework
    [Windows.MessageBox]::Show("NÃO FOI POSSÍVEL ACESSAR O PAINEL CENTRAL EM:`n$PanelUrl`n`nCONFIRME SE OS DOIS COMPUTADORES ESTÃO NA MESMA REDE E SE O PAINEL ESTÁ ABERTO.`n`n$($_.Exception.Message)",'MONALISA FINANCEIRO') | Out-Null
    exit 1
}
