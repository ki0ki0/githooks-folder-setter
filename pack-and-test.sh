#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Use a path under /private/tmp (the real path on macOS) so NuGet sees the
# same absolute path that we write to regardless of /tmp symlink resolution.
PKG_DIR="$(python3 -c 'import os, tempfile; print(os.path.realpath(tempfile.gettempdir()))' 2>/dev/null || realpath /tmp)/ghfs-pkg"
export GHFS_PKG_DIR="$PKG_DIR"

echo "==> Cleaning old package feed at $PKG_DIR"
rm -rf "$PKG_DIR"
rm -rf ~/.nuget/packages/githooksfoldersetter

echo "==> Packing GitHooksFolderSetter"
dotnet pack "$SCRIPT_DIR/GitHooksFolderSetter/GitHooksFolderSetter.csproj" \
  -o "$PKG_DIR" \
  --nologo \
  -v quiet

echo "==> Running tests  (GHFS_PKG_DIR=$GHFS_PKG_DIR)"
dotnet test "$SCRIPT_DIR/tests/GitHooksFolderSetter.Tests/GitHooksFolderSetter.Tests.csproj" \
  --nologo
