<#
.SYNOPSIS
Builds a Worklapse release and publishes it on GitHub, where installed copies find it.

.DESCRIPTION
Publishes the app with the FolderProfile publish profile, packs it with Velopack (Setup.exe, the full package, and a
delta from the latest release on GitHub), signs it with Azure Trusted Signing, and uploads it as release v<Version>.
The version is the <Version> in src/Worklapse.App/Worklapse.App.csproj; raise it before each release.

Needs: vpk 1.2.0 (dotnet tool install -g vpk --version 1.2.0, the same version as the Velopack package), `az login`
with the Trusted Signing Certificate Profile Signer role, the .NET 8 runtime (for vpk's signing library), and a GitHub
token that can write the repository's contents in WORKLAPSE_GITHUB_TOKEN.

.PARAMETER Unsigned
Packs without signing (Windows SmartScreen then warns whoever runs Setup.exe).

.PARAMETER NoUpload
Stops after packing, leaving the release in src/Worklapse.App/bin/velopack to try out.
#>
param(
    [switch]$Unsigned,
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'

$Repository = 'https://github.com/colbyj/Worklapse'
# Velopack installs to %LocalAppData%\<packId> and removes that folder on uninstall, so the id mustn't be "Worklapse":
# that's the data folder, with the recordings.
$PackId = 'WorklapseApp'
$VpkVersion = '1.2.0'

$root = Split-Path $PSScriptRoot -Parent
$appFolder = Join-Path $root 'src\Worklapse.App'
$project = Join-Path $appFolder 'Worklapse.App.csproj'
$publishFolder = Join-Path $appFolder 'bin\publish\worklapse-win-x64'
$releaseFolder = Join-Path $appFolder 'bin\velopack'
$signingFile = Join-Path $PSScriptRoot 'azure-signing.json'

function Invoke-Checked([string]$command, [string[]]$arguments) {
    & $command @arguments
    if ($LASTEXITCODE -ne 0) { throw "$command $($arguments[0]) failed (exit code $LASTEXITCODE)." }
}

# --- Checks, before minutes of building ------------------------------------------------------------------------------

$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project." }

$installedVpk = dotnet tool list -g | ForEach-Object { if ($_ -match '^vpk\s+(\S+)') { $Matches[1] } }
if (-not $installedVpk) { throw "vpk isn't installed: dotnet tool install -g vpk --version $VpkVersion" }
if ($installedVpk -ne $VpkVersion) {
    throw "vpk is $installedVpk; the Velopack package is $VpkVersion. dotnet tool update -g vpk --version $VpkVersion"
}

$token = $env:WORKLAPSE_GITHUB_TOKEN
if (-not $NoUpload) {
    if (-not $token) { throw 'Set WORKLAPSE_GITHUB_TOKEN to a GitHub token that can write the repository''s contents.' }
    if (git -C $root status --porcelain) { throw 'There are uncommitted changes; the release would be tagged on a commit that lacks them.' }
}

# Clients take only a release newer than theirs, so one that isn't would upload and reach nobody.
$headers = @{ 'User-Agent' = 'Worklapse-release' }
if ($token) { $headers['Authorization'] = "Bearer $token" }
$latest = $null
try {
    $latest = (Invoke-RestMethod "https://api.github.com/repos/colbyj/Worklapse/releases/latest" -Headers $headers).tag_name.TrimStart('v')
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }  # 404: no release yet
}
if ($latest -and [version]$version -le [version]$latest) {
    throw "Version $version isn't above the latest release ($latest). Raise <Version> in $project."
}

if (-not $Unsigned) {
    if (-not (dotnet --list-runtimes | Select-String -SimpleMatch 'Microsoft.NETCore.App 8.')) {
        throw 'Signing needs the .NET 8 runtime (vpk''s Trusted Signing library runs on it).'
    }
    # The same credential vpk's signer uses (azure-signing.json leaves it the Azure CLI or the AZURE_* variables).
    az account get-access-token --resource https://codesigning.azure.net --output none
    if ($LASTEXITCODE -ne 0) { throw 'No Azure credential for signing: az login (or pass -Unsigned).' }
}

Write-Host "Releasing Worklapse $version$(if ($latest) { " (latest on GitHub: $latest)" })$(if ($Unsigned) { ', unsigned' })"

# --- Build and pack ------------------------------------------------------------------------------------------------

if (Test-Path $releaseFolder) { Remove-Item $releaseFolder -Recurse -Force }
New-Item -ItemType Directory $releaseFolder | Out-Null
if ($latest) {
    # The latest release's full package, for the delta from it to this one.
    Invoke-Checked vpk @('download', 'github', '--repoUrl', $Repository, '--outputDir', $releaseFolder)
}

if (Test-Path $publishFolder) { Remove-Item $publishFolder -Recurse -Force }
Invoke-Checked dotnet @('publish', $project, '-p:PublishProfile=FolderProfile')

$pack = @(
    'pack',
    '--packId', $PackId,
    '--packVersion', $version,
    '--packDir', $publishFolder,
    '--mainExe', 'Worklapse.exe',
    '--packTitle', 'Worklapse',
    '--packAuthors', 'Colby Johanson',
    '--icon', (Join-Path $appFolder 'Assets\worklapse.ico'),
    # vpk leaves out .pdb files by default; Worklapse's own give crash reports their line numbers. (?!) matches nothing.
    '--exclude', '(?!)',
    '--outputDir', $releaseFolder
)
if (-not $Unsigned) { $pack += @('--azureTrustedSignFile', $signingFile) }
Invoke-Checked vpk $pack

if ($NoUpload) {
    Write-Host "Packed in $releaseFolder (not uploaded)."
    return
}

# --- Upload --------------------------------------------------------------------------------------------------------

$commit = (git -C $root rev-parse HEAD).Trim()
Invoke-Checked vpk @('upload', 'github', '--repoUrl', $Repository, '--token', $token, '--outputDir', $releaseFolder,
    '--tag', "v$version", '--releaseName', "Worklapse $version", '--targetCommitish', $commit, '--publish')
Write-Host "Released: $Repository/releases/tag/v$version"
