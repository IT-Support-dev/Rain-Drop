@echo off
REM ต้องติดตั้ง .NET 8 SDK ก่อน: https://dotnet.microsoft.com/download/dotnet/8.0
cd /d %~dp0
dotnet publish Agent\Agent.csproj   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Agent  || goto :err
dotnet publish Master\Master.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Master || goto :err
dotnet publish AgentService\AgentService.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\AgentService || goto :err
copy /y install-agent.ps1 publish\ >nul
copy /y diagnose-agent.ps1 publish\ >nul
echo.
echo Build OK  ->  publish\Agent , publish\Master
goto :eof
:err
echo Build FAILED
exit /b 1
