$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results/yearly-card-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$env:FINANCE_DB=Join-Path $dir 'test.db';$env:FINANCE_URL='http://127.0.0.1:5195';$env:FINANCE_AUTH_DISABLED='1';$env:MONALISA_INDEPENDENT='1'
$p=Start-Process C:/Tools/dotnet/dotnet.exe -ArgumentList (Join-Path $PSScriptRoot 'api/bin/Debug/net10.0/Monalisa.Financeiro.Server.dll') -WorkingDirectory (Join-Path $PSScriptRoot 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'api.log')
function Req($path,$body=$null){$args=@{Uri=('http://127.0.0.1:5195/api'+$path)};if($null-ne $body){$args.Method='POST';$args.ContentType='application/json';$args.Body=$body|ConvertTo-Json -Depth 10};Invoke-RestMethod @args}
function Check($ok,$label){if(!$ok){throw $label};"OK: $label"}
try{
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 250}}
 $today=Get-Date -Format yyyy-MM-dd;$year=(Get-Date).Year
 $card=Req '/companies/1/credit-cards' @{name='CARTAO ANUAL';closingDay=31;dueDay=10;accountId=1;lastFour='1234'}
 $base="/companies/1/credit-cards/$($card.id)/purchases"
 $buy=Req $base @{description='COBRANCA ANUAL FINITA';amount=15000;purchaseDate=$today;mode='yearly';installments=3;category='ANUIDADES';notes=''}
 $months=@($buy.installments.referenceMonth)
 Check ($months.Count-eq 3 -and $months[0].Substring(5)-eq $months[1].Substring(5) -and [int]$months[1].Substring(0,4)-eq ($year+1) -and [int]$months[2].Substring(0,4)-eq ($year+2)) 'Parcelas anuais entram em faturas separadas por ano'
 Check (@($buy.installments|Where-Object amount -eq 15000).Count-eq 3) 'Valor integral repetido por ano, sem dividir'
 $annual=Req $base @{description='ASSINATURA ANUAL';amount=9900;purchaseDate=$today;mode='recurring_yearly';installments=1;category='ASSINATURAS';notes=''}
 $state=Req '/companies/1/state';$rows=@($state.creditCardPurchases|Where-Object recurringChargeId -eq $annual.recurrenceId)
 Check ($rows.Count-eq 1 -and ($state.creditCardRecurringCharges|Where-Object id -eq $annual.recurrenceId).intervalMonths-eq 12) 'Recorrência anual gera só o ciclo necessário, sem cobranças mensais'
 Req '/companies/1/credit-card-recurrences/refresh' @{}|Out-Null
 Check (@((Req '/companies/1/state').creditCardPurchases|Where-Object recurringChargeId -eq $annual.recurrenceId).Count-eq 1) 'Recalcular não duplica cobrança anual'
 $future=Req $base @{description='ANUAL FUTURA';amount=2500;purchaseDate=(Get-Date).AddYears(1).ToString('yyyy-MM-dd');mode='recurring_yearly';category='ASSINATURAS';notes=''}
 Check ($future.generated-eq 0) 'Recorrência respeita a data própria, sem usar a data de criação do cartão'
 $group=$rows[0].purchaseGroup
 Req "/companies/1/credit-card-purchases/$group/delete" @{reason='TESTE EXCLUSAO'}|Out-Null
 Req '/companies/1/credit-card-recurrences/refresh' @{}|Out-Null
 Check (@((Req '/companies/1/state').creditCardPurchases|Where-Object recurringChargeId -eq $annual.recurrenceId).Count-eq 0) 'Ocorrência anual excluída não reaparece'
 Req "/companies/1/credit-card-recurrences/$($annual.recurrenceId)/stop" @{reason='TESTE ENCERRAMENTO'}|Out-Null
 $state=Req '/companies/1/state'
 Check (($state.creditCardRecurringCharges|Where-Object id -eq $annual.recurrenceId).active-eq 0) 'Recorrência anual pode ser encerrada'
 Check ($state.accounts[0].balance-eq 0 -and @($state.settlements).Count-eq 0) 'Compras anuais não criam baixas automáticas'
}finally{if(!$p.HasExited){Stop-Process -Id $p.Id -Force}}
