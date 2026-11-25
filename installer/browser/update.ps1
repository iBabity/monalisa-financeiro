param([string]$Root = (Join-Path $env:LOCALAPPDATA 'MonalisaFinanceiroIndependente'), [switch]$Once)
. (Join-Path $PSScriptRoot 'common.ps1')
$mutex = New-Object Threading.Mutex($false, 'Local\MonalisaFinanceiroIndependente-Updater')
if (-not $mutex.WaitOne(0)) { exit 0 }
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    do {
        try {
            $config = Read-JsonFile (Join-Path $PSScriptRoot 'package.json')
            $updates = Join-Path $Root 'updates'
            New-Item -ItemType Directory -Path $updates -Force | Out-Null
            $incoming = Join-Path $updates 'incoming.json'
            Invoke-WebRequest -UseBasicParsing -Uri $config.manifestUrl -OutFile $incoming -TimeoutSec 15
            $manifest = Read-SignedManifest $incoming (Join-Path $Root 'update-public.xml')
            $current = Read-JsonFile (Join-Path $Root 'current.json')
            if ([version]$manifest.version -gt [version]$current.version) {
                $zip = Join-Path $updates 'package.zip'
                if (-not (Test-Path -LiteralPath $zip) -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $manifest.sha256) {
                    $download = $zip + '.download'
                    Invoke-WebRequest -UseBasicParsing -Uri $manifest.url -OutFile $download -TimeoutSec 1200
                    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $manifest.sha256 -or (Get-Item -LiteralPath $download).Length -ne $manifest.size) { throw 'O DOWNLOAD NÃO PASSOU NA VERIFICAÇÃO.' }
                    Move-Item -LiteralPath $download -Destination $zip -Force
                }
                Move-Item -LiteralPath $incoming -Destination (Join-Path $updates 'ready.json') -Force
            }
        } catch { Add-Content -LiteralPath (Join-Path $Root 'update.log') -Value ((Get-Date -Format o) + ' ' + $_.Exception.Message) }
        if (-not $Once) { Start-Sleep -Seconds 300 }
    } while (-not $Once)
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
