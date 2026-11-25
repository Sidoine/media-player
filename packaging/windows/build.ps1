param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\..\artifacts\windows")
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$stageRoot = Join-Path $env:TEMP "bobine-windows-package"
$serverOutput = Join-Path $stageRoot "server-publish"
$launcherOutput = Join-Path $stageRoot "launcher-publish"
$packageRoot = Join-Path $stageRoot "package"
$packageServer = Join-Path $packageRoot "Server"
$archivePath = Join-Path $OutputDirectory "Bobine-Windows-x64.zip"

Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $serverOutput, $launcherOutput, $packageServer, $OutputDirectory -Force | Out-Null

$head = git -C $repoRoot rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { throw "Impossible de déterminer la version Git du paquet." }
$version = "0.1-$head"

dotnet publish (Join-Path $repoRoot "src\MediaPlayer.Web\MediaPlayer.Web.csproj") `
    --configuration Release --runtime win-x64 --self-contained true -p:UseAppHost=true --output $serverOutput
if ($LASTEXITCODE -ne 0) { throw "La publication du serveur Windows a échoué." }

# App_Data is local development state and must never ship in a user download.
Remove-Item -LiteralPath (Join-Path $serverOutput "App_Data") -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $PSScriptRoot "launcher\Bobine.Launcher.csproj") `
    --configuration Release --runtime win-x64 --self-contained true -p:UseAppHost=true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $launcherOutput
if ($LASTEXITCODE -ne 0) { throw "La publication du lanceur Windows a échoué." }

Copy-Item -Path (Join-Path $serverOutput "*") -Destination $packageServer -Recurse -Force
Copy-Item -LiteralPath (Join-Path $launcherOutput "Bobine.exe") -Destination (Join-Path $packageRoot "Bobine.exe")
Set-Content -LiteralPath (Join-Path $packageRoot "build-version.txt") -Value $version -NoNewline -Encoding utf8

Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $archivePath -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($archive.Entries | ForEach-Object FullName)
    foreach ($required in @("Bobine.exe", "build-version.txt", "Server/MediaPlayer.Web.exe", "Server/MediaPlayer.Web.dll")) {
        if ($entries -notcontains $required) { throw "Fichier absent du paquet : $required" }
    }
    if ($entries | Where-Object { $_ -match "(^|/)App_Data/" }) { throw "Des données App_Data locales ont été incluses dans le paquet." }
}
finally { $archive.Dispose() }

Write-Host "Paquet créé et vérifié : $archivePath"
