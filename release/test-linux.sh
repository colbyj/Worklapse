#!/usr/bin/env bash
# Packs a Worklapse AppImage to try out, in src/Worklapse.App/bin/velopack-linux-test (see release-linux.sh --test).
exec "$(dirname "${BASH_SOURCE[0]}")/release-linux.sh" --test "$@"
