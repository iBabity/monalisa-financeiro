$ErrorActionPreference='Stop'
Push-Location (Join-Path $PSScriptRoot 'app')
try {
    & C:\Tools\flutter\bin\flutter.bat analyze
    if($LASTEXITCODE -ne 0){throw 'Falha na análise Flutter'}
    & C:\Tools\flutter\bin\flutter.bat test
    if($LASTEXITCODE -ne 0){throw 'Falha nos testes Flutter'}
    & C:\Tools\flutter\bin\flutter.bat build windows --release
    if($LASTEXITCODE -ne 0){throw 'Falha na compilação Flutter'}
} finally { Pop-Location }
& C:\Tools\dotnet\dotnet.exe build (Join-Path $PSScriptRoot 'api')
if($LASTEXITCODE -ne 0){throw 'Falha na compilação C#'}
& C:\Tools\dotnet\dotnet.exe build (Join-Path $PSScriptRoot 'license-server')
if($LASTEXITCODE -ne 0){throw 'Falha na compilação do Painel Central'}
& (Join-Path $PSScriptRoot 'test-api.ps1')
