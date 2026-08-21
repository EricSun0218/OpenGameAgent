[CmdletBinding()]
param(
    [string] $Godot = 'godot.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'docs\store-listings\artwork'
$outputRoot = Join-Path $sourceRoot 'rendered'
$script = Join-Path $PSScriptRoot 'render-store-artwork.gd'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Add-Type -AssemblyName System.Drawing

function Add-Text {
    param(
        [Parameter(Mandatory = $true)] [System.Drawing.Graphics] $Graphics,
        [Parameter(Mandatory = $true)] [string] $Text,
        [Parameter(Mandatory = $true)] [float] $X,
        [Parameter(Mandatory = $true)] [float] $Y,
        [Parameter(Mandatory = $true)] [float] $Size,
        [Parameter(Mandatory = $true)] [string] $Color,
        [switch] $Bold
    )

    $style = if ($Bold) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
    $font = [Drawing.Font]::new('Segoe UI', $Size, $style, [Drawing.GraphicsUnit]::Pixel)
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($Color))
    try {
        $Graphics.DrawString($Text, $font, $brush, $X, $Y)
    }
    finally {
        $brush.Dispose()
        $font.Dispose()
    }
}

function Add-TextOverlay {
    param(
        [Parameter(Mandatory = $true)] [string] $ImagePath,
        [Parameter(Mandatory = $true)] [string] $ArtworkName
    )

    $sourceImage = [Drawing.Image]::FromFile($ImagePath)
    $bitmap = [Drawing.Bitmap]::new($sourceImage)
    $sourceImage.Dispose()
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    try {
        switch ($ArtworkName) {
            'opengameagent-store-hero' {
                $lightPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#EEE9DC'), 33.6)
                $accentPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#DE593C'), 33.6)
                $letterPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#EEE9DC'), 24)
                foreach ($pen in @($lightPen, $accentPen, $letterPen)) {
                    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Square
                    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Square
                    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Miter
                }
                try {
                    [Drawing.PointF[]] $points = @(
                        [Drawing.PointF]::new(338.4,210.4), [Drawing.PointF]::new(216,210.4),
                        [Drawing.PointF]::new(158.4,268), [Drawing.PointF]::new(158.4,383.2),
                        [Drawing.PointF]::new(216,440.8), [Drawing.PointF]::new(331.2,440.8),
                        [Drawing.PointF]::new(388.8,383.2), [Drawing.PointF]::new(388.8,325.6),
                        [Drawing.PointF]::new(309.6,325.6))
                    $graphics.DrawLines($lightPen, $points)
                    $graphics.DrawLine($accentPen, 388.8, 325.6, 336, 325.6)
                    [Drawing.PointF[]] $letterPoints = @(
                        [Drawing.PointF]::new(232.8,383.2),
                        [Drawing.PointF]::new(273.6,268),
                        [Drawing.PointF]::new(314.4,383.2))
                    $graphics.DrawLines($letterPen, $letterPoints)
                    $graphics.DrawLine($letterPen, 249.6, 340, 297.6, 340)
                }
                finally {
                    $lightPen.Dispose()
                    $accentPen.Dispose()
                    $letterPen.Dispose()
                }
                Add-Text $graphics 'OpenGameAgent' 500 280 104 '#EEE9DC' -Bold
                Add-Text $graphics 'Agent runtime for AI-native games' 505 405 38 '#B8C1D1'
                Add-Text $graphics 'Structured context' 530 536 23 '#EEE9DC' -Bold
                Add-Text $graphics 'Typed tools' 786 536 23 '#EEE9DC' -Bold
                Add-Text $graphics 'Streaming' 989 536 23 '#EEE9DC' -Bold
                Add-Text $graphics 'Multi-NPC runtime' 1180 536 23 '#FFFFFF' -Bold
                Add-Text $graphics 'Unity 6  •  Godot 4.7 .NET  •  In-process or server-hosted' 505 625 26 '#718097'
                Add-Text $graphics 'MIT licensed · opengameagent.com' 128 765 23 '#718097'
            }
            'opengameagent-runtime-flow' {
                Add-Text $graphics 'From world state to safe game action' 100 65 64 '#EEE9DC' -Bold
                Add-Text $graphics 'The model proposes. Your game remains authoritative.' 100 150 28 '#8997AC'
                foreach ($item in @(
                    @('01 · OBSERVE',126,342,22,'#DE593C',$true), @('Structured context',126,388,38,'#EEE9DC',$true),
                    @('JSON state · game time',126,459,24,'#AEB9C9',$false), @('images · events · memory',126,496,24,'#AEB9C9',$false),
                    @('02 · THINK',576,342,22,'#DE593C',$true), @('Agent loop',576,388,38,'#EEE9DC',$true),
                    @('route · plan · stream',576,459,24,'#AEB9C9',$false), @('steer · tools · retry',576,496,24,'#AEB9C9',$false),
                    @('03 · PROPOSE',1026,342,22,'#DE593C',$true), @('Typed intent',1026,388,38,'#EEE9DC',$true),
                    @('bounded schema · operation ID',1026,459,24,'#AEB9C9',$false), @('expected world revision',1026,496,24,'#AEB9C9',$false),
                    @('GAME COMMITS',1309,676,26,'#FFFFFF',$true),
                    @('Validation · permissions · resources · physics · revisions remain ordinary game code',100,758,24,'#718097',$false))) {
                    Add-Text $graphics $item[0] $item[1] $item[2] $item[3] $item[4] -Bold:$item[5]
                }
            }
            'opengameagent-engine-modes' {
                Add-Text $graphics 'One runtime. Two deployment modes.' 100 65 64 '#EEE9DC' -Bold
                Add-Text $graphics 'Start in the engine. Move authority or credentials to a service when the game needs it.' 100 150 28 '#8997AC'
                foreach ($item in @(
                    @('IN-PROCESS',145,342,22,'#DE593C',$true), @('Runtime inside the game',145,388,45,'#EEE9DC',$true),
                    @('Fast integration · direct host tools',145,482,27,'#B8C1D1',$false), @('Local models or player-provided keys',145,529,27,'#B8C1D1',$false),
                    @('Unity',234,616,27,'#EEE9DC',$false), @('Godot .NET',390,616,27,'#EEE9DC',$false),
                    @('SERVER-HOSTED',895,342,22,'#DE593C',$true), @('Runtime behind an API',895,388,45,'#EEE9DC',$true),
                    @('Protected credentials · owner auth',895,482,27,'#B8C1D1',$false), @('Durable action exchange · scale out',895,529,27,'#B8C1D1',$false),
                    @('Same protocol',968,617,25,'#FFFFFF',$false), @('Sidecar',1217,616,27,'#EEE9DC',$false),
                    @('Provider-neutral · typed streaming · actor-scoped lifecycle · no second agent loop',100,768,24,'#718097',$false))) {
                    Add-Text $graphics $item[0] $item[1] $item[2] $item[3] $item[4] -Bold:$item[5]
                }
            }
            default { throw "No text overlay is defined for '$ArtworkName'." }
        }

        $bitmap.Save($ImagePath, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

foreach ($source in Get-ChildItem -LiteralPath $sourceRoot -File -Filter '*.svg' | Sort-Object Name) {
    $destination = Join-Path $outputRoot ($source.BaseName + '.png')
    $render = Start-Process `
        -FilePath $Godot `
        -ArgumentList @(
            '--headless',
            '--script',
            ('"{0}"' -f $script),
            '--',
            ('"{0}"' -f $source.FullName),
            ('"{0}"' -f $destination)) `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($render.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        throw "Failed to render '$($source.Name)'."
    }

    Add-TextOverlay -ImagePath $destination -ArtworkName $source.BaseName

    $bytes = [IO.File]::ReadAllBytes($destination)
    if ($bytes.Length -lt 24 -or
        $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4E -or $bytes[3] -ne 0x47) {
        throw "Rendered artwork '$($source.Name)' is not a PNG."
    }

    $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
    $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
    if ($width -ne 1600 -or $height -ne 900) {
        throw "Rendered artwork '$($source.Name)' must be 1600x900, got ${width}x${height}."
    }
}

$outputRoot
