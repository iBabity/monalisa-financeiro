param([string]$ApiDll=(Join-Path $PSScriptRoot 'api\bin\Debug\net10.0\Monalisa.Financeiro.Server.dll'))
$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results\ofx-automatic-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$names=@('FINANCE_DB','FINANCE_URL','FINANCE_AUTH_DISABLED','MONALISA_LICENSE_DISABLED')
$old=@{};foreach($n in $names){$old[$n]=[Environment]::GetEnvironmentVariable($n)}
$env:FINANCE_DB=Join-Path $dir 'test.db';$env:FINANCE_URL='http://127.0.0.1:5191';$env:FINANCE_AUTH_DISABLED='1';$env:MONALISA_LICENSE_DISABLED='1'
$today=Get-Date -Format yyyy-MM-dd;$bankDate=Get-Date -Format yyyyMMdd
function Req($path,$body=$null){$u='http://127.0.0.1:5191/api'+$path;if($null-eq $body){return Invoke-RestMethod $u -TimeoutSec 10};Invoke-RestMethod $u -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Depth 12))) -TimeoutSec 10}
function Check($ok,$label){if(!$ok){throw "FALHOU: $label"};"OK: $label"}
function Title($kind,$description,$amount){Req '/companies/1/titles' @{kind=$kind;description=$description;amount=$amount;due=$today;category='TESTE';notes=''}|Out-Null;@((Req '/companies/1/state').titles|Where-Object description -eq $description)[0]}
function Pay($title,$date=$today){Req "/companies/1/titles/$($title.id)/settle" @{accountId=1;principal=$title.amount;interest=0;penalty=0;discount=0;date=$date}}
function Preview($entries,$balance=''){
 $xml='<OFX><BANKTRANLIST>'
 foreach($e in $entries){$xml+="<STMTTRN><DTPOSTED>$bankDate<TRNAMT>$($e.amount)<FITID>$($e.fit)<MEMO>$($e.memo)</STMTTRN>"}
 $xml+='</BANKTRANLIST>'
 if($balance){$xml+="<LEDGERBAL><BALAMT>$balance<DTASOF>$bankDate</LEDGERBAL>"};$xml+='</OFX>'
 $script:lastBody=@{accountId=1;fileName='teste.ofx';dataBase64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($xml))}
 Req '/companies/1/ofx/preview' $lastBody
}
function AutoConfirm($preview){Req '/companies/1/ofx/confirm' @{importId=$preview.importId;items=@($preview.transactions|ForEach-Object{@{transactionId=$_.id;create=$true}})}}
$p=Start-Process C:\Tools\dotnet\dotnet.exe -ArgumentList $ApiDll -WorkingDirectory (Join-Path $PSScriptRoot 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'out.log') -RedirectStandardError (Join-Path $dir 'error.log')
try {
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 300}}
 $salary=Title 'receivable' 'SALÁRIO' 10000;Pay $salary ((Get-Date).AddDays(-1).ToString('yyyy-MM-dd'))|Out-Null
 $v=Preview @(@{fit='S1';amount='100.00';memo='PAGTO SALARIOS'},@{fit='R1';amount='50.00';memo='RESGATE COFRINHOS'},@{fit='A1';amount='-20.00';memo='APLICACAO COFRINHOS'},@{fit='D1';amount='-10.00';memo='LOJA TESTE'}) '120.00'
 Check (($v.transactions|Where-Object memo -eq 'PAGTO SALARIOS').existingSettlementId-gt 0) 'Salário manual em outra data reconhecido antes de criar duplicidade'
 Check (@($v.transactions|Where-Object internalTransfer).Count-eq 2) 'Resgate e aplicação reconhecidos como transferências'
 AutoConfirm $v|Out-Null
 $state=Req '/companies/1/state'
 Check (@($state.titles|Where-Object kind -eq receivable).Count-eq 1 -and @($state.titles|Where-Object kind -eq payable).Count-eq 1 -and $state.accounts[0].balance-eq 12000) 'Automação não duplica salário e não cria receita/despesa de cofrinhos'
 $again=Req '/companies/1/ofx/preview' $lastBody
 Check ($again.duplicates-eq 4) 'Reimportação reconhece baixas e transferências já conciliadas'
 Check (@($again.transactions|Where-Object amount -le 0).Count-eq 0) 'Movimentações já conciliadas continuam exibindo o valor do banco'
 $ca=Title 'payable' 'C&A (1/5)' 1000;Pay $ca|Out-Null
 $future=Title 'payable' 'C&A (2/5)' 1000
 $v=Preview @(@{fit='CA1';amount='-10.00';memo='PIX QRS CEA PAY FUN'})
 AutoConfirm $v|Out-Null
 $state=Req '/companies/1/state'
 Check (($state.titles|Where-Object id -eq $future.id).remaining-eq 1000) 'C&A já paga é vinculada sem baixar a segunda parcela'
 $parts=@();$paid=@()
 foreach($entry in @(@('BRUNO NOTEBOOK',1000),@('BRUNO IPHONE',2000),@('BRUNO OCULOS',3000))){$t=Title 'payable' $entry[0] $entry[1];$parts+=,$t;$paid+=,(Pay $t)}
 $before=(Req '/companies/1/state').accounts[0].balance
 $v=Preview @(@{fit='B1';amount='-80.00';memo='PIX TRANSF BRUNO'})
 Check ($v.transactions[0].existingSettlementIds.Count-eq 3) 'PIX identifica três parcelas já baixadas'
 AutoConfirm $v|Out-Null
 Check ((Req '/companies/1/state').accounts[0].balance-eq ($before-2000)) 'Rateio registra somente a diferença ainda não lançada'
 $before=(Req '/companies/1/state').accounts[0].balance
 Req "/companies/1/settlements/$($paid[0].id)/reverse" @{reason='CORRIGIR RATEIO';adjustBalance=$false}|Out-Null
 $v=Req '/companies/1/ofx/preview' $lastBody
 Check ($v.transactions[0].amount-eq 1000 -and !$v.transactions[0].duplicate) 'Estorno parcial reabre somente a parte do PIX correspondente'
 Req '/companies/1/ofx/confirm' @{importId=$v.importId;items=@(@{transactionId=$v.transactions[0].id;titleId=$parts[0].id})}|Out-Null
 Check ((Req '/companies/1/state').accounts[0].balance-eq $before) 'Correção do rateio reaproveita saldo preservado'
 $t1=Title 'receivable' 'SALARIO EXTRA' 3000;Pay $t1|Out-Null
 $t2=Title 'receivable' 'SALARIO BONUS' 3000;Pay $t2|Out-Null
 $v=Preview @(@{fit='AMB';amount='30.00';memo='PAGTO SALARIOS'})
 Check $v.transactions[0].reviewRequired 'Casos ambíguos exigem conferência'
 $failed=$false;try{AutoConfirm $v|Out-Null}catch{$failed=([int]$_.Exception.Response.StatusCode-eq 400)}
 Check $failed 'Servidor impede criar duplicidade mesmo que a interface envie CRIAR NOVO'
 $cof=Title 'receivable' 'RESGATE COFRINHOS' 700;Pay $cof|Out-Null
 $before=(Req '/companies/1/state').accounts[0].balance
 $v=Preview @(@{fit='LEGACY';amount='7.00';memo='RESGATE COFRINHOS'})
 AutoConfirm $v|Out-Null
 $state=Req '/companies/1/state'
 Check ($state.accounts[0].balance-eq $before -and @($state.titles|Where-Object id -eq $cof.id).Count-eq 0) 'Cofrinhos registrados anteriormente são reclassificados sem mudar o saldo'
 $before=(Req '/companies/1/state').accounts[0].balance
 $bankDate=(Get-Date).AddDays(1).ToString('yyyyMMdd')
 $v=Preview @(@{fit='FUTURE';amount='-3.00';memo='LOJA FUTURA'})
 Check $v.transactions[0].future 'Movimentações futuras não podem ser baixadas automaticamente'
 $failed=$false;try{AutoConfirm $v|Out-Null}catch{$failed=([int]$_.Exception.Response.StatusCode-eq 400)}
 Check ($failed -and (Req '/companies/1/state').accounts[0].balance-eq $before) 'Data futura é recusada sem alterar saldo'
 $bankDate=Get-Date -Format yyyyMMdd
 $failed=$false;try{Preview @(@{fit='S1';amount='101.00';memo='PAGTO SALARIOS'})|Out-Null}catch{$failed=([int]$_.Exception.Response.StatusCode-eq 400)}
 Check ($failed -and (Req '/companies/1/state').accounts[0].balance-eq $before) 'FITID com valor alterado é recusado em vez de duplicado ou sobrescrito'
 $partiesBefore=@((Req '/companies/1/state').parties).Count
 $v=Preview @(@{fit='PARTY-IN';amount='11.21';memo='PIX RECEBIDO POR ANA MARIA CPF 123.456.789-00'},@{fit='PARTY-OUT';amount='-22.32';memo='PIX ENVIADO PARA MERCADO SOL'},@{fit='PARTY-EMPTY';amount='33.43';memo='PIX RECEBIDO'})
 Check (($v.transactions|Where-Object memo -like 'PIX RECEBIDO POR ANA*').suggestedParty-eq 'ANA MARIA' -and ($v.transactions|Where-Object memo -like 'PIX ENVIADO PARA MERCADO*').suggestedParty-eq 'MERCADO SOL') 'OFX separa o nome da pessoa, direção do PIX e documento'
 Check (@((Req '/companies/1/state').parties).Count-eq $partiesBefore) 'Prévia sugere nomes sem cadastrar pessoas'
 AutoConfirm $v|Out-Null
 $state=Req '/companies/1/state'
 Check (@($state.parties|Where-Object {$_.name-eq 'ANA MARIA' -and $_.type-eq 'customer'}).Count-eq 1 -and @($state.parties|Where-Object {$_.name-eq 'MERCADO SOL' -and $_.type-eq 'supplier'}).Count-eq 1) 'PIX recebido cria cliente e enviado cria fornecedor'
 Check (($state.titles|Where-Object documentNumber -eq 'PARTY-IN').partyName-eq 'ANA MARIA' -and ($state.titles|Where-Object documentNumber -eq 'PARTY-OUT').partyName-eq 'MERCADO SOL') 'Cadastros ficam vinculados aos lançamentos conciliados'
 Check ($null-eq ($state.titles|Where-Object documentNumber -eq 'PARTY-EMPTY').partyId) 'Descrição genérica não inventa cliente ou fornecedor'
 $v=Preview @(@{fit='PARTY-REUSE';amount='11.22';memo='PIX RECEBIDO DE ANA MARIA'})
 Req '/companies/1/ofx/confirm' @{importId=$v.importId;items=@(@{transactionId=$v.transactions[0].id;create=$true;confirmNew=$true})}|Out-Null
 Check (@((Req '/companies/1/state').parties|Where-Object name -eq 'ANA MARIA').Count-eq 1) 'Nova transação reaproveita cadastro existente sem duplicar pessoa'
 $v=Preview @(@{fit='PARTY-SUPPRESS';amount='-88.77';memo='PIX ENVIADO PARA EMPRESA PARA REVISAR'})
 Req '/companies/1/ofx/confirm' @{importId=$v.importId;items=@(@{transactionId=$v.transactions[0].id;create=$true;partyName=''})}|Out-Null
 Check (@((Req '/companies/1/state').parties|Where-Object name -eq 'EMPRESA PARA REVISAR').Count-eq 0) 'Usuário pode limpar nome sugerido antes de confirmar'
 'TESTES DE CONCILIAÇÃO AUTOMÁTICA PASSARAM'
} finally {
 if(!$p.HasExited){Stop-Process -Id $p.Id -Force}
 foreach($n in $names){[Environment]::SetEnvironmentVariable($n,$old[$n])}
}
