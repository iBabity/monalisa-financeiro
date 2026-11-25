param([string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/browser'), [switch]$SkipBuild)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$pubspec=Get-Content (Join-Path $project 'app/pubspec.yaml') -Raw
$version=[regex]::Match($pubspec,'(?m)^version:\s*(\d+\.\d+\.\d+)').Groups[1].Value
if(-not $version){throw 'VERSÃO NÃO IDENTIFICADA.'}
$build=Join-Path $project ('tmp/browser-package-'+$version+'-'+[Guid]::NewGuid().ToString('N'))
$payload=Join-Path $build 'payload'
New-Item -ItemType Directory -Path $payload,$OutputDirectory -Force | Out-Null
if(-not $SkipBuild){
    Push-Location (Join-Path $project 'app')
    try{& C:/Tools/flutter/bin/flutter.bat build web --release --no-wasm-dry-run; if($LASTEXITCODE -ne 0){throw 'BUILD WEB FALHOU.'}}finally{Pop-Location}
}
& C:/Tools/dotnet/dotnet.exe publish (Join-Path $project 'api/Monalisa.Financeiro.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $payload 'server')
if($LASTEXITCODE -ne 0){throw 'BUILD DO SERVIDOR FALHOU.'}
New-Item -ItemType Directory -Path (Join-Path $payload 'server/wwwroot') -Force | Out-Null
Copy-Item -Path (Join-Path $project 'app/build/web/*') -Destination (Join-Path $payload 'server/wwwroot') -Recurse -Force
# Copy only explicitly listed program assets. Never copy local data/configuration.
foreach($name in @('common.ps1','launch.ps1','update.ps1','start.vbs')){
    $source=Join-Path $PSScriptRoot ('browser/'+$name)
    $text=[IO.File]::ReadAllText($source)
    [IO.File]::WriteAllText((Join-Path $payload $name),$text,[Text.UTF8Encoding]::new($true))
}
Copy-Item -LiteralPath (Join-Path $project 'app/windows/runner/resources/app_icon.ico') -Destination (Join-Path $payload 'monalisa.ico')
$privateRoot=Join-Path $project '.private-support'
New-Item -ItemType Directory -Path $privateRoot -Force | Out-Null
$keyPath=Join-Path $privateRoot 'browser-update-signing.dpapi'
Add-Type -AssemblyName System.Security
$rsa=[Security.Cryptography.RSA]::Create(3072)
try{
    if(Test-Path -LiteralPath $keyPath){
        $privateBytes=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyPath),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($privateBytes))
    }else{
        $privateBytes=[Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
        [IO.File]::WriteAllBytes($keyPath,[Security.Cryptography.ProtectedData]::Protect($privateBytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    }
    [IO.File]::WriteAllText((Join-Path $payload 'update-public.xml'),$rsa.ToXmlString($false))
    $config=@{version=$version;channel='browser-independent';manifestUrl='https://raw.githubusercontent.com/iBabity/monalisa-financeiro-browser-updates/main/browser-manifest.json'}
    [IO.File]::WriteAllText((Join-Path $payload 'package.json'),($config|ConvertTo-Json))
    $forbidden=@(Get-ChildItem -LiteralPath $payload -Recurse -File|Where-Object{$_.Name-match '(?i)\.db($|-)|\.ofx$|\.license\.json$|^\.env|credentials|private.*key'})
    if($forbidden.Count){throw 'PACOTE RECUSADO: DADOS LOCAIS ENCONTRADOS.'}
    $zip=Join-Path $OutputDirectory ('Monalisa-Financeiro-Navegador-'+$version+'.zip')
    if(Test-Path -LiteralPath $zip){throw 'ARQUIVO DE VERSÃO JÁ EXISTE. USE OUTRA PASTA PARA RECOMPILAR.'}
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $manifest=@{version=$version;channel='browser-independent';sha256=$hash;size=(Get-Item $zip).Length;url=('https://github.com/iBabity/monalisa-financeiro-browser-updates/releases/download/v'+$version+'/'+[IO.Path]::GetFileName($zip));published=[DateTime]::UtcNow.ToString('O')}
    $bytes=[Text.Encoding]::UTF8.GetBytes(($manifest|ConvertTo-Json -Compress))
    $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $signed=@{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($signature)}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'browser-manifest.json'),($signed|ConvertTo-Json))
}finally{$rsa.Dispose();if($privateBytes){[Array]::Clear($privateBytes,0,$privateBytes.Length)}}
[IO.File]::WriteAllText((Join-Path $build 'payload.sha256'),$hash)
$install=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'browser/install.ps1'))
[IO.File]::WriteAllText((Join-Path $build 'install.ps1'),$install,[Text.UTF8Encoding]::new($true))
$assembly='using System.Reflection; [assembly:AssemblyFileVersion("'+$version+'.0")] [assembly:AssemblyVersion("'+$version+'.0")]'
[IO.File]::WriteAllText((Join-Path $build 'version.cs'),$assembly)
$exe=Join-Path $OutputDirectory ('Monalisa-Financeiro-Navegador-'+$version+'-Setup.exe')
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $csc /nologo /target:winexe /platform:anycpu "/out:$exe" "/win32icon:$payload/monalisa.ico" "/win32manifest:$PSScriptRoot/browser/user.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$zip,payload.zip" "/resource:$build/payload.sha256,payload.sha256" "/resource:$build/install.ps1,install.ps1" (Join-Path $PSScriptRoot 'browser/bootstrapper.cs') (Join-Path $build 'version.cs')
if($LASTEXITCODE -ne 0){throw 'BUILD DO INSTALADOR FALHOU.'}
$result=@{version=$version;installer=$exe;archive=$zip;payload=$payload;manifest=(Join-Path $OutputDirectory 'browser-manifest.json');installerSha256=(Get-FileHash $exe -Algorithm SHA256).Hash}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-result.json'),($result|ConvertTo-Json))
$result|ConvertTo-Json
