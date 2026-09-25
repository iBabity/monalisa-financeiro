param([switch]$SemNavegador)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$api=Join-Path $root 'api'
$env:FINANCE_DB=Join-Path $root 'data\financeiro.db'
$env:FINANCE_URL='http://127.0.0.1:5187'
New-Item -ItemType Directory -Force (Join-Path $root 'logs') | Out-Null
$ready=$false
try { $ready=(Invoke-RestMethod 'http://127.0.0.1:5187/api/health').status -eq 'ok' } catch {}
if (-not $ready) {
    Start-Process -FilePath 'C:\Tools\dotnet\dotnet.exe' -ArgumentList @((Join-Path $api 'bin\Debug\net10.0\Monalisa.Financeiro.dll')) -WorkingDirectory $api -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root 'logs\api.log') -RedirectStandardError (Join-Path $root 'logs\error.log') | Out-Null
    for($i=0;$i -lt 40;$i++){try{$ready=(Invoke-RestMethod 'http://127.0.0.1:5187/api/health').status -eq 'ok';if($ready){break}}catch{};Start-Sleep -Milliseconds 300}
}
if(-not $ready){throw 'Não foi possível iniciar. Consulte a pasta logs.'}
if(-not $SemNavegador){Start-Process 'http://127.0.0.1:5187'}
