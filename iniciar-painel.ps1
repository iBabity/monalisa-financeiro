$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$project=Join-Path $root 'license-server'
$dll=Join-Path $project 'bin\Debug\net10.0\Monalisa.Licensing.dll'
$env:MONALISA_LICENSE_BIND='http://127.0.0.1:5200'
$env:MONALISA_LICENSE_DB=Join-Path $project 'data\licenses.db'
$env:MONALISA_LICENSE_PRIVATE_KEY=Join-Path $root '.private-license\license-signing-private.pem'
if(-not (Test-Path -LiteralPath $dll)){
    & C:\Tools\dotnet\dotnet.exe build $project
    if($LASTEXITCODE -ne 0){throw 'Não foi possível compilar o Painel Central.'}
}
$ready=$false
try{$ready=(Invoke-RestMethod 'http://127.0.0.1:5200/api/health').status -eq 'ok'}catch{}
if(-not $ready){
    New-Item -ItemType Directory -Force (Join-Path $root 'logs')|Out-Null
    Start-Process -FilePath 'C:\Tools\dotnet\dotnet.exe' -ArgumentList @($dll) -WorkingDirectory $project -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root 'logs\licensing.log') -RedirectStandardError (Join-Path $root 'logs\licensing-error.log')|Out-Null
    for($i=0;$i -lt 40;$i++){try{$ready=(Invoke-RestMethod 'http://127.0.0.1:5200/api/health').status -eq 'ok';if($ready){break}}catch{};Start-Sleep -Milliseconds 300}
}
if(-not $ready){throw 'Não foi possível iniciar o Painel Central. Consulte a pasta logs.'}
Start-Process 'http://127.0.0.1:5200'
