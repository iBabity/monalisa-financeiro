$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results/yearly-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$env:FINANCE_DB=Join-Path $dir 'test.db';$env:FINANCE_URL='http://127.0.0.1:5195';$env:FINANCE_AUTH_DISABLED='1';$env:MONALISA_INDEPENDENT='1'
$p=Start-Process C:/Tools/dotnet/dotnet.exe -ArgumentList (Join-Path $PSScriptRoot 'api/bin/Debug/net10.0/Monalisa.Financeiro.Server.dll') -WorkingDirectory (Join-Path $PSScriptRoot 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'api.log')
function Req($path,$body=$null){$args=@{Uri=('http://127.0.0.1:5195/api'+$path)};if($null-ne $body){$args.Method='POST';$args.ContentType='application/json';$args.Body=$body|ConvertTo-Json -Depth 10};Invoke-RestMethod @args}
function Check($ok,$label){if(!$ok){throw $label};"OK: $label"}
try{
 for($i=0;$i-lt 40;$i++){try{Req '/health'|Out-Null;break}catch{Start-Sleep -Milliseconds 250}}
 foreach($kind in @('payable','receivable')){
  Req '/companies/1/titles' @{kind=$kind;description=('ANUAL '+$kind);amount=45000;due='2026-11-22';category='ANUIDADE';notes='';installments=3;installmentIntervalMonths=12;plannedAccountId=1}|Out-Null
  $state=Req '/companies/1/state';$titles=@($state.titles|Where-Object kind -eq $kind|Sort-Object due)
  Check (($titles.due -join ',')-eq '2026-11-22,2027-11-22,2028-11-22' -and @($titles|Where-Object amount -eq 15000).Count-eq 3) "$kind : datas anuais e valor integral por parcela"
  Check (@($titles|Where-Object {$_.installmentIntervalMonths-eq 12 -and $_.plannedAccountId-eq 1}).Count-eq 3) "$kind : periodicidade e conta financeira preservadas"
  $id=$titles[0].id
  Req "/companies/1/titles/$id/edit" @{description='ANUAL EDITADA';amount=16000;due='2026-12-05';category='ANUIDADE';notes='';scope='all_open';plannedAccountId=1}|Out-Null
  $titles=@((Req '/companies/1/state').titles|Where-Object kind -eq $kind|Sort-Object due)
  Check (($titles.due -join ',')-eq '2026-12-05,2027-12-05,2028-12-05') "$kind : edição em conjunto mantém intervalos anuais"
 }
 Req '/companies/1/titles' @{kind='payable';description='BISSEXTO';amount=25000;due='2024-02-29';category='ANUIDADE';notes='';installments=5;installmentIntervalMonths=12}|Out-Null
 $state=Req '/companies/1/state';$leap=@($state.titles|Where-Object description -like 'BISSEXTO*'|Sort-Object due)
 Check (($leap.due -join ',')-eq '2024-02-29,2025-02-28,2026-02-28,2027-02-28,2028-02-29') 'Anos bissextos tratados pelo calendário, sem somar 365 dias'
 Check ($state.accounts[0].balance-eq 0 -and @($state.settlements).Count-eq 0) 'Geração anual não cria baixas nem altera saldos'
}finally{if(!$p.HasExited){Stop-Process -Id $p.Id -Force}}
