$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dir=Join-Path $root ('test-results\pluggy-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$env:FINANCE_DB=Join-Path $dir 'test.db'
$env:FINANCE_URL='http://127.0.0.1:5199'
$env:FINANCE_AUTH_DISABLED='1'
$env:MONALISA_LICENSE_DISABLED='1'
function Req($path,$body=$null){
 if($null-eq $body){return Invoke-RestMethod ('http://127.0.0.1:5199/api'+$path) -TimeoutSec 90}
 Invoke-RestMethod ('http://127.0.0.1:5199/api'+$path) -Method Post -ContentType 'application/json' -Body ($body|ConvertTo-Json -Compress) -TimeoutSec 90
}
$p=Start-Process 'C:\Tools\dotnet\dotnet.exe' -ArgumentList (Join-Path $root 'api\bin\Debug\net10.0\Monalisa.Financeiro.Server.dll') -WorkingDirectory (Join-Path $root 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'log.txt') -RedirectStandardError (Join-Path $dir 'error.txt')
try {
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 300}}
 $before=Req '/companies/1/state'
 $connected=Req '/companies/1/open-finance/sandbox/connect' @{}
 if(!$connected.sandbox-or !$connected.connected){throw 'Conexao Sandbox falhou'}
 'CONEXAO_SANDBOX=OK'
 $again=Req '/companies/1/open-finance/sandbox/connect' @{}
 if(!$again.connected){throw 'Conexao repetida falhou'}
 'CONEXAO_REUTILIZADA=OK'
 $data=Req '/companies/1/open-finance/sandbox'
 [pscustomobject]@{ready=$data.ready;status=$data.status;accounts=@($data.accounts).Count;transactions=@($data.accounts.transactions).Count}|ConvertTo-Json -Compress
 $after=Req '/companies/1/state'
 if($after.titles.Count-ne $before.titles.Count-or $after.settlements.Count-ne $before.settlements.Count-or $after.accounts[0].balance-ne $before.accounts[0].balance){throw 'Sandbox alterou os dados financeiros'}
 'DADOS_FINANCEIROS_PRESERVADOS=OK'
} finally {
 if(!$p.HasExited){Stop-Process -Id $p.Id}
 foreach($name in @('FINANCE_DB','FINANCE_URL','FINANCE_AUTH_DISABLED','MONALISA_LICENSE_DISABLED')){Remove-Item "Env:\$name" -ErrorAction SilentlyContinue}
}
