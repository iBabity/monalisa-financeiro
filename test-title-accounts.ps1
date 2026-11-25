$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results/title-accounts-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$env:FINANCE_DB=Join-Path $dir 'test.db';$env:FINANCE_URL='http://127.0.0.1:5195';$env:FINANCE_AUTH_DISABLED='1';$env:MONALISA_INDEPENDENT='1'
$dll=Join-Path $PSScriptRoot 'api/bin/Debug/net10.0/Monalisa.Financeiro.Server.dll'
$p=Start-Process C:/Tools/dotnet/dotnet.exe -ArgumentList $dll -WorkingDirectory (Join-Path $PSScriptRoot 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'api.log') -RedirectStandardError (Join-Path $dir 'error.log')
function Req($path,$body=$null){$args=@{Uri=('http://127.0.0.1:5195/api'+$path)};if($null-ne $body){$args.Method='POST';$args.ContentType='application/json';$args.Body=$body|ConvertTo-Json -Depth 10};Invoke-RestMethod @args}
function Check($ok,$label){if(!$ok){throw $label};"OK: $label"}
try{
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 250}}
 Req '/companies/1/accounts' @{name='ITAU';opening=0;type='bank'}|Out-Null
 $state=Req '/companies/1/state';$account=($state.accounts|Where-Object name -eq ITAU).id
 $due=Get-Date -Format yyyy-MM-dd
 $input=@{kind='payable';description='TESTE CONTAS';amount=20000;due=$due;category='DESPESAS GERAIS';notes='';installments=2;plannedAccountId=$account}
 Req '/companies/1/titles' $input|Out-Null
 $state=Req '/companies/1/state'
 Check (@($state.titles|Where-Object plannedAccountId -eq $account).Count-eq 2) 'Conta prevista é mantida em todas as parcelas'
 Check ($state.accounts[0].balance-eq 0 -and $state.accounts[1].balance-eq 0) 'Conta prevista não movimenta saldos'
 $id=$state.titles[0].id
 $copy=Req "/companies/1/titles/$id/duplicate" @{due=$due}
 Check (((Req '/companies/1/state').titles|Where-Object id -eq $copy.id).plannedAccountId-eq $account) 'Duplicação mantém conta prevista'
 $edit=@{description='CONTA ALTERADA';amount=10000;due=$due;category='DESPESAS GERAIS';notes='';scope='all_open';plannedAccountId=1}
 Req "/companies/1/titles/$id/edit" $edit|Out-Null
 $state=Req '/companies/1/state'
 Check (@($state.titles|Where-Object installmentCount -eq 2|Where-Object plannedAccountId -eq 1).Count-eq 2) 'Edição das parcelas troca conta prevista'
 Req "/companies/1/titles/$id/settle" @{accountId=$account;principal=10000;interest=0;penalty=0;discount=0;date=$due}|Out-Null
 $state=Req '/companies/1/state'
 Check ($state.settlements[0].accountId-eq $account -and ($state.accounts|Where-Object id -eq $account).balance-eq -10000 -and ($state.accounts|Where-Object id -eq 1).balance-eq 0) 'Baixa em conta diferente movimenta somente a conta efetiva'
 $input.kind='receivable';$input.description='RECEBIMENTO CONTA';$input.installments=1;$input.amount=12000
 Req '/companies/1/titles' $input|Out-Null
 Check (((Req '/companies/1/state').titles|Where-Object kind -eq receivable).plannedAccountId-eq $account) 'Contas a receber também guarda conta prevista'
 $input.plannedAccountId=99999;$rejected=$false;try{Req '/companies/1/titles' $input|Out-Null}catch{$rejected=[int]$_.Exception.Response.StatusCode-eq 400}
 Check $rejected 'Conta inexistente ou de outra empresa é recusada'
 $rejected=$false;try{Req "/companies/1/accounts/$account/delete" @{}|Out-Null}catch{$rejected=[int]$_.Exception.Response.StatusCode-eq 400}
 Check $rejected 'Exclusão informa vínculo com lançamentos previstos'
}finally{if(!$p.HasExited){Stop-Process -Id $p.Id -Force}}
