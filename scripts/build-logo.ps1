$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$settings = New-Object System.Xml.XmlReaderSettings
$settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
$reader = [System.Xml.XmlReader]::Create((Join-Path $root 'discSapoLogo.svg'), $settings)
$svg = New-Object System.Xml.XmlDocument
try { $svg.Load($reader) } finally { $reader.Dispose() }
$assets = Join-Path $root 'src\DiscordVpn\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('<DrawingImage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><DrawingImage.Drawing><DrawingGroup>')
$lines.Add('<GeometryDrawing Brush="Transparent" Geometry="M0,0 L1254,0 1254,1254 0,1254 Z"/>')
$lines.Add('<DrawingGroup Transform="1.224609,0,0,1.224609,0,0">')
foreach ($path in $svg.SelectNodes('//*[local-name()="path"]')) {
    if ($path.GetAttribute('style') -notmatch 'fill:rgb\((\d+),(\d+),(\d+)\)') { throw 'Unsupported SVG fill' }
    $color = '#{0:X2}{1:X2}{2:X2}' -f [int]$Matches[1],[int]$Matches[2],[int]$Matches[3]
    $geometry = [Security.SecurityElement]::Escape($path.GetAttribute('d'))
    $lines.Add('<GeometryDrawing Brush="' + $color + '" Geometry="F0 ' + $geometry + '"/>')
}
$lines.Add('</DrawingGroup></DrawingGroup></DrawingImage.Drawing></DrawingImage>')
$xaml = $lines -join [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $assets 'Logo.xaml'), $xaml)
$drawing = [Windows.Markup.XamlReader]::Parse($xaml)
$visual = New-Object Windows.Media.DrawingVisual
$context = $visual.RenderOpen()
$context.DrawImage($drawing, [Windows.Rect]::new(0,0,256,256))
$context.Close()
$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(256,256,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$png = New-Object IO.MemoryStream
$encoder.Save($png)
[IO.File]::WriteAllBytes((Join-Path $assets 'Logo.png'), $png.ToArray())
$icon = [IO.File]::Create((Join-Path $assets 'Logo.ico'))
$writer = [IO.BinaryWriter]::new($icon)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
    $writer.Write($png.ToArray())
} finally { $writer.Dispose(); $png.Dispose() }
