#!/usr/bin/env bash
# Builds a Worklapse release for Linux and publishes it on GitHub, where installed copies find it.
#
# Publishes the app with the Linux publish profile, packs it with Velopack (WorklapseApp.AppImage, the full package,
# and a delta from the latest Linux release on GitHub), and uploads it to release v<Version>. The version is the
# <Version> in src/Worklapse.App/Worklapse.App.csproj. Either OS's script may go first: the first makes the release
# (the version must be above the latest), the second adds its files to it (the same version, from the same commit).
# There's no signing on Linux.
#
# Needs: vpk 1.2.0 (dotnet tool install -g vpk --version 1.2.0, the same version as the Velopack package), curl,
# python3, the native library's build tools (see native/README.md), and a GitHub token that can write the repository's
# contents in WORKLAPSE_GITHUB_TOKEN.
#
# Usage: release/release-linux.sh [--no-upload]
#   --no-upload  Stops after packing, leaving the release in src/Worklapse.App/bin/velopack-linux to try out.

set -euo pipefail

no_upload=false
for arg in "$@"; do
    case "$arg" in
        --no-upload) no_upload=true ;;
        *) echo "Unknown option: $arg (the only one is --no-upload)" >&2; exit 2 ;;
    esac
done

repository='https://github.com/colbyj/Worklapse'
api='https://api.github.com/repos/colbyj/Worklapse'
# Must match release.ps1's: the packId names the app to Velopack (on Windows, also its install folder).
pack_id='WorklapseApp'
vpk_version='1.2.0'

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app_folder="$root/src/Worklapse.App"
project="$app_folder/Worklapse.App.csproj"
publish_folder="$app_folder/bin/publish/worklapse-linux-x64"
release_folder="$app_folder/bin/velopack-linux"
export PATH="$PATH:$HOME/.dotnet/tools"

fail() { echo "$*" >&2; exit 1; }

# GET from GitHub's API; prints the body, or nothing on a 404. Any other failure stops the script.
github_get() {
    local auth=()
    [[ -n "${WORKLAPSE_GITHUB_TOKEN:-}" ]] && auth=(-H "Authorization: Bearer $WORKLAPSE_GITHUB_TOKEN")
    local body status
    body="$(curl -sS -w '\n%{http_code}' -H 'User-Agent: Worklapse-release' "${auth[@]}" "$api/$1")"
    status="${body##*$'\n'}"
    body="${body%$'\n'*}"
    case "$status" in
        200) printf '%s' "$body" ;;
        404|422) ;;
        *) fail "GitHub answered $status for $1." ;;
    esac
}

json() { python3 -I -c "import json, sys; d = json.load(sys.stdin); print($1)"; }

# --- Checks, before minutes of building ------------------------------------------------------------------------------

version="$(grep -oP '<Version>\K[^<]+' "$project" | head -n 1)"
[[ -n "$version" ]] || fail "No <Version> in $project."

installed_vpk="$(dotnet tool list -g | awk '$1 == "vpk" { print $2 }')"
[[ -n "$installed_vpk" ]] || fail "vpk isn't installed: dotnet tool install -g vpk --version $vpk_version"
[[ "$installed_vpk" == "$vpk_version" ]] ||
    fail "vpk is $installed_vpk; the Velopack package is $vpk_version. dotnet tool update -g vpk --version $vpk_version"

commit=''
if ! $no_upload; then
    [[ -n "${WORKLAPSE_GITHUB_TOKEN:-}" ]] || fail "Set WORKLAPSE_GITHUB_TOKEN to a GitHub token that can write the repository's contents."
    [[ -z "$(git -C "$root" status --porcelain)" ]] ||
        fail 'There are uncommitted changes; the release would be tagged on a commit that lacks them.'
    # The release is tagged on this commit, which GitHub must have (otherwise the upload fails with "Validation Failed").
    commit="$(git -C "$root" rev-parse HEAD)"
    pushed="$(github_get "commits/$commit")"
    [[ -n "$pushed" ]] || fail "GitHub doesn't have commit ${commit:0:7} yet: push it first."
fi

# Clients take only a release newer than theirs, so one that isn't would upload and reach nobody. The same version as
# the latest is the other OS's release, which this one joins.
latest_release="$(github_get releases/latest)"
latest=''
merge=false
if [[ -n "$latest_release" ]]; then
    latest="$(json "d['tag_name'].lstrip('v')" <<< "$latest_release")"
    if [[ "$version" == "$latest" ]]; then
        if [[ "$(json "'releases.linux.json' in [a['name'] for a in d['assets']]" <<< "$latest_release")" == True ]]; then
            fail "Release $latest already has the Linux build. Raise <Version> in $project."
        fi
        if ! $no_upload; then
            tag="$(github_get "commits/v$version")"
            tag_commit="$(json "d['sha']" <<< "$tag")"
            [[ "$tag_commit" == "$commit" ]] ||
                fail "Release $latest is of commit ${tag_commit:0:7}, not ${commit:0:7}: release from that commit, or raise <Version> in $project."
        fi
        merge=true
    elif [[ "$(printf '%s\n%s\n' "$latest" "$version" | sort -V | tail -n 1)" != "$version" ]]; then
        fail "Version $version isn't above the latest release ($latest). Raise <Version> in $project."
    fi
fi

echo "Releasing Worklapse $version for Linux${latest:+ (latest on GitHub: $latest)}$($merge && echo ', joining its release')"

# --- Build and pack ------------------------------------------------------------------------------------------------

rm -rf "$release_folder"
mkdir -p "$release_folder"
if [[ -n "$latest" ]]; then
    # The latest Linux release's full package, for the delta from it to this one (none before the first).
    vpk download github --repoUrl "$repository" --channel linux --outputDir "$release_folder"
fi

rm -rf "$publish_folder"
dotnet publish "$project" -p:PublishProfile=Linux

# vpk leaves out .pdb files by default; Worklapse's own give crash reports their line numbers. (?!) matches nothing.
vpk pack \
    --packId "$pack_id" \
    --packVersion "$version" \
    --packDir "$publish_folder" \
    --mainExe Worklapse \
    --packTitle Worklapse \
    --packAuthors 'Colby Johanson' \
    --icon "$app_folder/Assets/worklapse.png" \
    --exclude '(?!)' \
    --outputDir "$release_folder"

if $no_upload; then
    echo "Packed in $release_folder (not uploaded)."
    exit 0
fi

# --- Upload --------------------------------------------------------------------------------------------------------

upload=(upload github --repoUrl "$repository" --token "$WORKLAPSE_GITHUB_TOKEN" --outputDir "$release_folder"
    --channel linux --tag "v$version" --releaseName "Worklapse $version" --targetCommitish "$commit" --publish)
$merge && upload+=(--merge)
vpk "${upload[@]}"
echo "Released: $repository/releases/tag/v$version"
