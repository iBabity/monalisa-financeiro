param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Notes,
    [string]$Repository = 'monalisa-financeiro-updates'
)

$ErrorActionPreference = 'Stop'
$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
$credential = @{}
foreach ($line in $credentialLines) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) { $credential[$parts[0]] = $parts[1] }
}
if (-not $credential.password) { throw 'Credencial do GitHub não encontrada.' }

$headers = @{
    Authorization = "Bearer $($credential.password)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'Monalisa-Financeiro-Release'
}
$api = 'https://api.github.com'
$user = Invoke-RestMethod -Uri "$api/user" -Headers $headers
$owner = $user.login
$repositoryUri = "$api/repos/$owner/$Repository"

try {
    $repo = Invoke-RestMethod -Uri $repositoryUri -Headers $headers
} catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
    $repo = Invoke-RestMethod -Uri "$api/user/repos" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{
        name = $Repository
        description = 'Instaladores e manifesto de atualização do Monalisa Financeiro. O código-fonte permanece privado.'
        private = $false
        has_issues = $false
        has_projects = $false
        has_wiki = $false
        auto_init = $true
    } | ConvertTo-Json)
}
if ($repo.private) { throw 'O repositório de instaladores precisa ser público para não expor um token nos computadores clientes.' }

$tag = "v$Version"
try {
    $release = Invoke-RestMethod -Uri "$repositoryUri/releases/tags/$tag" -Headers $headers
} catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
    $release = Invoke-RestMethod -Uri "$repositoryUri/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body (@{
        tag_name = $tag
        target_commitish = $repo.default_branch
        name = "Monalisa Financeiro $Version"
        body = $Notes
        draft = $false
        prerelease = $false
    } | ConvertTo-Json)
}

$file = Get-Item -LiteralPath $Installer
$hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
$asset = @($release.assets) | Where-Object { $_.name -eq $file.Name } | Select-Object -First 1
if (-not $asset) {
    $uploadBase = $release.upload_url -replace '\{\?name,label\}$', ''
    $asset = Invoke-RestMethod -Uri ($uploadBase + '?name=' + [Uri]::EscapeDataString($file.Name)) -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $file.FullName -TimeoutSec 1200
}

$manifest = [ordered]@{
    latestVersion = $Version
    mandatory = $true
    sha256 = $hash
    size = $file.Length
    notes = $Notes.ToUpperInvariant()
    downloadUrl = $asset.browser_download_url
    published = (Get-Date).ToUniversalTime().ToString('O')
}
$manifestText = $manifest | ConvertTo-Json
$manifestPath = 'manifest.json'
$contentUri = "$repositoryUri/contents/$manifestPath"
$existingSha = $null
try { $existingSha = (Invoke-RestMethod -Uri $contentUri -Headers $headers).sha } catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
}
$payload = @{
    message = "Publica atualização $Version"
    content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($manifestText + "`n"))
    branch = $repo.default_branch
}
if ($existingSha) { $payload.sha = $existingSha }
Invoke-RestMethod -Uri $contentUri -Method Put -Headers $headers -ContentType 'application/json' -Body ($payload | ConvertTo-Json) | Out-Null

[pscustomobject]@{
    Repository = $repo.html_url
    RepositoryPrivate = $repo.private
    Release = $release.html_url
    Asset = $asset.browser_download_url
    Manifest = "https://raw.githubusercontent.com/$owner/$Repository/$($repo.default_branch)/manifest.json"
    Version = $Version
    Sha256 = $hash
    Size = $file.Length
}
