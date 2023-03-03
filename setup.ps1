$OxideModUri = "https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip"
$CarbonModUri = "https://github.com/CarbonCommunity/Carbon.Core/releases/download/production_build/Carbon.Windows.Release.zip"
$DepotDownloaderUri = "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_2.4.7/depotdownloader-2.4.7.zip"
$AppId = 258550
$FileList = @"
regex:RustDedicated_Data/Managed/.*
"@

$OxidePath = "${PSScriptRoot}\.ass\Oxide"
$CarbonPath = "${PSScriptRoot}\.ass\Carbon.Windows.Release"
$DepotDownloaderPath = "${PSScriptRoot}\.ass\.depotdownloader"
$DepotDownloaderExePath = "${PSScriptRoot}\.ass\.depotdownloader\DepotDownloader.exe"
$RustAssembliesOutputPath = "${PSScriptRoot}\.ass\.depotdownloader"
$RustAssembliesFromPath = "${PSScriptRoot}\.ass\.depotdownloader\RustDedicated_Data"
$RustAssembliesToPath = "${PSScriptRoot}\.ass\RustDedicated_Data"

function Get-Oxide() {
    if (Test-Path -Path $OxidePath) {
        Remove-Item -Path $OxidePath -Confirm
    }

    $TempPath = New-TemporaryFile

    Invoke-RestMethod -Uri $OxideModUri -OutFile $TempPath
    Expand-Archive -Path $TempPath -DestinationPath $OxidePath
}

function Get-Carbon() {
    if (Test-Path -Path $CarbonPath) {
        Remove-Item -Path $CarbonPath -Confirm
    }

    $TempPath = New-TemporaryFile

    Invoke-RestMethod -Uri $CarbonModUri -OutFile $TempPath
    Expand-Archive -Path $TempPath -DestinationPath $CarbonPath
}

function Get-DepotDownloader() {
    if (Test-Path -Path $DepotDownloaderPath) {
        Remove-Item -Path $DepotDownloaderPath -Confirm
    }

    $TempPath = New-TemporaryFile

    Invoke-RestMethod -Uri $DepotDownloaderUri -OutFile $TempPath
    Expand-Archive -Path $TempPath -DestinationPath $DepotDownloaderPath
}

function Get-RustAssemblies() {
    if (Test-Path -Path $RustAssembliesToPath) {
        Remove-Item -Path $RustAssembliesToPath -Confirm
    }

    $TempPath = New-TemporaryFile

    Set-Content -Path $TempPath -Value $FileList -NoNewline

    & $DepotDownloaderExePath -app $AppId -dir $RustAssembliesOutputPath -filelist $TempPath
    
    Move-Item -Path $RustAssembliesFromPath -Destination $RustAssembliesToPath
}

Get-Oxide
Get-Carbon
Get-DepotDownloader
Get-RustAssemblies