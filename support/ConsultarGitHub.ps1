$ErrorActionPreference = 'Stop'

$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
$credential = @{}
foreach ($line in $credentialLines) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) { $credential[$parts[0]] = $parts[1] }
}
if (-not $credential.password) { throw 'Credencial do GitHub não encontrada no Gerenciador de Credenciais.' }

$headers = @{
    Authorization = "Bearer $($credential.password)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'Monalisa-Financeiro-Release'
}
$user = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers
$repo = Invoke-RestMethod -Uri "https://api.github.com/repos/$($user.login)/monalisa-financeiro" -Headers $headers
[pscustomobject]@{
    Login = $user.login
    SourceRepository = $repo.full_name
    SourcePrivate = $repo.private
    SourceVisibility = $repo.visibility
}
