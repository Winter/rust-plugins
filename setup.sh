#!/bin/bash

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)

OxideUri="https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip"
CarbonUri="https://github.com/CarbonCommunity/Carbon.Core/releases/download/production_build/Carbon.Windows.Release.zip"
DepotDownloaderUri="https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_2.4.7/depotdownloader-2.4.7.zip"
AppId=258550
FileList="regex:RustDedicated_Data/Managed/.*"
OxideOutputPath="$SCRIPT_DIR/.ass/Oxide"
CarbonOutputPath="$SCRIPT_DIR/.ass/Carbon.Windows.Release"
DepotDownloaderOutputPath="$SCRIPT_DIR/.ass/.depotdownloader"
DepotDownloaderPath="$SCRIPT_DIR/.ass/.depotdownloader/DepotDownloader.dll"
ServerAssembliesOutputPath="$SCRIPT_DIR/.ass/.depotdownloader"
ServerAssembliesFromPath="$SCRIPT_DIR/.ass/.depotdownloader/RustDedicated_Data"
ServerAssembliesToPath="$SCRIPT_DIR/.ass/RustDedicated_Data"

# Check if curl or wget is installed
if command -v curl &>/dev/null; then
    http_cli="curl"
elif command -v wget &>/dev/null; then
    http_cli="wget"
else
    echo "Unable to find curl or wget. Please install one of them to continue." >&2
    exit 1
fi

# Check if dotnet is installed
if ! command -v dotnet &>/dev/null; then
    echo "Unable to find dotnet. Please install it to continue." >&2
    exit 1
fi

# Check if we're on macOS. We need to use zsh as macOS still ships Bash 3.
if [[ $(uname) == "Darwin" ]] && [[ $(ps -o comm=$$) == *"bash"* ]]; then
    if command -v zsh >/dev/null; then
        exec zsh "$0" "$@"
    else
        echo "OS was detected as macOS but we couldn't find zsh"
        exit 1
    fi
fi

help_all="Run all Tasks"
help_oxide="Download Oxide"
help_carbon="Download Carbon"
help_dd="Download DepotDownloader"
help_server="Download Server assemblies"

help="$(basename "$0") [-h|--help] [-a|--all] [-o|--oxide] [-c|--carbon] [-d|--dd] [-s|--server]

Options:
    -h|--help     Show this help text
    -a|--all      $help_all
    -o|--oxide    $help_oxide
    -c|--carbon   $help_carbon
    -d|--dd       $help_dd
    -s|--server   $help_server"

# Set the default option to show help information
task="help"

# Parse arguments
while [[ $# -gt 0 ]]; do
    key="$1"
    case $key in
    -h | --help)
        echo "$help"
        exit 0
        ;;
    -a | --all)
        task="all"
        shift
        ;;
    -o | --oxide)
        task="oxide"
        shift
        ;;
    -c | --carbon)
        task="carbon"
        shift
        ;;
    -d | --depotdownloader)
        task="depotdownloader"
        shift
        ;;
    -s | --server)
        task="server"
        shift
        ;;
    *)
        echo "Unknown Option: $key"
        echo "$usage"
        exit 1
        ;;
    esac
done

# Run tasks
case $task in
"help")
    echo "$help"
    exit 0
    ;;
"all")
    echo "Running all Tasks"
    ;&
"oxide" | "all")
    if [[ -d $OxideOutputPath ]]; then
        echo "Removing existing Oxide resources"
        rm -r $OxideOutputPath
    else
        mkdir -p $OxideOutputPath
    fi

    TempPath=$(mktemp)

    echo "Downloading Oxide to $TempPath"
    $http_cli -o $TempPath -L $OxideUri

    echo "Extracting Oxide to $OxideOutputPath"
    unzip $TempPath -d $OxideOutputPath
    ;&
"carbon" | "all")
    if [[ -d $CarbonOutputPath ]]; then
        echo "Removing existing Carbon resources"
        rm -r $CarbonOutputPath
    else
        mkdir -p $CarbonOutputPath
    fi

    TempPath=$(mktemp)

    echo "Downloading Carbon to $TempPath"
    $http_cli -o $TempPath -L $CarbonUri

    echo "Extracting Carbon to $CarbonOutputPath"
    unzip $TempPath -d $CarbonOutputPath
    ;&
"depotdownloader" | "all")
    if [[ -d $DepotDownloaderOutputPath ]]; then
        echo "Removing existing Carbon resources"
        rm -r $DepotDownloaderOutputPath
    else
        mkdir -p $DepotDownloaderOutputPath
    fi

    TempPath=$(mktemp)

    echo "Downloading DepotDownloader to $TempPath"
    $http_cli -o $TempPath -L $DepotDownloaderUri

    echo "Extracting DepotDownloader to $DepotDownloaderOutputPath"
    unzip $TempPath -d $DepotDownloaderOutputPath
    ;&
"server" | "all")
    if [[ -d $ServerAssembliesToPath ]]; then
        echo "Removing existing Server Assembly resources"
        rm -r $ServerAssembliesToPath
    else
        mkdir -p $ServerAssembliesToPath
    fi

    TempPath=$(mktemp)

    echo "Writing filelist to $TempPath"
    echo $FileList >$TempPath

    dotnet $DepotDownloaderPath -app $AppId -dir $ServerAssembliesOutputPath -filelist $TempPath -os linux

    if [[ ! -d $ServerAssembliesFromPath ]]; then
        echo "Unable to find Server Assemblies at $ServerAssembliesFromPath"
        exit 1
    fi

    echo "Moving Server Assemblies from $ServerAssembliesFromPath to $ServerAssembliesToPath"
    mv $ServerAssembliesFromPath $ServerAssembliesToPath
    ;;
esac

exit 0
