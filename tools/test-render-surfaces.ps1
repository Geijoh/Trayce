param(
    [string]$ExePath = "",
    [string]$OutDir = "$PSScriptRoot\..\out\render-smoke",
    [int]$TimeoutSeconds = 12,
    [switch]$SkipBuild,
    [string[]]$Surfaces = @(
        "flyout-ok-light",
        "flyout-error-dark",
        "settings-general-light",
        "settings-api-light",
        "settings-api-bottom-light",
        "menu-dark",
        "tray-light",
        "dialog-presets",
        "dialog-logo",
        "dialog-about"
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Push-Location $root
try {
    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        if (-not $SkipBuild) {
            dotnet build --nologo | Write-Host
        }
        $ExePath = Join-Path $root "bin\Debug\net8.0-windows\win-x64\Trayce.exe"
    }

    $ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "Missing Trayce executable: $ExePath"
    }

    Add-Type -AssemblyName System.Drawing

    function Invoke-Trayce {
        param(
            [Parameter(Mandatory = $true)][string]$Label,
            [Parameter(Mandatory = $true)][string[]]$Arguments
        )

        $safeLabel = $Label -replace '[^A-Za-z0-9_.-]', '_'
        $stdout = Join-Path $OutDir "$safeLabel.out.txt"
        $stderr = Join-Path $OutDir "$safeLabel.err.txt"
        Remove-Item -LiteralPath $stdout,$stderr -ErrorAction SilentlyContinue

        $info = [System.Diagnostics.ProcessStartInfo]::new()
        $info.FileName = $ExePath
        $info.WorkingDirectory = $root
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.Arguments = (($Arguments | ForEach-Object { ConvertTo-QuotedArgument $_ }) -join " ")

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $info
        [void]$process.Start()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $outText = $process.StandardOutput.ReadToEnd()
            $errText = $process.StandardError.ReadToEnd()
            Set-Content -Path $stdout -Value $outText -Encoding UTF8
            Set-Content -Path $stderr -Value $errText -Encoding UTF8
            throw "Timed out running $Label after $TimeoutSeconds seconds. stdout=$outText stderr=$errText"
        }

        $outText = $process.StandardOutput.ReadToEnd()
        $errText = $process.StandardError.ReadToEnd()
        Set-Content -Path $stdout -Value $outText -Encoding UTF8
        Set-Content -Path $stderr -Value $errText -Encoding UTF8

        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode). stdout=$outText stderr=$errText"
        }
    }

    function ConvertTo-QuotedArgument {
        param([Parameter(Mandatory = $true)][string]$Argument)

        if ($Argument -notmatch '[\s"]') {
            return $Argument
        }

        return '"' + ($Argument -replace '"', '\"') + '"'
    }

    function Assert-Png {
        param([Parameter(Mandatory = $true)][string]$Path)

        if (-not (Test-Path -LiteralPath $Path)) {
            throw "Missing rendered PNG: $Path"
        }

        $item = Get-Item -LiteralPath $Path
        if ($item.Length -le 0) {
            throw "Rendered PNG is empty: $Path"
        }

        $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
        try {
            if ($bitmap.Width -lt 8 -or $bitmap.Height -lt 8) {
                throw "Rendered PNG is too small: $Path ($($bitmap.Width)x$($bitmap.Height))"
            }

            $seen = New-Object 'System.Collections.Generic.HashSet[int]'
            $opaque = 0
            for ($x = 0; $x -lt $bitmap.Width; $x += [Math]::Max(1, [int]($bitmap.Width / 8))) {
                for ($y = 0; $y -lt $bitmap.Height; $y += [Math]::Max(1, [int]($bitmap.Height / 8))) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    if ($pixel.A -gt 0) { $opaque++ }
                    [void]$seen.Add($pixel.ToArgb())
                }
            }

            if ($opaque -eq 0 -or $seen.Count -lt 2) {
                throw "Rendered PNG appears blank: $Path"
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    Invoke-Trayce -Label "self-test" -Arguments @("--self-test")

    foreach ($surface in $Surfaces) {
        $target = Join-Path $OutDir "$surface.png"
        Remove-Item -LiteralPath $target -ErrorAction SilentlyContinue
        Invoke-Trayce -Label $surface -Arguments @("--render-surface", $surface, $target)
        Assert-Png -Path $target
        Write-Host "Rendered $surface"
    }
}
finally {
    Pop-Location
}
