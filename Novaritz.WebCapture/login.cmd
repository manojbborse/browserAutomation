@echo off
cd /d "%~dp0"
echo Novaritz ChatGPT login
dotnet run --no-build -- login
pause
