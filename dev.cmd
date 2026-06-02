@echo off
setlocal

cd /d %~dp0

echo Checking for an existing K9 Operator backend process...
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stop-k9-operator-api.ps1

echo Checking for an existing K9 Operator frontend process on http://localhost:4300...
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stop-k9-operator-ui.ps1

echo Starting K9 Operator backend API on http://localhost:5001...
start "K9 Operator API" cmd /k "dotnet run --project backend\K9Api\K9Api.csproj --urls http://localhost:5001"

echo Starting K9 Operator Angular frontend on http://localhost:4300...
start "K9 Operator UI" cmd /k "npm start"

echo.
echo Backend:  http://localhost:5001
echo Frontend: http://localhost:4300
echo.
echo Close the opened windows to stop the servers.
