#!/bin/bash
# Applies patches/LibGit2Sharp/*.patch to the external/LibGit2Sharp submodule, so MvsBuddy.Core (netstandard2.0) can
# reference it. Run it after cloning or updating the submodules and before building, like build-native.sh.
# In place, not on a copy: LibGit2Sharp's project needs its repository around it (Directory.Build.props, the signing
# key, Targets/, MinVer reading the git history). .gitmodules sets "ignore = dirty" for the submodule so the applied
# patches do not show it as modified; that also hides any other edit made in it.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
sub=$root/external/LibGit2Sharp
for patch in "$root"/patches/LibGit2Sharp/*.patch; do
    name=$(basename "$patch")
    if git -C "$sub" apply --reverse --check "$patch" 2>/dev/null; then
        echo "$name: already applied"
    elif git -C "$sub" apply --check "$patch" 2>/dev/null; then
        git -C "$sub" apply "$patch"
        echo "$name: applied"
    else
        echo "$name does not apply to external/LibGit2Sharp at $(git -C "$sub" rev-parse --short HEAD): the patch needs updating for this version." >&2
        exit 1
    fi
done
