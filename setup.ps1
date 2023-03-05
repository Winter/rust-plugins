$OxideUri = "https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip"
$CarbonUri = "https://github.com/CarbonCommunity/Carbon.Core/releases/download/production_build/Carbon.Windows.Release.zip"
$DepotDownloaderUri = "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_2.4.7/depotdownloader-2.4.7.zip"
$AppId = 258550
$FileList = "regex:RustDedicated_Data/Managed/.*"
$OxideOutputPath = "${PSScriptRoot}\.ass\Oxide"
$CarbonOutputPath = "${PSScriptRoot}\.ass\Carbon.Windows.Release"
$DepotDownloaderOutputPath = "${PSScriptRoot}\.ass\.depotdownloader"
$DepotDownloaderPath = "${PSScriptRoot}\.ass\.depotdownloader\DepotDownloader.exe"
$ServerAssembliesOutputPath = "${PSScriptRoot}\.ass\.depotdownloader"
$ServerAssembliesFromPath = "${PSScriptRoot}\.ass\.depotdownloader\RustDedicated_Data"
$ServerAssembliesToPath = "${PSScriptRoot}\.ass\RustDedicated_Data"

# Check if the PowerShell version is at least 6.0
if ($PSVersionTable.PSEdition -eq 'Desktop' -and $PSVersionTable.Major -lt 6) {
    Write-Host "This script requires PowerShell 6.0 or later." -ForegroundColor Red
    exit 1
}

$help_all = "Run all Tasks"
$help_oxide = "Download Oxide"
$help_carbon = "Download Carbon"
$help_dd = "Download DepotDownloader"
$help_server = "Download Server assemblies"

$help = @"
Usage: $($MyInvocation.MyCommand.Name) [-h|--help] [-a|--all] [-o|--oxide] [-c|--carbon] [-d|--dd] [-s|--server]

Options:
    -h|--help  Show this help text
    -a|--all   $help_all
    -o|--oxide $help_oxide
    -c|--carbon $help_carbon
    -d|--dd $help_dd
    -s|--server $help_server
"@

# Set default option to show help information
$task = "help"

# Parse arguments
$key = $args[0]
switch ($key) {
    { ($_ -eq "-h") -or ($_ -eq "--help") } {
        Write-Host $usage
        exit 0
    }
    { ($_ -eq "-a") -or ($_ -eq "--all") } {
        $task = "all"
        break
    }
    { ($_ -eq "-o") -or ($_ -eq "--oxide") } {
        $task = "oxide"
        break
    }
    { ($_ -eq "-c") -or ($_ -eq "carbon") } {
        $task = "carbon"
        return
    }
    { ($_ -eq "-d") -or ($_ -eq "--dd") } {
        $task = "depotdownloader"
        return
    }
    { ($_ -eq "-s") -or ($_ -eq "--server") } {
        $task = "server"
        return;
    }
    default {
        Write-Host "Unknown Option: $key" -ForegroundColor Red
        Write-Host $help
        exit 1
    }
}


# Run tasks
switch ($task) {
    "help" {
        Write-Host $help
        exit 0
    }
    "all" {
        Write-Host "Running all Tasks"
    }
    { ($_ -eq "oxide") -or ($_ -eq "all") } {
        if (Test-Path -Path $OxideOutputPath) {
            Write-Host "Removing existing Oxide resources"
            Remove-Item -Path $OxideOutputPath -Recurse
        }

        $TempPath = New-TemporaryFile

        Write-Host "Downloading Oxide to ${TempPath}"
        Invoke-RestMethod -Uri $OxideUri -OutFile $TempPath -Verbose

        Write-Host "Extracting Oxide to ${OxideOutputPath}"
        Expand-Archive -Path $TempPath -DestinationPath $OxideOutputPath

        Write-Host "Cleaning up"
        Remove-Item -Path $TempPath -Recurse
    }
    { ($_ -eq "carbon") -or ($_ -eq "all") } {
        if (Test-Path -Path $CarbonOutputPath) {
            Write-Host "Removing existing Carbon resources"
            Remove-Item -Path $CarbonOutputPath -Recurse
        }

        $TempPath = New-TemporaryFile

        Write-Host "Downloading Carbon to ${TempPath}"
        Invoke-RestMethod -Uri $CarbonUri -OutFile $TempPath -Verbose

        Write-Host "Extracting Oxide to ${CarbonOutputPath}"
        Expand-Archive -Path $TempPath -DestinationPath $CarbonOutputPath

        Write-Host "Cleaning up"
        Remove-Item -Path $TempPath -Recurse
    }
    { ($_ -eq "depotdownloader") -or ($_ -eq "all") } {
        if (Test-Path -Path $DepotDownloaderOutputPath) {
            Write-Host "Removing existing DepotDownloader resources"
            Remove-Item -Path $DepotDownloaderOutputPath -Recurse
        }

        $TempPath = New-TemporaryFile

        Write-Host "Downloading DepotDownloader to ${TempPath}"
        Invoke-RestMethod -Uri $DepotDownloaderUri -OutFile $TempPath

        Write-Host "Extracting DepotDownloader to ${DepotDownloaderOutputPath}"
        Expand-Archive -Path $TempPath -DestinationPath $DepotDownloaderOutputPath

        Write-Host "Cleaning up"
        Remove-Item -Path $TempPath -Recurse
    }
    { ($_ -eq "server") -or ($_ -eq "all") } {
        if (Test-Path -Path $ServerAssembliesToPath) {
            Write-Host "Removing existing Server assembly resources"
            Remove-Item -Path $ServerAssembliesToPath -Recurse
        }

        $TempPath = New-TemporaryFile

        Write-Host "Writing filelist to ${TempPath}"
        Set-Content -Path $TempPath -Value $FileList -NoNewline

        Write-Host "Running DepotDownloader"
        & $DepotDownloaderPath -app $AppId -dir $ServerAssembliesOutputPath -filelist $TempPath
        
        if (!(Test-Path -Path $ServerAssembliesFromPath)) {
            Write-Host "Unable to find the Server assemblies at ${ServerAssembliesFromPath}"
            exit 1
        }

        Write-Host "Moving Server assemblies from ${ServerAssembliesFromPath} to ${ServerAssembliesToPath}"
        Move-Item -Path $ServerAssembliesFromPath -Destination $ServerAssembliesToPath
    }
}
