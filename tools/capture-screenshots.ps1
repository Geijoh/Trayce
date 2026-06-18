param(
    [string]$OutDir = "$PSScriptRoot\..\assets\screenshots",
    [string]$WorkDir = "$PSScriptRoot\..\out\readme-screenshots"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$WorkDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDir)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

Push-Location $root
try {
    dotnet run -- --render-all $WorkDir
}
finally {
    Pop-Location
}

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies "System.Drawing.dll" -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

public static class TrayceScreenshotMask
{
    public static void SaveRoundedPng(string inputPath, string outputPath, int radius)
    {
        using (var source = new Bitmap(inputPath))
        using (var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(output))
        using (var brush = new TextureBrush(source, WrapMode.Clamp))
        using (var path = RoundedPath(source.Width, source.Height, radius))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillPath(brush, path);
            output.Save(outputPath, ImageFormat.Png);
        }
    }

    static GraphicsPath RoundedPath(int width, int height, int radius)
    {
        // ponytail: one mask for generated README shots; tune per-surface if the preview renderer changes shape.
        float inset = 1f;
        float diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(inset, inset, diameter, diameter, 180, 90);
        path.AddArc(width - inset - diameter, inset, diameter, diameter, 270, 90);
        path.AddArc(width - inset - diameter, height - inset - diameter, diameter, diameter, 0, 90);
        path.AddArc(inset, height - inset - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
"@

$shots = @(
    @{ Source = "tray-dark.png"; Target = "tray-icons.png"; Radius = 10 },
    @{ Source = "settings-light.png"; Target = "settings-main.png"; Radius = 12 },
    @{ Source = "flyout-ok-dark.png"; Target = "details-flyout.png"; Radius = 10 },
    @{ Source = "menu-dark.png"; Target = "context-menu.png"; Radius = 8 }
)

foreach ($shot in $shots) {
    $source = Join-Path $WorkDir $shot.Source
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing rendered screenshot: $source"
    }

    [TrayceScreenshotMask]::SaveRoundedPng(
        $source,
        (Join-Path $OutDir $shot.Target),
        [int]$shot.Radius
    )
}

Write-Host "Wrote README screenshots to $OutDir"
