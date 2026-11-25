param([string]$ApiDll = (Join-Path $PSScriptRoot 'api\bin\Debug\net10.0\Monalisa.Financeiro.Server.dll'))
$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results\daily-login-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force|Out-Null
$names=@('FINANCE_DB','FINANCE_URL','MONALISA_NO_LOGIN','MONALISA_REQUIRE_DAILY_LOGIN','MONALISA_SHOWCASE','FINANCE_AUTH_DISABLED')
$old=@{};foreach($n in $names){$old[$n]=[Environment]::GetEnvironmentVariable($n)}
$env:FINANCE_DB=Join-Path $dir 'test.db';$env:FINANCE_URL='http://127.0.0.1:5193';$env:MONALISA_SHOWCASE='1';$env:MONALISA_NO_LOGIN='1';$env:MONALISA_REQUIRE_DAILY_LOGIN='0';Remove-Item Env:FINANCE_AUTH_DISABLED -ErrorAction SilentlyContinue
function Start-TestServer($suffix){
 $script:process=Start-Process C:\Tools\dotnet\dotnet.exe -ArgumentList $ApiDll -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir "$suffix.log") -RedirectStandardError (Join-Path $dir "$suffix-error.log")
 for($i=0;$i-lt 30;$i++){try{Invoke-RestMethod http://127.0.0.1:5193/api/health -TimeoutSec 2|Out-Null;return}catch{Start-Sleep -Milliseconds 300}}
 throw 'Servidor de teste não iniciou'
}
function Req($path,$body=$null,$token=''){
 $params=@{Uri="http://127.0.0.1:5193/api$path";Headers=@{Authorization="Bearer $token"};TimeoutSec=10}
 if($null-ne $body){$params.Method='POST';$params.ContentType='application/json';$params.Body=($body|ConvertTo-Json -Depth 8)}
 Invoke-RestMethod @params
}
function Check($value,$label){if(!$value){throw $label};"OK: $label"}
try {
 Start-TestServer 'automatic'
 $local=Req '/auth/local' @{}
 $before=Req '/companies/1/state' $null $local.token
 Stop-Process -Id $process.Id -Force;$process.WaitForExit()
 $env:MONALISA_REQUIRE_DAILY_LOGIN='1'
 Start-TestServer 'daily'
 $bootstrap=Req '/auth/bootstrap'
 Check ($bootstrap.dailyLogin -and !$bootstrap.noLogin -and $bootstrap.needsSetup) 'Base com apenas LOCAL permite configurar acesso pessoal'
 $denied=$false;try{Req '/auth/local' @{}|Out-Null}catch{$denied=([int]$_.Exception.Response.StatusCode-eq 400)}
 Check $denied 'Modo diário bloqueia acesso automático'
 $session=Req '/auth/bootstrap' @{name='TESTE';username='PESSOAL.TESTE';pin='4839271560'}
 $after=Req '/companies/1/state' $null $session.token
 Check (@($before.titles).Count-eq @($after.titles).Count -and $before.accounts[0].balance-eq $after.accounts[0].balance) 'Configuração de usuário preserva dados financeiros'
 Check (!(Req '/auth/bootstrap').needsSetup) 'Administrador não pode ser configurado novamente'
 $denied=$false;try{Req '/auth/bootstrap' @{name='OUTRO';username='OUTRO';pin='5938271640'}|Out-Null}catch{$denied=([int]$_.Exception.Response.StatusCode-eq 400)}
 Check $denied 'API rejeita tomada de controle por segunda configuração'
 $denied=$false;try{Req '/auth/login' @{username='PESSOAL.TESTE';pin='000000'}|Out-Null}catch{$denied=([int]$_.Exception.Response.StatusCode-eq 400)}
 Check $denied 'Senha incorreta recusada'
 $login=Req '/auth/login' @{username='PESSOAL.TESTE';pin='4839271560'}
 Check ((Req '/auth/me' $null $login.token).username-eq 'PESSOAL.TESTE') 'Login autenticado permite restaurar a sessão'
 Req '/auth/logout' @{} $login.token|Out-Null
 $denied=$false;try{Req '/auth/me' $null $login.token|Out-Null}catch{$denied=([int]$_.Exception.Response.StatusCode-eq 401)}
 Check $denied 'Sair invalida a sessão no servidor'
} finally {
 if($process -and !$process.HasExited){Stop-Process -Id $process.Id -Force}
 foreach($n in $names){[Environment]::SetEnvironmentVariable($n,$old[$n])}
}
