param([Parameter(Mandatory=$true)][string]$BuildResult)
$ErrorActionPreference='Stop'
$project=$PSScriptRoot
$build=Get-Content -LiteralPath $BuildResult -Raw|ConvertFrom-Json
$root=Join-Path $project ('test-results/browser-install-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force|Out-Null
$envNames=@('FINANCE_DB','FINANCE_URL','MONALISA_INDEPENDENT','MONALISA_REQUIRE_DAILY_LOGIN','MONALISA_SHOWCASE','MONALISA_NO_LOGIN','FINANCE_AUTH_DISABLED')
$old=@{};foreach($name in $envNames){$old[$name]=[Environment]::GetEnvironmentVariable($name)}
function Check($value,$label){if(-not $value){throw $label};Write-Output ('OK: '+$label)}
function Api($path,$body=$null,$token=''){
    $args=@{Uri=('http://127.0.0.1:5196/api'+$path);Headers=@{Authorization=('Bearer '+$token)};TimeoutSec=10}
    if($null-ne $body){$args.Method='POST';$args.ContentType='application/json';$args.Body=$body|ConvertTo-Json -Depth 10}
    Invoke-RestMethod @args
}
function Start-Api{
    $env:FINANCE_DB=Join-Path $root 'data/financeiro.db';$env:FINANCE_URL='http://127.0.0.1:5196';$env:MONALISA_INDEPENDENT='1';$env:MONALISA_REQUIRE_DAILY_LOGIN='1'
    foreach($name in @('MONALISA_SHOWCASE','MONALISA_NO_LOGIN','FINANCE_AUTH_DISABLED')){Remove-Item ('Env:\'+$name) -ErrorAction SilentlyContinue}
    $exe=Join-Path $root ('versions/'+$build.version+'/server/Monalisa.Financeiro.Server.exe')
    $script:process=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root ('server-'+[guid]::NewGuid().ToString('N')+'.log'))
    for($i=0;$i-lt 60;$i++){try{Api '/health'|Out-Null;return}catch{Start-Sleep -Milliseconds 250}}
    throw 'SERVIDOR DE TESTE NÃO INICIOU.'
}
try{
    & "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $project 'installer/browser/install.ps1') -PreparedPayloadDirectory $build.payload -Root $root -NoLaunch -NoShortcuts
    Check ($LASTEXITCODE-eq 0) 'Instalação limpa compatível com Windows PowerShell 5.1'
    Check (-not(Test-Path (Join-Path $root 'data/financeiro.db'))) 'Instalador não transporta banco de dados'
    . (Join-Path $build.payload 'common.ps1')
    $manifest=Read-SignedManifest $build.manifest (Join-Path $root 'update-public.xml')
    Check ($manifest.version-eq $build.version) 'Assinatura RSA do canal validada'
    $bad=Get-Content $build.manifest -Raw|ConvertFrom-Json
    $bad.payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"version":"99.0.0"}'))
    $badPath=Join-Path $root 'invalid-manifest.json';$bad|ConvertTo-Json|Set-Content $badPath
    $refused=$false;try{Read-SignedManifest $badPath (Join-Path $root 'update-public.xml')|Out-Null}catch{$refused=$true}
    Check $refused 'Manifesto adulterado recusado'
    Start-Api
    $license=Api '/license/status';Check ($license.status-eq 'active' -and !$license.showcase) 'Edição independente ativa sem licença e sem demonstração'
    $auth=Api '/auth/bootstrap';Check ($auth.needsSetup -and $auth.dailyLogin -and !$auth.noLogin) 'Primeiro uso exige novo usuário e senha locais'
    $pin=[string](Get-Random -Minimum 10000000 -Maximum 99999999)
    $session=Api '/auth/bootstrap' @{name='TESTE ISOLADO';username='TESTE.INSTALADOR';pin=$pin}
    $state=Api '/companies/1/state' $null $session.token
    Check (@($state.titles).Count-eq 0 -and @($state.settlements).Count-eq 0 -and @($state.movements).Count-eq 0) 'Nenhum lançamento, baixa ou movimentação da máquina de origem'
    Check ($state.accounts[0].balance-eq 0) 'Conta inicial com saldo zero'
    Check ((Api '/companies' $null $session.token)[0].name-eq 'MINHA EMPRESA') 'Empresa nova independente'
    $title=Api '/companies/1/titles' @{kind='payable';description='TESTE PRESERVAÇÃO';amount=12550;due='2026-12-15';category='DESPESAS GERAIS';notes='';count=1} $session.token
    # No replacement while this instance is active.
    New-Item -ItemType Directory -Path (Join-Path $root 'updates') -Force|Out-Null
    Copy-Item $build.manifest (Join-Path $root 'updates/ready.json')
    Copy-Item $build.archive (Join-Path $root 'updates/package.zip')
    Write-AtomicJson (Join-Path $root 'current.json') @{version='1.1.0'}
    Apply-ReadyUpdate $root (Join-Path $root 'update-public.xml')
    Check ((Read-JsonFile (Join-Path $root 'current.json')).version-eq '1.1.0') 'Atualização não interrompe serviço em uso'
    Stop-Process -Id $process.Id -Force;$process.WaitForExit()
    # Move the test installation aside so update installs a fresh immutable version.
    $versionPath=Join-Path $root ('versions/'+$build.version)
    Move-Item -LiteralPath $versionPath -Destination ($versionPath+'-test-old')
    Apply-ReadyUpdate $root (Join-Path $root 'update-public.xml')
    Check ((Read-JsonFile (Join-Path $root 'current.json')).version-eq $build.version) 'Atualização assinada aplicada à instalação de teste'
    Check (@(Get-ChildItem (Join-Path $root 'backups') -Directory).Count-gt 0) 'Backup local criado antes da atualização'
    Start-Api
    $login=Api '/auth/login' @{username='TESTE.INSTALADOR';pin=$pin}
    $after=Api '/companies/1/state' $null $login.token
    Check (@($after.titles).Count-eq 1 -and $after.titles[0].description-eq 'TESTE PRESERVAÇÃO' -and $after.titles[0].amount-eq 12550) 'Atualização preserva banco, lançamento e senha próprios'
    Write-Output ('TESTES CONCLUÍDOS: '+$root)
}finally{
    if($process -and !$process.HasExited){Stop-Process -Id $process.Id -Force}
    foreach($name in $envNames){[Environment]::SetEnvironmentVariable($name,$old[$name])}
}
