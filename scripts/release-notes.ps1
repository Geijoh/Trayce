param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$OutputPath = "out/release-notes.md"
)

$ErrorActionPreference = "Stop"

$tag = "v$Version"
$repo = $env:GITHUB_REPOSITORY
$tags = @(git tag --list "v[0-9]*" --sort=-v:refname)
$previous = $tags | Where-Object { $_ -ne $tag } | Select-Object -First 1
$range = if ($previous) { "$previous..HEAD" } else { "HEAD" }
$commits = @(git log $range --no-merges --pretty=format:"- %s (`%h`)")

if ($commits.Count -eq 0) {
    $commits = @("- Build/package refresh.")
}

$lines = @(
    "## Changes",
    "",
    $commits,
    "",
    "## Package",
    "",
    "- ``Trayce-win-x64.zip``",
    "- ``Trayce-win-x64.zip.sha256``"
)

if ($repo -and $previous) {
    $lines += @(
        "",
        "## Full changelog",
        "",
        "https://github.com/$repo/compare/$previous...$tag"
    )
}

$dir = Split-Path $OutputPath
if ($dir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}
Set-Content -Path $OutputPath -Value $lines -Encoding UTF8
Write-Host "Wrote $OutputPath"
