$ErrorActionPreference='Stop'
$dir=Join-Path $PSScriptRoot ('test-results\site-origin-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$origin='https://monalisa-financeiro-pessoal.ctrl-alt-def-2555.chatgpt.site'
$names=@('FINANCE_DB','FINANCE_URL','MONALISA_WEB_ORIGIN','MONALISA_SHOWCASE','MONALISA_NO_LOGIN')
$previous=@{}; foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name)}
$env:FINANCE_DB=Join-Path $dir 'test.db'
$env:FINANCE_URL='http://127.0.0.1:5194'
$env:MONALISA_WEB_ORIGIN=$origin
$env:MONALISA_SHOWCASE='1'
$env:MONALISA_NO_LOGIN='1'
$p=Start-Process (Join-Path $PSScriptRoot 'tmp\personal-site\server\Monalisa.Financeiro.Server.exe') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dir 'out.log') -RedirectStandardError (Join-Path $dir 'error.log')
try {
 for($i=0;$i-lt 30;$i++){try{Invoke-RestMethod http://127.0.0.1:5194/api/health -TimeoutSec 2|Out-Null;break}catch{Start-Sleep -Milliseconds 300}}
 $headers=@{Origin=$origin;'Access-Control-Request-Method'='POST';'Access-Control-Request-Headers'='authorization,content-type';'Access-Control-Request-Private-Network'='true'}
 $r=Invoke-WebRequest http://127.0.0.1:5194/api/auth/local -Method Options -Headers $headers
 if($r.StatusCode-ne 204 -or $r.Headers['Access-Control-Allow-Origin']-ne $origin){throw 'Preflight inválido'}
 $r=Invoke-WebRequest http://127.0.0.1:5194/api/auth/local -Method Post -Headers @{Origin=$origin} -ContentType application/json -Body '{}'
 if($r.StatusCode-ne 200){throw 'Sessão local indisponível'}
 $bad=Invoke-WebRequest http://127.0.0.1:5194/api/auth/local -Method Post -Headers @{Origin='https://outro-site.example'} -ContentType application/json -Body '{}' -SkipHttpErrorCheck
 if($bad.StatusCode-ne 403){throw 'Origem indevida aceita'}
 Write-Output 'CORS verificado: Site pessoal aceito; outros sites bloqueados; banco de teste isolado.'
}finally{
 if(!$p.HasExited){Stop-Process -Id $p.Id}
 foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$previous[$name])}
}
