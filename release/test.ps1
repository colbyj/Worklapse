# Packs a Worklapse build to try out, in src/Worklapse.App/bin/velopack-test (see release.ps1 -Test).
& (Join-Path $PSScriptRoot 'release.ps1') -Test @args
exit $LASTEXITCODE
