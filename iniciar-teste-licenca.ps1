param(
    [string]$Banco = (Join-Path $PSScriptRoot 'test-results\maria-readonly\financeiro-maria.db'),
    [int]$Porta = 5188,
    [switch]$SemNavegador
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$api = Join-Path $root 'api'
$dll = Join-Path $api 'bin\Release\net10.0\Monalisa.Financeiro.dll'
$Banco = [IO.Path]::GetFullPath($Banco)
$url = "http://127.0.0.1:$Porta"

if (-not (Test-Path -LiteralPath $Banco)) {
    New-Item -ItemType Directory -Force (Split-Path $Banco) | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'data\financeiro.db') -Destination $Banco
}
if (-not (Test-Path -LiteralPath $dll)) {
    & 'C:\Tools\dotnet\dotnet.exe' build (Join-Path $api 'Monalisa.Financeiro.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível compilar o ambiente de teste.' }
}

$ready = $false
try { $ready = (Invoke-RestMethod "$url/api/health").status -eq 'ok' } catch {}
if (-not $ready) {
    $env:FINANCE_DB = $Banco
    $env:FINANCE_URL = $url
    $env:MONALISA_LICENSE_URL = 'http://127.0.0.1:5200'
    Remove-Item Env:MONALISA_LICENSE_DISABLED -ErrorAction SilentlyContinue
    Remove-Item Env:FINANCE_AUTH_DISABLED -ErrorAction SilentlyContinue
    $logs = Join-Path $root 'logs'
    New-Item -ItemType Directory -Force $logs | Out-Null
    Start-Process -FilePath 'C:\Tools\dotnet\dotnet.exe' -ArgumentList @($dll) -WorkingDirectory $api -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logs 'teste-licenca.log') -RedirectStandardError (Join-Path $logs 'teste-licenca-error.log') | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        try { $ready = (Invoke-RestMethod "$url/api/health").status -eq 'ok'; if ($ready) { break } } catch {}
        Start-Sleep -Milliseconds 300
    }
}
if (-not $ready) { throw 'Não foi possível abrir o ambiente de teste da licença.' }
if (-not $SemNavegador) { Start-Process "$url/?v=license-test" }

