# FastDL - installazione (solo utente corrente, niente admin)
$ErrorActionPreference = 'Stop'
$src  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dst  = Join-Path $env:USERPROFILE 'FastDL'   # cartella visibile (AppData e' nascosta)
$extId = 'akbejbcccdikgpblkeeldihaopgfbmda'
$hostName = 'com.fastdl.host'

New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item "$src\fastdl.exe" $dst -Force
if (Test-Path "$dst\extension") { Remove-Item -Recurse -Force "$dst\extension" }
Copy-Item -Recurse "$src\extension" "$dst\extension"
Copy-Item "$src\uninstall.ps1", "$src\Disinstalla.cmd", "$src\Registra.cmd" $dst -Force -ErrorAction SilentlyContinue

$manifest = Join-Path $dst "$hostName.json"
@{
    name            = $hostName
    description     = 'FastDL download engine'
    path            = 'fastdl.exe'
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$extId/")
} | ConvertTo-Json | Set-Content -Encoding ascii $manifest

foreach ($k in 'HKCU:\Software\Google\Chrome\NativeMessagingHosts',
               'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts',
               'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts') {
    New-Item -Path "$k\$hostName" -Force -Value $manifest | Out-Null
}

Set-Clipboard -Value "$dst\extension"
Write-Host ''
Write-Host 'Motore installato in' $dst -ForegroundColor Green
Write-Host ''
Write-Host 'Ultimo passo (solo la prima volta):'
Write-Host '  1. In Chrome si apre chrome://extensions'
Write-Host '  2. Attiva "Modalita sviluppatore" (in alto a destra)'
Write-Host '  3. Si apre anche la cartella FastDL: TRASCINA la cartella "extension" dentro la pagina di Chrome'
Write-Host '     (in alternativa: "Carica estensione non pacchettizzata", incolla il percorso nella barra in alto e premi Invio)'
Write-Host "     $dst\extension" -ForegroundColor Cyan
Write-Host ''
Start-Process explorer.exe "`"$dst`""
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($chrome) { Start-Process $chrome 'chrome://extensions' }
