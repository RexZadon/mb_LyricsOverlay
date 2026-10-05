<#
.SYNOPSIS
  Clean build, test, and package a release into .\release\.

.DESCRIPTION
  Produces, for the version in Directory.Build.props:
    release\mb_LyricsOverlay_<ver>.zip           the plugin (what users install)
    release\mb_LyricsOverlayProvider_<ver>.zip   the optional LRCLIB lyrics-provider plugin
    release\mb_LyricsOverlay_<ver>_source.zip    the source tree, without build output or local files
    release\SHA256SUMS.txt
  Then unzips the plugin zips into a clean temp folder and checks that each DLL loads and resolves
  every dependency from that folder or the .NET Framework.

  Run from the repo root in Windows PowerShell 5.1 or PowerShell 7:
    powershell -ExecutionPolicy Bypass -File tools\package.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
Write-Host "== Packaging version $version"

# ---------------------------------------------------------------- clean, build, test
foreach ($d in @('dist', 'release') + (Get-ChildItem -Path src, tests -Directory -Recurse -Include bin, obj | ForEach-Object FullName)) {
    if (Test-Path $d) { Remove-Item -Recurse -Force $d }
}
& dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
& dotnet test -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

$release = New-Item -ItemType Directory -Force (Join-Path $root 'release')
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function New-Zip($zipPath, $entries) {
    # $entries: ordered hashtable of "name inside zip" -> source file
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        foreach ($k in $entries.Keys) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$k], ($k -replace '\\', '/'), 'Optimal') | Out-Null
        }
    } finally { $zip.Dispose() }
}

# ---------------------------------------------------------------- plugin zips
# DLLs sit at the zip root: MusicBee's "Add Plugin" button copies the zip's contents into its Plugins
# folder. Text files carry the plugin name so they can't clash with other plugins' files there.
# PDBs are not shipped.
$readme = Join-Path $root 'README.md'
$license = Join-Path $root 'LICENSE'
$notices = Join-Path $root 'THIRD_PARTY_NOTICES.md'

$pluginZip = Join-Path $release "mb_LyricsOverlay_$version.zip"
New-Zip $pluginZip ([ordered]@{
    'mb_LyricsOverlay.dll'                     = Join-Path $root 'dist\mb_LyricsOverlay.dll'
    'mb_LyricsOverlay_README.txt'              = $readme
    'mb_LyricsOverlay_LICENSE.txt'             = $license
    'mb_LyricsOverlay_THIRD_PARTY_NOTICES.txt' = $notices
})

$providerZip = Join-Path $release "mb_LyricsOverlayProvider_$version.zip"
New-Zip $providerZip ([ordered]@{
    'mb_LyricsOverlayProvider.dll'             = Join-Path $root 'dist\mb_LyricsOverlayProvider.dll'
    'mb_LyricsOverlay_README.txt'              = $readme
    'mb_LyricsOverlay_LICENSE.txt'             = $license
    'mb_LyricsOverlay_THIRD_PARTY_NOTICES.txt' = $notices
})

# ---------------------------------------------------------------- source zip
$excludeDirs = @('bin', 'obj', 'dist', 'release', '.vs', '.vscode', '.idea', '.claude', '.git', 'TestResults')
# RELEASE_NOTES_FOR_ME.md and LISTING.md are the maintainer's private notes and listing draft.
$excludeFiles = @('*.user', '*.suo', '*.log', '*.log.old', '*.tmp', 'Thumbs.db', 'Desktop.ini', 'settings.json',
                  'RELEASE_NOTES_FOR_ME.md', 'LISTING.md')
$sourceEntries = [ordered]@{}
Get-ChildItem -Path $root -Recurse -File -Force | Where-Object {
    $leaf = $_.Name
    $dirs = if ($_.DirectoryName.Length -gt $root.Length) { $_.DirectoryName.Substring($root.Length + 1) -split '[\\/]' } else { @() }
    -not ($dirs | Where-Object { $excludeDirs -contains $_ }) -and -not ($excludeFiles | Where-Object { $leaf -like $_ })
} | Sort-Object FullName | ForEach-Object {
    $sourceEntries["mb_LyricsOverlay-$version-source/" + $_.FullName.Substring($root.Length + 1)] = $_.FullName
}
$sourceZip = Join-Path $release "mb_LyricsOverlay_$($version)_source.zip"
New-Zip $sourceZip $sourceEntries

