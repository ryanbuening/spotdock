@echo off
setlocal

dotnet test tests\SpotDock.Tests\SpotDock.Tests.csproj -c Release
if errorlevel 1 exit /b %errorlevel%

if /I "%~1"=="local" goto :local
if /I "%~1"=="--local" goto :local

dotnet publish src\SpotDock\SpotDock.csproj -c Release -o artifacts\publish
exit /b %errorlevel%

:local
dotnet publish src\SpotDock\SpotDock.csproj -c Release -p:PublishAot=false --self-contained false -o artifacts\local
exit /b %errorlevel%
