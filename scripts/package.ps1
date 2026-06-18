$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $root "bin\Release\net8.0-windows\win-x64\publish"
$distDir = Join-Path $root "dist"
$zipPath = Join-Path $distDir "Trayce-win-x64.zip"

Push-Location $root
try {
    dotnet publish -c Release
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath
    }
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath
    Write-Host $zipPath
}
finally {
    Pop-Location
}
