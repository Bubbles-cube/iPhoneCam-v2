@echo off
setlocal
set "DLL=%~dp0bin\Release\net10.0-windows10.0.22621.0\iPhoneCam.Source.comhost.dll"
echo Registering: "%DLL%"
"%SystemRoot%\System32\regsvr32.exe" "%DLL%"
echo.
pause
