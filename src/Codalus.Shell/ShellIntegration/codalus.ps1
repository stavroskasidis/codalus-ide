# Codalus IDE shell integration for PowerShell (ADR 0014): wraps the prompt to report the working directory
# (OSC 7) and command marks (OSC 133). Works in PowerShell 7 and Windows PowerShell 5.1.
if ($Global:__CodalusOriginalPrompt) { return }
$Global:__CodalusOriginalPrompt = $function:prompt
$Global:__CodalusLastHistoryId = -1

function Global:prompt {
    $succeeded = $?
    $esc = [char]27
    $bel = [char]7
    $out = ''
    $last = Get-History -Count 1
    if ($last -and $last.Id -ne $Global:__CodalusLastHistoryId) {
        $Global:__CodalusLastHistoryId = $last.Id
        $code = if ($succeeded) { 0 } elseif ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) { $LASTEXITCODE } else { 1 }
        $out += "$esc]133;D;$code$bel"
    }

    $location = $executionContext.SessionState.Path.CurrentLocation
    if ($location.Provider.Name -eq 'FileSystem') {
        $path = $location.ProviderPath -replace '\\', '/'
        if (-not $path.StartsWith('/')) { $path = '/' + $path }
        $out += "$esc]7;file://$([Environment]::MachineName)$($path -replace ' ', '%20')$bel"
    }

    $out += "$esc]133;A$bel"
    $original = & $Global:__CodalusOriginalPrompt
    return $out + $original + "$esc]133;B$bel"
}
