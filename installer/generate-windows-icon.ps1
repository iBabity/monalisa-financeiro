param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [string]$OutputIco = (Join-Path (Split-Path $PSScriptRoot -Parent) 'app\windows\runner\resources\app_icon.ico'),

    [string]$PreviewPng = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\icon\monalisa-financeiro-icon-256.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $SourcePng)) {
    throw "IMAGEM DE ORIGEM NÃO ENCONTRADA: $SourcePng"
}

function ConvertTo-PngBytes {
    param(
        [System.Drawing.Image]$Image,
        [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage($Image, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    } finally {
        $graphics.Dispose()
    }

    $stream = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePng))
try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = foreach ($size in $sizes) {
        [PSCustomObject]@{
            Size = $size
            Data = ConvertTo-PngBytes -Image $source -Size $size
        }
    }

    New-Item -ItemType Directory -Path (Split-Path $OutputIco -Parent), (Split-Path $PreviewPng -Parent) -Force | Out-Null

    $file = [IO.File]::Create($OutputIco)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
            $writer.Write([Byte]$dimension)
            $writer.Write([Byte]$dimension)
            $writer.Write([Byte]0)
            $writer.Write([Byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$image.Data.Length)
            $writer.Write([UInt32]$offset)
            $offset += $image.Data.Length
        }

        foreach ($image in $images) {
            $writer.Write($image.Data)
        }
    } finally {
        $writer.Dispose()
        $file.Dispose()
    }

    [IO.File]::WriteAllBytes($PreviewPng, ($images | Where-Object Size -eq 256).Data)
} finally {
    $source.Dispose()
}

Write-Host "ÍCONE WINDOWS GERADO: $OutputIco"
Write-Host "PRÉVIA 256 PX: $PreviewPng"
