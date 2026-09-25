param(
  [string]$LegacyDll=(Join-Path $PSScriptRoot 'api\bin\Debug\net10.0\Monalisa.Financeiro.dll'),
  [string]$NewDll=(Join-Path $PSScriptRoot 'artifacts\api-check\Monalisa.Financeiro.dll')
)
$ErrorActionPreference='Stop'
$folder=Join-Path $PSScriptRoot ('test-results\migration-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $folder | Out-Null
$env:FINANCE_DB=Join-Path $folder 'legacy.db'
$env:FINANCE_URL='http://127.0.0.1:5198'
$env:FINANCE_AUTH_DISABLED='1'
$env:MONALISA_LICENSE_DISABLED='1'
function Call($path,$body=$null) {
  if($null -eq $body){return Invoke-RestMethod ('http://127.0.0.1:5198/api'+$path)}
  return Invoke-RestMethod ('http://127.0.0.1:5198/api'+$path) -Method Post -ContentType 'application/json' -Body ($body | ConvertTo-Json)
}
function Boot($dll,$label) {
  $script:server=Start-Process C:\Tools\dotnet\dotnet.exe -ArgumentList $dll -WorkingDirectory (Join-Path $PSScriptRoot 'api') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $folder "$label.log") -RedirectStandardError (Join-Path $folder "$label-error.log")
  for($i=0;$i -lt 40;$i++){try{Call '/health' | Out-Null;return}catch{Start-Sleep -Milliseconds 300}}
  throw 'Servidor de migração não iniciou'
}
try {
  Boot $LegacyDll 'legacy'
  Call '/companies/1/titles' @{kind='payable';description='Parcelamento antigo';amount=10001;due='2026-01-31';category='Teste';notes='Preservar';installments=3} | Out-Null
  $before=Call '/companies/1/state'
  if($before.titles[0].PSObject.Properties.Name -contains 'installmentGroup'){throw 'Este teste exige o binário anterior à migração como LegacyDll'}
  Stop-Process -Id $server.Id
  Boot $NewDll 'migrated'
  $after=Call '/companies/1/state'
  if($after.titles.Count -ne 3 -or ($after.titles.amount | Measure-Object -Sum).Sum -ne 10001){throw 'Títulos alterados na migração'}
  if(-not $after.titles[0].installmentGroup -or @($after.titles | Group-Object installmentGroup).Count -ne 1){throw 'Vínculo antigo não recuperado'}
  if(@(Get-ChildItem (Join-Path $folder 'backups') -Filter '*.db').Count -ne 1){throw 'Backup anterior à migração ausente'}
  $result=Call '/companies/1/titles/1/cancel' @{reason='Verificar migração';scope='all_open'}
  if($result.cancelledCount -ne 3){throw 'Cancelamento de parcelas migradas falhou'}
  Write-Output 'OK: Migração mantém títulos, valores, vínculos antigos e cria backup; cancelamento em conjunto aprovado.'
} finally {
  if($server -and -not $server.HasExited){Stop-Process -Id $server.Id}
  Remove-Item Env:\FINANCE_DB -ErrorAction SilentlyContinue
  Remove-Item Env:\FINANCE_URL -ErrorAction SilentlyContinue
  Remove-Item Env:\FINANCE_AUTH_DISABLED -ErrorAction SilentlyContinue
  Remove-Item Env:\MONALISA_LICENSE_DISABLED -ErrorAction SilentlyContinue
}
