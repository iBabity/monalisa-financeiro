$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$dir=Join-Path $root ('test-results\ofx-all-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$env:FINANCE_DB=Join-Path $dir 'test.db'
$env:FINANCE_URL='http://127.0.0.1:5199'
$env:FINANCE_AUTH_DISABLED='1'
$env:MONALISA_LICENSE_DISABLED='1'
function Req($path,$body=$null){
 if($null -eq $body){return Invoke-RestMethod ('http://127.0.0.1:5199/api'+$path) -TimeoutSec 5}
 Invoke-RestMethod ('http://127.0.0.1:5199/api'+$path) -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 10))) -TimeoutSec 10
}
function Check($ok,$label){if(!$ok){throw "FALHOU: $label"}; "OK: $label"}
$p=Start-Process 'C:\Tools\dotnet\dotnet.exe' -ArgumentList (Join-Path $root 'api\bin\Debug\net10.0\Monalisa.Financeiro.Server.dll') -WorkingDirectory (Join-Path $root 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'log.txt') -RedirectStandardError (Join-Path $dir 'error.txt')
try{
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 300}}
 $today=Get-Date -Format yyyyMMdd
 $iso=Get-Date -Format yyyy-MM-dd
 $ofx="OFXHEADER:100`nENCODING:UTF-8`n<OFX><BANKID>0341<BANKTRANLIST><STMTTRN><TRNTYPE>DEBIT<DTPOSTED>$today<TRNAMT>-10.00<FITID>D1<MEMO>SAIDA TESTE</STMTTRN><STMTTRN><TRNTYPE>CREDIT<DTPOSTED>$today<TRNAMT>20.00<FITID>C1<MEMO>ENTRADA TESTE</STMTTRN></BANKTRANLIST><LEDGERBAL><BALAMT>50.00<DTASOF>$today</LEDGERBAL></OFX>"
 $previewBody=@{accountId=1;fileName='teste.ofx';dataBase64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ofx))}
 $v=Req '/companies/1/ofx/preview' $previewBody
 Check ($v.total-eq 2 -and $v.bankBalance-eq 5000) 'Reconhece entradas, saidas e saldo'
 $items=@($v.transactions|ForEach-Object {@{transactionId=$_.id;create=$true;titleId=0}})
 Req '/companies/1/ofx/confirm' @{importId=$v.importId;items=$items;updateBalance=$true}|Out-Null
 $s=Req '/companies/1/state'
 Check (@($s.titles|Where-Object kind -eq payable).Count-eq 1 -and @($s.titles|Where-Object kind -eq receivable).Count-eq 1) 'Cria pagar e receber automaticamente'
 Check ($s.accounts[0].balance-eq 5000) 'Saldo conciliado com ajuste identificado'
 $v2=Req '/companies/1/ofx/preview' $previewBody
 Check ($v2.duplicates-eq 2) 'Reimportacao nao duplica'
 $balanceAgain=Req '/companies/1/ofx/confirm' @{importId=$v.importId;items=@();updateBalance=$true}
 Check ($balanceAgain.balanceAdjustment-eq 0) 'Reconciliar saldo novamente e idempotente'
 $failed=$false;try{Req '/companies/1/ofx/confirm' @{importId=$v.importId;items=$items}|Out-Null}catch{$failed=$true}
 Check $failed 'Rejeita baixa repetida'
 Req '/companies/1/titles' @{kind='payable';description='SAIDA TESTE';amount=1000;due=$iso;category='TESTE';notes=''}|Out-Null
 Req '/companies/1/titles' @{kind='receivable';description='ENTRADA TESTE';amount=2000;due=$iso;category='TESTE';notes=''}|Out-Null
 $manualTitles=@((Req '/companies/1/state').titles|Where-Object {$_.remaining-gt 0})
 foreach($t in $manualTitles){Req "/companies/1/titles/$($t.id)/settle" @{accountId=1;principal=$t.amount;interest=0;penalty=0;discount=0;date=$iso}|Out-Null}
 $existingOfx=$ofx.Replace('<FITID>D1','<FITID>D2').Replace('<FITID>C1','<FITID>C2')
 $previewBody.dataBase64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($existingOfx))
 $manual=Req '/companies/1/ofx/preview' $previewBody
 Check (@($manual.transactions|Where-Object {$null-ne $_.existingSettlementId}).Count-eq 2) 'Encontra baixas ja registradas para vincular sem duplicar'
 $linkItems=@($manual.transactions|ForEach-Object {@{transactionId=$_.id;existingSettlementId=$_.existingSettlementId}})
 Req '/companies/1/ofx/confirm' @{importId=$manual.importId;items=$linkItems}|Out-Null
 Check ((Req '/companies/1/state').accounts[0].balance-eq 6000) 'Vinculo de baixa existente preserva saldo'
 $file='C:\Users\conta\Downloads\Extrato Conta Corrente-011120262205.ofx'
 if(Test-Path -LiteralPath $file){
  $real=Req '/companies/1/ofx/preview' @{accountId=1;fileName='itau.ofx';dataBase64=[Convert]::ToBase64String([IO.File]::ReadAllBytes($file))}
  Check ($real.bankBalance-eq 1249 -and $real.balanceDate-eq '2026-11-01') 'Arquivo Itau real reconhece saldo e data'
  Check (@($real.transactions|Where-Object future).Count-gt 0) 'Identifica datas futuras para conferencia'
 }
 'TESTES OFX COMPLETOS PASSARAM'
}finally{
 if(!$p.HasExited){Stop-Process -Id $p.Id}
 foreach($name in @('FINANCE_DB','FINANCE_URL','FINANCE_AUTH_DISABLED','MONALISA_LICENSE_DISABLED')){Remove-Item "Env:\$name" -ErrorAction SilentlyContinue}
}
