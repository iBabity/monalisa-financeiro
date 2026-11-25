param([switch]$NoBrowser, [string]$Root = (Join-Path $env:LOCALAPPDATA 'MonalisaFinanceiroIndependente'))
$ErrorActionPreference = 'Stop'
try {
    $current = Get-Content -LiteralPath (Join-Path $Root 'current.json') -Raw | ConvertFrom-Json
    $package = Join-Path $Root ('versions/' + $current.version)
    . (Join-Path $package 'common.ps1')
    $guard = New-Object Threading.Mutex($false, 'Local\MonalisaFinanceiroIndependente-Launch')
    if (-not $guard.WaitOne(0)) { return }
    try {
        try { Apply-ReadyUpdate $Root (Join-Path $Root 'update-public.xml') } catch {
            Add-Content -LiteralPath (Join-Path $Root 'update.log') -Value ((Get-Date -Format o) + ' ' + $_.Exception.Message)
        }
        $current = Read-JsonFile (Join-Path $Root 'current.json')
        $package = Join-Path $Root ('versions/' + $current.version)
        $url = 'http://127.0.0.1:5197/'
        if (-not (Test-OwnServer $Root)) {
            $occupied = Get-NetTCPConnection -LocalPort 5197 -State Listen -ErrorAction SilentlyContinue
            if ($occupied) { throw 'A PORTA 5197 ESTÁ OCUPADA POR OUTRO SERVIÇO. NENHUM BANCO FOI ALTERADO.' }
            New-Item -ItemType Directory -Path (Join-Path $Root 'data') -Force | Out-Null
            $env:FINANCE_DB = Join-Path $Root 'data/financeiro.db'
            $env:FINANCE_URL = $url.TrimEnd('/')
            $env:MONALISA_INDEPENDENT = '1'
            $env:MONALISA_REQUIRE_DAILY_LOGIN = '1'
            $env:MONALISA_NETWORK_ROLE = 'standalone'
            foreach ($name in @('FINANCE_AUTH_DISABLED','MONALISA_SHOWCASE','MONALISA_NO_LOGIN','MONALISA_DATA_SERVER_URL','MONALISA_NETWORK_KEY','MONALISA_NETWORK_SERVER_URL','MONALISA_WEB_ORIGIN','MONALISA_LICENSE_URL')) { Remove-Item ('Env:\' + $name) -ErrorAction SilentlyContinue }
            $server = Join-Path $package 'server/Monalisa.Financeiro.Server.exe'
            Start-Process -FilePath $server -WorkingDirectory (Split-Path $server) -WindowStyle Hidden | Out-Null
            $healthy = $false
            for ($attempt=0; $attempt -lt 60; $attempt++) {
                try { $health = Invoke-RestMethod ($url + 'api/health') -TimeoutSec 1; if ($health.status -eq 'ok') { $healthy=$true; break } } catch {}
                Start-Sleep -Milliseconds 250
            }
            if (-not $healthy) { throw 'O SERVIÇO LOCAL NÃO INICIOU. SEUS DADOS FORAM PRESERVADOS.' }
        }
        # A separate, hidden worker checks every 5 min; never replaces a running server.
        Start-Process -FilePath powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',('"' + (Join-Path $package 'update.ps1') + '"'),'-Root',('"' + $Root + '"')) -WindowStyle Hidden | Out-Null
        if (-not $NoBrowser) { Start-Process ($url + '?v=' + $current.version) | Out-Null }
    } finally { $guard.ReleaseMutex(); $guard.Dispose() }
} catch {
    if (Test-Path -LiteralPath $Root) { Add-Content -LiteralPath (Join-Path $Root 'startup.log') -Value ((Get-Date -Format o) + ' ' + $_.Exception.Message) }
    if (-not $NoBrowser) { Add-Type -AssemblyName PresentationFramework; [Windows.MessageBox]::Show($_.Exception.Message,'MONALISA FINANCEIRO') | Out-Null }
    exit 1
}
