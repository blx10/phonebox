function Install-PhoneBoxApk {
    param([Parameter(Mandatory)][string]$Adb, [Parameter(Mandatory)][string]$ApkPath, [Parameter(Mandatory)][string]$Serial)
    $target = @('-s', $Serial)
    # Windows PowerShell 5.1 turns a native program's stderr lines into errors under -ErrorAction Stop (adb prints
    # "daemon not running; starting now" there), which would hide adb's real answer.
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Adb @target install -r $ApkPath 2>&1 | ForEach-Object { "$_" }; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $old }
    if ($output -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE') {
        throw 'APK signing key differs from the installed app. The installed app and its data were kept. Build with the original signing key.'
    }
    if ($code -ne 0 -or -not ($output -match '^Success\s*$')) { throw ('APK update failed; the installed app was kept. ' + ($output -join ' ')) }
    $output | Select-Object -Last 1
}
