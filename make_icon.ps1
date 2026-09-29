Add-Type -AssemblyName System.Drawing
$sizes = @(16,24,32,48,64,128,256)
$parts = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(21,59,92))
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(228,241,250))
    $arrowPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(89,200,245),24)
    $checkPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(18,143,112),18)
    $arrowPen.StartCap = $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $checkPen.StartCap = $checkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.FillEllipse($blue,8,8,240,240)
    $g.FillRectangle($white,44,145,168,72)
    $g.DrawLine($arrowPen,128,42,128,128)
    $g.DrawLines($arrowPen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(91,101),[System.Drawing.Point]::new(128,134),[System.Drawing.Point]::new(165,101)))
    $g.DrawLines($checkPen,[System.Drawing.Point[]]@([System.Drawing.Point]::new(97,177),[System.Drawing.Point]::new(119,198),[System.Drawing.Point]::new(161,154)))
    $stream = [System.IO.MemoryStream]::new()
    $bmp.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $parts.Add($stream.ToArray())
    $stream.Dispose(); $g.Dispose(); $bmp.Dispose(); $blue.Dispose(); $white.Dispose(); $arrowPen.Dispose(); $checkPen.Dispose()
}
$path = Join-Path $PSScriptRoot 'Closed_Stack_Downloader\Assets\app.ico'
$fs = [System.IO.File]::Create($path); $w = [System.IO.BinaryWriter]::new($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0; $i -lt $sizes.Count; $i++) {
    $n=$sizes[$i]; $w.Write([byte]($n % 256)); $w.Write([byte]($n % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$parts[$i].Length); $w.Write([uint32]$offset)
    $offset += $parts[$i].Length
}
foreach ($part in $parts) { $w.Write($part) }
$w.Dispose()