# ---------------------------------------------------------------- listing + checksums
$zips = @($pluginZip, $providerZip, $sourceZip)
foreach ($z in $zips) {
    Write-Host "`n== $(Split-Path $z -Leaf)"
    $zip = [System.IO.Compression.ZipFile]::OpenRead($z)
    try { $zip.Entries | ForEach-Object { '{0,10}  {1}' -f $_.Length, $_.FullName } | Write-Host }
    finally { $zip.Dispose() }
}
$sums = $zips | ForEach-Object { '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) }
$sums | Set-Content -Encoding ascii (Join-Path $release 'SHA256SUMS.txt')
Write-Host "`n== SHA256SUMS.txt"
$sums | Write-Host

# ---------------------------------------------------------------- install-flow check
# Unzip each plugin zip alone into an empty folder (as "Add Plugin" or a manual copy would) and load
# it in a separate .NET Framework process. Every referenced assembly must resolve from that folder
# or the framework, and MusicBee's entry type must be there.
$check = @'
param($dir)
$ErrorActionPreference = 'Stop'
$ok = $true
foreach ($dll in Get-ChildItem $dir -Filter *.dll) {
    if ($dll.Name -notlike 'mb_*') { Write-Host "FAIL $($dll.Name): MusicBee only loads mb_*.dll"; $ok = $false; continue }
    $asm = [Reflection.Assembly]::LoadFrom($dll.FullName)
    $name = $asm.GetName()
    Write-Host ("{0}: version {1}, {2}, runtime {3}" -f $dll.Name, $name.Version, $name.ProcessorArchitecture, $asm.ImageRuntimeVersion)
    foreach ($ref in $asm.GetReferencedAssemblies()) {
        try {
            $local = Join-Path $dir ($ref.Name + '.dll')
            $r = if (Test-Path $local) { [Reflection.Assembly]::LoadFrom($local) } else { [Reflection.Assembly]::Load($ref) }
            $where = if ($r.GlobalAssemblyCache) { 'framework (GAC)' } else { $r.Location }
            Write-Host ("  ok   {0} {1} -> {2}" -f $ref.Name, $ref.Version, $where)
        } catch { Write-Host ("  FAIL {0} {1}: {2}" -f $ref.Name, $ref.Version, $_.Exception.Message); $ok = $false }
    }
    $types = $asm.GetTypes()   # throws ReflectionTypeLoadException if any type can't be loaded
    $plugin = $asm.GetType('MusicBeePlugin.Plugin')
    if (-not $plugin -or -not $plugin.GetMethod('Initialise')) { Write-Host '  FAIL no MusicBeePlugin.Plugin.Initialise'; $ok = $false }
    else { Write-Host ("  ok   MusicBeePlugin.Plugin.Initialise found; {0} types load" -f $types.Length) }
}
if (-not $ok) { exit 1 }
'@
$checkScript = Join-Path ([IO.Path]::GetTempPath()) "mb_LyricsOverlay_check_$PID.ps1"
Set-Content -Path $checkScript -Value $check -Encoding utf8
try {
    foreach ($z in @($pluginZip, $providerZip)) {
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('mb_LyricsOverlay_install_' + [Guid]::NewGuid().ToString('N'))
        [System.IO.Compression.ZipFile]::ExtractToDirectory($z, $tmp)
        Write-Host "`n== Install check: $(Split-Path $z -Leaf) -> $tmp"
        Get-ChildItem $tmp | ForEach-Object { Write-Host "  $($_.Name)" }
        # Windows PowerShell 5.1 runs on .NET Framework 4.x, like MusicBee.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $checkScript $tmp
        $code = $LASTEXITCODE
        Remove-Item -Recurse -Force $tmp
        if ($code -ne 0) { throw "install check failed for $z" }
    }
} finally { Remove-Item -Force $checkScript -ErrorAction SilentlyContinue }

Write-Host "`nRelease ready in $release"
