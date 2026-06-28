$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = Join-Path $root "bin\Release\net8.0-windows\win-x64\publish"
$distDir = Join-Path $root "dist"
$unpackedDir = Join-Path $distDir "Trayce-win-x64"
$zipPath = Join-Path $distDir "Trayce-win-x64.zip"
$hashPath = "$zipPath.sha256"
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

function Wait-UnpackedExeUnlocked {
    for ($attempt = 1; $attempt -le 40; $attempt++) {
        if (-not (Test-Path -LiteralPath $unpackedExe)) { return }
        try {
            $stream = [IO.File]::Open($unpackedExe, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $stream.Dispose()
            return
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
}

function Get-SignTool {
    if ($env:TRAYCE_SIGNTOOL -and (Test-Path -LiteralPath $env:TRAYCE_SIGNTOOL)) {
        return $env:TRAYCE_SIGNTOOL
    }

    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kits)) {
        return $null
    }

    Get-ChildItem -Path $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -ExpandProperty FullName -First 1
}

function Invoke-OptionalSign {
    param([Parameter(Mandatory=$true)][string]$Path)

    if (-not $env:TRAYCE_SIGN_CERT_BASE64) {
        Write-Host "Code signing skipped: TRAYCE_SIGN_CERT_BASE64 is not set."
        return
    }

    if (-not $env:TRAYCE_SIGN_CERT_PASSWORD) {
        throw "TRAYCE_SIGN_CERT_PASSWORD must be set when TRAYCE_SIGN_CERT_BASE64 is set."
    }

    $signTool = Get-SignTool
    if (-not $signTool) {
        throw "signtool.exe was not found. Set TRAYCE_SIGNTOOL or install the Windows SDK signing tools."
    }

    $pfxPath = Join-Path ([IO.Path]::GetTempPath()) ("trayce-sign-" + [Guid]::NewGuid().ToString("N") + ".pfx")
    try {
        [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:TRAYCE_SIGN_CERT_BASE64))
        & $signTool sign /f $pfxPath /p $env:TRAYCE_SIGN_CERT_PASSWORD /fd SHA256 /tr "http://timestamp.digicert.com" /td SHA256 $Path
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory=$true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha.ComputeHash($stream)
            return ([BitConverter]::ToString($bytes) -replace "-", "").ToLowerInvariant()
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

Push-Location $root
try {
    dotnet publish -c Release
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    if (Test-Path $unpackedDir) {
        Stop-UnpackedTrayce
        Wait-UnpackedExeUnlocked
        Remove-Item -LiteralPath $unpackedDir -Recurse -Force
    }
    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath
    }
    if (Test-Path $hashPath) {
        Remove-Item -LiteralPath $hashPath
    }
    New-Item -ItemType Directory -Force -Path $unpackedDir | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $unpackedDir -Recurse -Force
    Invoke-OptionalSign -Path $unpackedExe
    Compress-Archive -Path (Join-Path $unpackedDir "*") -DestinationPath $zipPath
    $hash = Get-Sha256Hex -Path $zipPath
    Set-Content -Path $hashPath -Value "$hash  Trayce-win-x64.zip" -Encoding ASCII
    Write-Host $unpackedDir
    Write-Host $zipPath
    Write-Host $hashPath
}
finally {
    Pop-Location
}
