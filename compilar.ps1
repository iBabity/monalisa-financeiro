$ErrorActionPreference='Stop'
Push-Location (Join-Path $PSScriptRoot 'app')
try {
    & C:\Tools\flutter\bin\flutter.bat analyze
    if($LASTEXITCODE -ne 0){throw 'Falha na análise Flutter'}
    & C:\Tools\flutter\bin\flutter.bat test
    if($LASTEXITCODE -ne 0){throw 'Falha nos testes Flutter'}
    & C:\Tools\flutter\bin\flutter.bat build web --release --no-wasm-dry-run
    if($LASTEXITCODE -ne 0){throw 'Falha na compilação Flutter'}
    New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'api\wwwroot') | Out-Null
    Copy-Item -Path 'build\web\*' -Destination (Join-Path $PSScriptRoot 'api\wwwroot') -Recurse -Force
} finally { Pop-Location }
& C:\Tools\dotnet\dotnet.exe build (Join-Path $PSScriptRoot 'api')
if($LASTEXITCODE -ne 0){throw 'Falha na compilação C#'}
& (Join-Path $PSScriptRoot 'test-api.ps1')
