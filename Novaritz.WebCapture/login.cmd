@echo off
cd /d "%~dp0"
echo Novaritz ChatGPT login
dotnet run -c Release --no-build -- login %*
pause
