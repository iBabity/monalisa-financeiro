param([switch]$Recompilar)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$app=Join-Path $root 'app'
$bundle=Join-Path $app 'build\windows\x64\runner\Release'
$executable=Join-Path $bundle 'MonalisaFinanceiro.exe'
$serverTarget=Join-Path $bundle 'server'

if($Recompilar -or -not (Test-Path -LiteralPath $executable)){
    Push-Location $app
    try {
        & C:\Tools\flutter\bin\flutter.bat build windows --release
        if($LASTEXITCODE -ne 0){throw 'Não foi possível compilar o aplicativo Windows.'}
    } finally { Pop-Location }
}

New-Item -ItemType Directory -Path $serverTarget -Force | Out-Null
& C:\Tools\dotnet\dotnet.exe publish (Join-Path $root 'api\Monalisa.Financeiro.csproj') -c Debug -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $serverTarget
if($LASTEXITCODE -ne 0){throw 'Não foi possível empacotar o serviço local completo.'}

$postgresBin='C:\Program Files\PostgreSQL\12\bin'
$postgresTarget=Join-Path $serverTarget 'tools\postgresql'
$postgresFiles=@('pg_restore.exe','libpq.dll','libintl-9.dll','libiconv-2.dll','zlib1.dll','libssl-1_1-x64.dll','libcrypto-1_1-x64.dll','libwinpthread-1.dll','libxml2.dll','libxslt.dll')
New-Item -ItemType Directory -Path $postgresTarget -Force | Out-Null
foreach($file in $postgresFiles){$source=Join-Path $postgresBin $file;if(-not(Test-Path -LiteralPath $source)){throw "Componente do PostgreSQL não encontrado: $source"};Copy-Item -LiteralPath $source -Destination $postgresTarget -Force}

$env:FINANCE_DB=Join-Path $root 'data\financeiro.db'
$env:FINANCE_URL='http://127.0.0.1:5187'
$env:MONALISA_LICENSE_DISABLED='1'
Start-Process -FilePath $executable -WorkingDirectory $bundle
