#!/usr/bin/env bash
# Packs the SDK, the Player and the templates into a local NuGet feed (issue #32).
#
#   tools/pack_sdk.sh [feed-dir] [--build]
#
# Writes Sage.Sdk.<v>.nupkg, Sage.Player.<v>.nupkg and Sage.Templates.<v>.nupkg into feed-dir (default:
# artifacts/feed; <v> is build/Sage.Version.props). The Player is the host and the `sage` CLI as they are
# built in bin/Debug, bin/Development and bin/Shipping, so build the solution in all three first (CI does),
# or pass --build to build what it needs here (with -p:SageSkipShaders=true off Windows, since mgfxc needs
# Wine there: that Player boots and runs games but draws nothing).
#
# Then, outside the repository:
#   dotnet new install <feed-dir>/Sage.Templates.<v>.nupkg
#   dotnet new sage-game -o MyGame --feed <feed-dir>      # --feed writes a nuget.config naming the feed
#   cd MyGame && dotnet run
#
# Nothing is published anywhere: a public feed is REDESIGN §6 decision 7.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
feed=
build=false
for arg in "$@"; do
    case "$arg" in
        --build) build=true ;;
        *) feed=$arg ;;
    esac
done
feed=${feed:-$repo/artifacts/feed}
mkdir -p "$feed"
feed=$(cd "$feed" && pwd)

if $build; then
    skip=()
    case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ;; *) skip=(-p:SageSkipShaders=true) ;; esac
    for config in Debug Development Shipping; do
        dotnet build "$repo/src/Sage.Host/Sage.Host.csproj" -c "$config" "${skip[@]}"
        dotnet build "$repo/src/Sage.Cli/Sage.Cli.csproj" -c "$config"
    done
fi

# A new build of a package with the same version must not be shadowed by the copy NuGet already
# extracted: clear this version from the global packages folder.
packages=$(dotnet nuget locals global-packages --list | sed 's/^[^:]*: *//')
version=$(sed -n 's:.*<SageVersion>\(.*\)</SageVersion>.*:\1:p' "$repo/build/Sage.Version.props")
for id in sage.sdk sage.player; do
    rm -rf "${packages:?}/$id/$version"
done

for project in Sage.Sdk Sage.Player Sage.Templates; do
    dotnet pack "$repo/sdk/$project/$project.csproj" -c Debug -o "$feed" --nologo
done
echo "Packed Sage $version into $feed"
