# Registers zanburak-video:// for the current Windows user (no Administrator required).
param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"
$protocol = "zanburak-video"

if (-not $ExePath) {
    $candidates = @(
        Join-Path $PSScriptRoot "..\src\ZanburakVideoProcessor\bin\Release\net8.0-windows\win-x64\publish\ZanburakVideoProcessor.exe"
        Join-Path $PSScriptRoot "..\src\ZanburakVideoProcessor\bin\Release\net8.0-windows\ZanburakVideoProcessor.exe"
        Join-Path $PSScriptRoot "..\publish\ZanburakVideoProcessor.exe"
    )
    foreach ($c in $candidates) {
        $full = [IO.Path]::GetFullPath($c)
        if (Test-Path $full) { $ExePath = $full; break }
    }
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    Write-Error "ZanburakVideoProcessor.exe not found. Build the app first or pass -ExePath."
}

$exe = (Resolve-Path $ExePath).Path
$key = "HKCU:\Software\Classes\$protocol"
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name "(default)" -Value "URL:Zanburak Video Processor"
New-ItemProperty -Path $key -Name "URL Protocol" -Value "" -PropertyType String -Force | Out-Null
New-Item -Path "$key\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$key\DefaultIcon" -Name "(default)" -Value ('"{0},0"' -f $exe)
New-Item -Path "$key\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$key\shell\open\command" -Name "(default)" -Value ('"{0}" "%1"' -f $exe)

Write-Host ("Protocol {0}:// registered for the current user." -f $protocol)
Write-Host ("Sample: start {0}://process?task=00000000-0000-0000-0000-000000000000" -f $protocol)
Write-Host ("Exe: {0}" -f $exe)
