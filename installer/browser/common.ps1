$ErrorActionPreference = 'Stop'
function Read-JsonFile([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Write-AtomicJson([string]$Path, $Value) {
    $temporary = $Path + '.new'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Read-SignedManifest([string]$Path, [string]$PublicKeyPath) {
    $envelope = Read-JsonFile $Path
    $bytes = [Convert]::FromBase64String($envelope.payload)
    $signature = [Convert]::FromBase64String($envelope.signature)
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    $rsa.PersistKeyInCsp = $false
    try {
        $rsa.FromXmlString([IO.File]::ReadAllText($PublicKeyPath))
        if (-not $rsa.VerifyData($bytes, 'SHA256', $signature)) { throw 'ASSINATURA DA ATUALIZAÇÃO INVÁLIDA.' }
    } finally { $rsa.Dispose() }
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if ($manifest.channel -ne 'browser-independent' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'MANIFESTO DE ATUALIZAÇÃO INVÁLIDO.' }
    $uri = [Uri]$manifest.url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or -not $uri.AbsolutePath.StartsWith('/iBabity/monalisa-financeiro-browser-updates/releases/download/')) { throw 'ORIGEM DA ATUALIZAÇÃO INVÁLIDA.' }
    return $manifest
}
function Expand-VerifiedPackage([string]$Zip, [string]$Destination, $Manifest) {
    if ((Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash -ne $Manifest.sha256) { throw 'ARQUIVO DA ATUALIZAÇÃO CORROMPIDO.' }
    if ((Get-Item -LiteralPath $Zip).Length -ne $Manifest.size) { throw 'TAMANHO DA ATUALIZAÇÃO INVÁLIDO.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $total = 0L
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\','/')
            $total += $entry.Length
            if ($total -gt 2GB -or $name.StartsWith('/') -or $name.Contains(':') -or $name.Split('/') -contains '..' -or $name -match '(?i)(^|/)(data|backups|\.git|\.private-support)/|\.db($|-)|\.ofx$|\.license\.json$|\.env($|\.)|credentials|private.*key') { throw 'O PACOTE CONTÉM CAMINHO OU DADOS PROIBIDOS.' }
        }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($Zip, $Destination)
    $info = Read-JsonFile (Join-Path $Destination 'package.json')
    if ($info.channel -ne 'browser-independent' -or $info.version -ne $Manifest.version -or -not (Test-Path -LiteralPath (Join-Path $Destination 'server/Monalisa.Financeiro.Server.exe')) -or -not (Test-Path -LiteralPath (Join-Path $Destination 'server/wwwroot/index.html'))) { throw 'PACOTE INCOMPLETO OU DE OUTRA EDIÇÃO.' }
}
function Test-OwnServer([string]$Root) {
    $prefix = [IO.Path]::GetFullPath((Join-Path $Root 'versions')).TrimEnd('\') + '\'
    return @((Get-Process -Name Monalisa.Financeiro.Server -ErrorAction SilentlyContinue) | Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
}
function Backup-IndependentData([string]$Root) {
    $database = Join-Path $Root 'data/financeiro.db'
    if (-not (Test-Path -LiteralPath $database)) { return }
    if (Test-OwnServer $Root) { throw 'AGUARDE O PRÓXIMO INÍCIO DO COMPUTADOR PARA ATUALIZAR COM SEGURANÇA.' }
    $backup = Join-Path $Root ('backups/antes-atualizacao-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($suffix in @('', '-wal', '-shm')) {
        $source = $database + $suffix
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $backup }
    }
}
function Apply-ReadyUpdate([string]$Root, [string]$PublicKeyPath) {
    $ready = Join-Path $Root 'updates/ready.json'
    $zip = Join-Path $Root 'updates/package.zip'
    if (-not (Test-Path -LiteralPath $ready) -or (Test-OwnServer $Root)) { return }
    $manifest = Read-SignedManifest $ready $PublicKeyPath
    $current = Read-JsonFile (Join-Path $Root 'current.json')
    if ([version]$manifest.version -le [version]$current.version) { return }
    $staging = Join-Path $Root ('versions/.staging-' + [Guid]::NewGuid().ToString('N'))
    Expand-VerifiedPackage $zip $staging $manifest
    Backup-IndependentData $Root
    $target = Join-Path $Root ('versions/' + $manifest.version)
    if (Test-Path -LiteralPath $target) { throw 'A VERSÃO JÁ EXISTE. CONSULTE O LOG DE ATUALIZAÇÃO.' }
    Move-Item -LiteralPath $staging -Destination $target
    # Commit only after validating the complete package and backing up this machine's DB.
    Write-AtomicJson (Join-Path $Root 'current.json') @{ version = $manifest.version; previousVersion = $current.version }
}
