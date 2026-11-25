param([string]$ApiDll = (Join-Path $PSScriptRoot 'api\bin\Debug\net10.0\Monalisa.Financeiro.Server.dll'))
$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results\reverse-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$names=@('FINANCE_DB','FINANCE_URL','FINANCE_AUTH_DISABLED','MONALISA_LICENSE_DISABLED')
$previous=@{}; foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name)}
$env:FINANCE_DB=Join-Path $dir 'test.db'
$env:FINANCE_URL='http://127.0.0.1:5196'
$env:FINANCE_AUTH_DISABLED='1'
$env:MONALISA_LICENSE_DISABLED='1'
function Req($path,$body=$null){
 $url='http://127.0.0.1:5196/api'+$path
 if($null -eq $body){return Invoke-RestMethod $url -TimeoutSec 10}
 Invoke-RestMethod $url -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 10))) -TimeoutSec 10
}
function Check($ok,$label){if(!$ok){throw "FALHOU: $label"}; "OK: $label"}
$p=Start-Process 'C:\Tools\dotnet\dotnet.exe' -ArgumentList $ApiDll -WorkingDirectory (Join-Path $PSScriptRoot 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'log.txt') -RedirectStandardError (Join-Path $dir 'error.txt')
try {
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 300}}
 $today=Get-Date -Format yyyy-MM-dd; $ofxDate=Get-Date -Format yyyyMMdd
 foreach($kind in @('payable','receivable')) {
  $sign=if($kind-eq 'payable'){'-'}else{''}
  foreach($number in 1..2){Req '/companies/1/titles' @{kind=$kind;description="C&A $kind PARCELA $number";amount=1000;due=$today;category='TESTE';notes=''}|Out-Null}
  $titles=@((Req '/companies/1/state').titles|Where-Object {$_.kind-eq $kind}|Sort-Object id)
  $ofx="<OFX><BANKTRANLIST><STMTTRN><DTPOSTED>$ofxDate<TRNAMT>${sign}10.00<FITID>REVERSE-$kind<MEMO>C&A</STMTTRN></BANKTRANLIST></OFX>"
  $body=@{accountId=1;fileName='test.ofx';dataBase64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ofx))}
  $preview=Req '/companies/1/ofx/preview' $body
  Check ($null-eq $preview.transactions[0].suggestedTitleId) 'Parcelas iguais e texto insuficiente exigem escolha manual'
  $result=Req '/companies/1/ofx/confirm' @{importId=$preview.importId;items=@(@{transactionId=$preview.transactions[0].id;titleId=$titles[1].id})}
  $before=(Req '/companies/1/state').accounts[0].balance
  Req "/companies/1/settlements/$($result.settlementIds[0])/reverse" @{reason='PARCELA INCORRETA';adjustBalance=$false}|Out-Null
  $state=Req '/companies/1/state'
  Check ($state.accounts[0].balance-eq $before -and ($state.titles|Where-Object id -eq $titles[1].id).remaining-eq 1000) "Estorno sem saldo reabre $kind e preserva conta"
  $preview=Req '/companies/1/ofx/preview' $body
  Check (!$preview.transactions[0].duplicate) 'OFX permite corrigir a parcela após estorno'
  $corrected=Req '/companies/1/ofx/confirm' @{importId=$preview.importId;items=@(@{transactionId=$preview.transactions[0].id;titleId=$titles[0].id})}
  Check ((Req '/companies/1/state').accounts[0].balance-eq $before) 'Reconciliar parcela correta reaproveita movimento sem duplicar saldo'
  $again=Req '/companies/1/ofx/preview' $body
  Check ($again.transactions[0].duplicate) 'Reimportação continua protegida contra duplicidade'
  Req "/companies/1/settlements/$($corrected.settlementIds[0])/reverse" @{reason='ESTORNO NORMAL'}|Out-Null
  Check ((Req '/companies/1/state').accounts[0].balance-eq 0) 'Estorno padrão mantém compatibilidade e atualiza saldo'
 }
 'TESTES DE ESTORNO COM E SEM SALDO PASSARAM'
} finally {
 if(!$p.HasExited){Stop-Process -Id $p.Id}
 foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name])}
}
