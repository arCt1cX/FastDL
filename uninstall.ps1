# FastDL - disinstallazione
$hostName = 'com.fastdl.host'
foreach ($k in 'HKCU:\Software\Google\Chrome\NativeMessagingHosts',
               'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts',
               'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts') {
    Remove-Item -Path "$k\$hostName" -Force -ErrorAction SilentlyContinue
}
$dst = Join-Path $env:USERPROFILE 'FastDL'
Write-Host 'Chiavi di registro rimosse. Rimuovi anche l''estensione FastDL da chrome://extensions,'
Write-Host "poi cancella la cartella: $dst"
