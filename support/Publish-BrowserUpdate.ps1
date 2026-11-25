param([Parameter(Mandatory=$true)][string]$BuildResult)
$ErrorActionPreference='Stop'
$build=Get-Content -LiteralPath $BuildResult -Raw | ConvertFrom-Json
$lines="protocol=https`nhost=github.com`n`n" | git credential fill
$credential=@{}
foreach($line in $lines){$pair=$line -split '=',2;if($pair.Length-eq 2){$credential[$pair[0]]=$pair[1]}}
if(-not $credential.password){throw 'CREDENCIAL DO GITHUB NÃO ENCONTRADA.'}
$headers=@{Authorization="Bearer $($credential.password)";Accept='application/vnd.github+json';'User-Agent'='Monalisa-Browser-Distribution';'X-GitHub-Api-Version'='2022-11-28'}
$repository='https://api.github.com/repos/iBabity/monalisa-financeiro-browser-updates'
function GitHub($url,$method='GET',$body=$null){
    $args=@{Uri=$url;Method=$method;Headers=$headers}
    if($body){$args.ContentType='application/json';$args.Body=$body|ConvertTo-Json -Depth 8}
    Invoke-RestMethod @args
}
try{$repo=GitHub $repository}catch{
    if([int]$_.Exception.Response.StatusCode-ne 404){throw}
    $repo=GitHub 'https://api.github.com/user/repos' 'POST' @{name='monalisa-financeiro-browser-updates';description='Distribuição do Monalisa Financeiro para navegador. Instalador e atualizações assinadas; sem banco de dados ou código-fonte.';private=$false;auto_init=$true;has_issues=$false;has_projects=$false;has_wiki=$false}
}
if($repo.private){throw 'O CANAL DE DISTRIBUIÇÃO DEVE SER PÚBLICO, SEM TOKENS NOS CLIENTES.'}
if($repo.owner.login-ne 'iBabity'){throw 'PROPRIETÁRIO INESPERADO.'}
$tag='v'+$build.version
try{$release=GitHub ($repository+'/releases/tags/'+$tag)}catch{
    if([int]$_.Exception.Response.StatusCode-ne 404){throw}
    $release=GitHub ($repository+'/releases') 'POST' @{tag_name=$tag;target_commitish=$repo.default_branch;name=('Monalisa Financeiro Navegador '+$build.version);body='INSTALAÇÃO INDEPENDENTE PARA WINDOWS 10/11 X64. BANCO NOVO NO PRIMEIRO USO. ATUALIZAÇÕES AUTOMÁTICAS ASSINADAS. NÃO INCLUI DADOS, USUÁRIOS OU SENHAS DE OUTRA INSTALAÇÃO.';draft=$false;prerelease=$false}
}
$urls=@()
foreach($path in @($build.archive,$build.installer)){
    $file=Get-Item -LiteralPath $path
    $asset=@($release.assets)|Where-Object{$_.name-eq $file.Name}|Select-Object -First 1
    if($asset){throw 'UMA VERSÃO JÁ PUBLICADA É IMUTÁVEL. INCREMENTE A VERSÃO.'}
    $upload=($release.upload_url -replace '\{\?name,label\}$','')+'?name='+[Uri]::EscapeDataString($file.Name)
    $asset=Invoke-RestMethod -Uri $upload -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $file.FullName -TimeoutSec 1200
    $urls+=$asset.browser_download_url
}
$manifestPath=$repository+'/contents/browser-manifest.json'
$existing=$null
try{$existing=GitHub $manifestPath}catch{if([int]$_.Exception.Response.StatusCode-ne 404){throw}}
$body=@{message=('Atualização navegador '+$build.version);branch=$repo.default_branch;content=[Convert]::ToBase64String([IO.File]::ReadAllBytes($build.manifest))}
if($existing){$body.sha=$existing.sha}
GitHub $manifestPath 'PUT' $body | Out-Null
[pscustomobject]@{release=$release.html_url;installer=$urls[1];package=$urls[0];manifest=('https://raw.githubusercontent.com/iBabity/monalisa-financeiro-browser-updates/'+$repo.default_branch+'/browser-manifest.json')}|ConvertTo-Json
