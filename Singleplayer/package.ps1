param (
    [switch]$NoArchive,
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

Write-Host ""
Write-Host ""
Write-Host ""
Write-Host "=== Packe die Mod ===" -ForegroundColor Cyan
Write-Host ""

# -------------------------------------------------
# Checks
# -------------------------------------------------
if (!(Test-Path "info.json")) { throw "info.json fehlt!" }
if (!(Test-Path "build"))     { throw "build/-Ordner fehlt!" }
if (!(Test-Path "license.json")) { throw "license.json fehlt! (TrackMaintenance-Lizenz für DVCustomLicenses)" }
if (!(Test-Path "Contractor\license.json")) { throw "Contractor\license.json fehlt! (TrackMaintenanceContractor-Lizenz)" }
if (!(Test-Path "Contractor\icon.png")) { Write-Host "Hinweis: Contractor\icon.png nicht gefunden - zweite Lizenz ohne eigenes Icon." -ForegroundColor Yellow }

$HasIcon = Test-Path "icon.png"
if (-not $HasIcon) { Write-Host "Hinweis: icon.png nicht gefunden - wird nicht mitgepackt." -ForegroundColor Yellow }

# -------------------------------------------------
# Mod-Infos
# -------------------------------------------------
$modInfo = Get-Content -Raw "info.json" | ConvertFrom-Json
$modId   = $modInfo.Id

# -------------------------------------------------
# Zielverzeichnisse
# -------------------------------------------------
$DistDir   = Join-Path $OutputDirectory "dist"
$BackupDir = Join-Path $OutputDirectory "backup"

New-Item -Path $DistDir   -ItemType Directory -Force | Out-Null
New-Item -Path $BackupDir -ItemType Directory -Force | Out-Null

# -------------------------------------------------
# VERSION PRÜFEN / AUTOMATISCH ERHÖHEN
#
# Schema: Version.Build.Hotfix
# Beispiele:
# 0.00.2  -> 0.00.21
# 0.00.21 -> 0.00.22
# 1.23.45 -> 1.23.46
# -------------------------------------------------
$modVersion = [string]$modInfo.Version

# Beliebig viele Stellen pro Versionsblock, aber genau 3 numerische Blöcke
if ($modVersion -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "Ungültiges Versionsformat '$modVersion'. Erwartet wird Version.Build.Hotfix, z.B. 1.23.45."
}

$versionPart = $Matches[1]
$buildPart   = $Matches[2]
$hotfixPart  = $Matches[3]

# Zuerst exakt die Version aus info.json prüfen
$fileName = "${modId}_v${modVersion}.zip"
$zipPath  = Join-Path $DistDir $fileName

if ((-not $NoArchive) -and (Test-Path $zipPath)) {

    Write-Host "Version $modVersion existiert bereits in dist." -ForegroundColor Yellow

    # Sonderfall:
    # Einstelliger Hotfix wird beim ersten Hochzählen erweitert:
    # 2 -> 21, 3 -> 31 usw.
    if ($hotfixPart.Length -eq 1) {
        $nextHotfix = "${hotfixPart}1"
    }
    else {
        # Mehrstelligen Hotfix normal numerisch +1 erhöhen.
        # Führende Nullen werden soweit möglich beibehalten.
        $width = $hotfixPart.Length
        $number = [System.Numerics.BigInteger]::Parse($hotfixPart)
        $number = [System.Numerics.BigInteger]::Add($number, [System.Numerics.BigInteger]::One)
        $nextHotfix = $number.ToString().PadLeft($width, '0')
    }

    while ($true) {

        $candidateVersion = "${versionPart}.${buildPart}.${nextHotfix}"
        $candidateName    = "${modId}_v${candidateVersion}.zip"
        $candidatePath    = Join-Path $DistDir $candidateName

        if (!(Test-Path $candidatePath)) {
            $modVersion = $candidateVersion
            break
        }

        Write-Host "Version $candidateVersion existiert ebenfalls bereits in dist." -ForegroundColor Yellow

        # Ab hier immer normal numerisch hochzählen
        $width = $nextHotfix.Length
        $number = [System.Numerics.BigInteger]::Parse($nextHotfix)
        $number = [System.Numerics.BigInteger]::Add($number, [System.Numerics.BigInteger]::One)
        $nextHotfix = $number.ToString().PadLeft($width, '0')
    }
}

# -------------------------------------------------
# info.json aktualisieren, falls Version geändert
# -------------------------------------------------
$oldVersion = [string]$modInfo.Version

if ($modVersion -ne $oldVersion) {

    $modInfo.Version = $modVersion

    $modInfo |
        ConvertTo-Json -Depth 100 |
        Set-Content -Path "info.json" -Encoding UTF8

    Write-Host "Version erhöht: $oldVersion -> $modVersion" -ForegroundColor Yellow
}

Write-Host "Mod-ID:  $modId"
Write-Host "Version: $modVersion"

# -------------------------------------------------
# TEMP Ordner (ORIGINALVERHALTEN)
# -------------------------------------------------
$TmpDir    = Join-Path $DistDir "tmp"
$ZipOutDir = Join-Path $TmpDir $modId

if (Test-Path $ZipOutDir) {
    Remove-Item $ZipOutDir -Recurse -Force
}

New-Item -Path $ZipOutDir -ItemType Directory -Force | Out-Null

# -------------------------------------------------
# RELEASE Dateien sammeln (EXAKT WIE IM ORIGINAL)
# -------------------------------------------------
$FilesToInclude = @(
    "info.json",
    "license.json",
    "Contractor",
    "build\*",
    "LICENSE"
)
if ($HasIcon) { $FilesToInclude += "icon.png" }

foreach ($item in $FilesToInclude) {
    Copy-Item -Path $item -Destination $ZipOutDir -Recurse -Force
}

# -------------------------------------------------
# ZIP erstellen (dist)
# -------------------------------------------------
if (-not $NoArchive) {

    $fileName = "${modId}_v${modVersion}.zip"
    $zipPath  = Join-Path $DistDir $fileName

    Compress-Archive `
        -Path (Join-Path $ZipOutDir "*") `
        -DestinationPath $zipPath `
        -CompressionLevel Fastest `
        -Force

    Write-Host "ZIP erstellt: dist\$fileName"

    # =================================================
    # SOURCE BACKUP – BIN/OBJ ABSOLUT AUSGESCHLOSSEN
    # =================================================
    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $sourceBackupName = "${modId}_v${modVersion}_$timestamp.zip"
    $sourceBackupPath = Join-Path $BackupDir $sourceBackupName

    $sourceTmp = Join-Path $TmpDir "source"

    if (Test-Path $sourceTmp) {
        Remove-Item $sourceTmp -Recurse -Force
    }

    New-Item -Path $sourceTmp -ItemType Directory -Force | Out-Null

    $sourceFiles = Get-ChildItem -Path $PSScriptRoot -Recurse -File |
        Where-Object {
            $rel = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')

            # HARTE Ausschlüsse (egal wo im Pfad)
            if ($rel -match '(^|[\\/])bin([\\/]|$)')    { return $false }
            if ($rel -match '(^|[\\/])obj([\\/]|$)')    { return $false }
            if ($rel -match '(^|[\\/])build([\\/]|$)')  { return $false }
            if ($rel -match '(^|[\\/])dist([\\/]|$)')   { return $false }
            if ($rel -match '(^|[\\/])backup([\\/]|$)') { return $false }

            # NUR Quellformate
            return $_.Extension -in ".cs", ".csproj", ".txt", ".json", ".sln", ".ps1"
        }

    foreach ($f in $sourceFiles) {
        Copy-Item $f.FullName (Join-Path $sourceTmp $f.Name) -Force
    }

    Compress-Archive `
        -Path (Join-Path $sourceTmp "*") `
        -DestinationPath $sourceBackupPath `
        -CompressionLevel Optimal `
        -Force

    Remove-Item $sourceTmp -Recurse -Force

    Write-Host "Source-Backup erstellt: backup\$sourceBackupName"
}
else {
    Write-Host "Archivieren übersprungen (-NoArchive)"
}

Write-Host ""
Write-Host "=== FERTIG ===" -ForegroundColor Green
Write-Host ""
Write-Host ""
Write-Host ""