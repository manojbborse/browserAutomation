@echo off
cd /d "%~dp0"
dotnet run --no-build -- run %*
pause
