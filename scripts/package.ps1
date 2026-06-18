$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = Join-Path $root "bin\Release\net8.0-windows\win-x64\publish"
$distDir = Join-Path $root "dist"
$unpackedDir = Join-Path $distDir "Trayce-win-x64"
$zipPath = Join-Path $distDir "Trayce-win-x64.zip"
$unpackedExe = [IO.Path]::GetFullPath((Join-Path $unpackedDir "Trayce.exe"))

function Stop-UnpackedTrayce {
    Get-Process -Name "Trayce" -ErrorAction SilentlyContinue | ForEach-Object {
        $processPath = $null
        try { $processPath = [IO.Path]::GetFullPath($_.Path) } catch { }
        if ($processPath -and $processPath.Equals($unpackedExe, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Stopping $processPath"
            Stop-Process -Id $_.Id -Force
            try { Wait-Process -Id $_.Id -Timeout 10 -ErrorAction Stop } catch { }
        }
    }
}

Push-Location $root
try {
    dotnet publish -c Release
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    if (Test-Path $unpackedDir) {
        Stop-UnpackedTrayce
        Remove-Item -LiteralPath $unpackedDir -Recurse -Force
    }
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath
    }
    New-Item -ItemType Directory -Force -Path $unpackedDir | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $unpackedDir -Recurse -Force
    Compress-Archive -Path (Join-Path $unpackedDir "*") -DestinationPath $zipPath
    Write-Host $unpackedDir
    Write-Host $zipPath
}
finally {
    Pop-Location
}
