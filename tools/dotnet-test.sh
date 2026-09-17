#!/usr/bin/env bash
#
# Runs the test suite without a local .NET install.
#
# The dev box has Docker but no dotnet, so the SDK image is the toolchain. This
# wrapper exists because getting that invocation right by hand every time is
# tedious and easy to get subtly wrong - in particular, without --user the
# build writes root-owned bin/ and obj/ directories into the working tree and
# the next non-Docker command that touches them fails.
#
# Usage:
#   tools/dotnet-test.sh                      # run everything
#   tools/dotnet-test.sh --filter Printer     # pass anything through to dotnet test
#
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sdk_image="${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:8.0}"

# A package cache on the host, so a second run does not re-download the world.
# The standard location, so it is shared with anything else on this machine
# that uses the SDK.
nuget_cache="${NUGET_PACKAGES_HOST:-$HOME/.nuget/packages}"
mkdir -p "$nuget_cache"

exec docker run --rm \
    --user "$(id -u):$(id -g)" \
    -e HOME=/tmp \
    -e DOTNET_CLI_HOME=/tmp \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e NUGET_PACKAGES=/nuget \
    -v "$repo":/src \
    -v "$nuget_cache":/nuget \
    -w /src \
    "$sdk_image" \
    dotnet test Rock.CloudPrint.Tests "$@"
