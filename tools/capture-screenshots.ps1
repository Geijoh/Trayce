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
    dotnet build --nologo | Write-Host

    $exe = Join-Path $root "bin\Debug\net8.0-windows\win-x64\Trayce.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Missing built renderer: $exe"
    }

    function Invoke-RenderSurface {
        param(
            [Parameter(Mandatory=$true)][string]$Surface,
            [Parameter(Mandatory=$true)][string]$Target,
            [int]$TimeoutSeconds = 10
        )

        $stdout = Join-Path $WorkDir "$Surface.out.txt"
        $stderr = Join-Path $WorkDir "$Surface.err.txt"
        Remove-Item -LiteralPath $Target,$stdout,$stderr -ErrorAction SilentlyContinue

        $process = Start-Process -FilePath $exe `
            -ArgumentList @("--render-surface", $Surface, "`"$Target`"") `
            -WorkingDirectory $root `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -PassThru

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $rendered = $false
        while ((Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath $Target) {
                $item = Get-Item -LiteralPath $Target
                if ($item.Length -gt 0) {
                    $rendered = $true
                    break
                }
            }
            if ($process.HasExited) { break }
            Start-Sleep -Milliseconds 150
        }

        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }

        if (-not $rendered) {
            $outText = if (Test-Path -LiteralPath $stdout) { Get-Content -Raw -LiteralPath $stdout } else { "" }
            $errText = if (Test-Path -LiteralPath $stderr) { Get-Content -Raw -LiteralPath $stderr } else { "" }
            throw "Timed out rendering $Surface. stdout=$outText stderr=$errText"
        }
    }

    $surfaces = @(
        "tray-dark",
        "settings-api-light",
        "flyout-ok-dark",
        "menu-dark"
    )

    foreach ($surface in $surfaces) {
        Invoke-RenderSurface -Surface $surface -Target (Join-Path $WorkDir "$surface.png")
    }
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
    @{ Source = "settings-api-light.png"; Target = "settings-main.png"; Radius = 12 },
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
