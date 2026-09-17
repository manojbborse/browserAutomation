@echo off
cd /d "%~dp0"
dotnet run -c Release --no-build -- run %*
pause
