@echo off
rem Registra il motore FastDL in Chrome / Edge / Brave (solo utente corrente)
set "M=%~dp0com.fastdl.host.json"
reg add "HKCU\Software\Google\Chrome\NativeMessagingHosts\com.fastdl.host" /ve /t REG_SZ /d "%M%" /f >nul
reg add "HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.fastdl.host" /ve /t REG_SZ /d "%M%" /f >nul
reg add "HKCU\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.fastdl.host" /ve /t REG_SZ /d "%M%" /f >nul
echo.
echo  FastDL registrato. Riapri il popup dell'estensione: il pallino deve essere verde.
echo.
pause
