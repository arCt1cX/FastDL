# Compila fastdl.exe col compilatore C# gia' incluso in Windows e prepara la cartella dist\
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dist = Join-Path $root 'dist'

New-Item -ItemType Directory -Force $dist | Out-Null
& $csc /nologo /target:exe /optimize+ /platform:anycpu /out:"$dist\fastdl.exe" `
    /r:System.Web.Extensions.dll "$root\src\FastDL.cs"
if ($LASTEXITCODE -ne 0) { throw "compilazione fallita" }

if (Test-Path "$dist\extension") { Remove-Item -Recurse -Force "$dist\extension" }
Copy-Item -Recurse "$root\extension" "$dist\extension"
Copy-Item "$root\install.ps1", "$root\uninstall.ps1", "$root\Installa.cmd", "$root\Disinstalla.cmd", "$root\Registra.cmd" $dist -Force
Write-Host "OK -> $dist"
