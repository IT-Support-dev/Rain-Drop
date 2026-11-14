@echo off
chcp 65001 >nul
setlocal
REM ============================================================
REM  สร้างตัวติดตั้งสำเร็จรูปของ Rain Drop: ดับเบิลคลิกไฟล์นี้ไฟล์เดียว
REM  ถ้ายังไม่มี .NET 8 SDK หรือ Inno Setup 6 สคริปต์จะติดตั้งให้ผ่าน winget
REM  ผลลัพธ์: installer-output\RainDrop-Master-Setup.exe
REM          installer-output\RainDrop-Agent-Setup.exe
REM ============================================================
cd /d %~dp0

REM ---------- [0] ตรวจและติดตั้งเครื่องมือที่ต้องใช้ ----------
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
"%DOTNET%" --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
  echo [0/5] ไม่พบ .NET 8 SDK กำลังติดตั้ง...
  winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-package-agreements --accept-source-agreements
  "%DOTNET%" --list-sdks 2>nul | findstr /b "8." >nul || goto :need_dotnet
)

call :find_iscc
if not defined ISCC (
  echo [0/5] ไม่พบ Inno Setup กำลังติดตั้ง...
  winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
  call :find_iscc
  if not defined ISCC goto :need_inno
)

REM ---------- [1-2] Build ----------
echo [1/5] Build Agent...
"%DOTNET%" publish Agent\Agent.csproj   -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Agent  || goto :err
"%DOTNET%" publish AgentService\AgentService.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\AgentService || goto :err
echo [2/5] Build Master...
"%DOTNET%" publish Master\Master.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Master || goto :err
echo [3/5] Build Hub (เซิร์ฟเวอร์กลาง)...
"%DOTNET%" publish Hub\Hub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Hub || goto :err

REM ---------- [4] คู่กุญแจ ----------
echo [4/5] เตรียมคู่กุญแจ...
if exist keys\master-private.pem (
  echo       ใช้กุญแจเดิมในโฟลเดอร์ keys
) else (
  start "" /wait publish\Master\RainDrop.Master.exe --generate-keys "%~dp0keys"
  if not exist keys\master-private.pem goto :err
  echo       สร้างกุญแจใหม่ในโฟลเดอร์ keys แล้ว  ** สำรองโฟลเดอร์ keys ไว้ในที่ปลอดภัย **
)
REM กุญแจชุมชนของ Hub (ลายนิ้วมือต้องตรงกับ Agent/Master)
copy /y keys\master-public.pem publish\Hub\hub-key.pem >nul
copy /y Hub\run-hub.bat publish\Hub\ >nul

REM ---------- [5] ตัวติดตั้ง ----------
echo [5/5] สร้างตัวติดตั้ง...
"%ISCC%" /Q installer\RainDropAgent.iss  || goto :err
"%ISCC%" /Q installer\RainDropMaster.iss || goto :err

echo.
echo ================================================
echo  สำเร็จ!
echo   ตัวติดตั้ง (installer-output):
echo    - RainDrop-Master-Setup.exe : เครื่องผู้ดูแล (ห้ามแจกจ่าย)
echo    - RainDrop-Agent-Setup.exe  : เครื่องลูกทุกเครื่อง
echo   เซิร์ฟเวอร์ชุมชน (รันบนเครื่องที่เปิดตลอด):
echo    - publish\Hub\RainDrop.Hub.exe  (ดับเบิลคลิกเพื่อเปิด)
echo ================================================
explorer installer-output
pause
goto :eof

:find_iscc
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
goto :eof

:need_dotnet
echo [ผิดพลาด] ติดตั้ง .NET 8 SDK อัตโนมัติไม่สำเร็จ กรุณาติดตั้งเองจาก https://dotnet.microsoft.com/download/dotnet/8.0
pause
exit /b 1

:need_inno
echo [ผิดพลาด] ติดตั้ง Inno Setup อัตโนมัติไม่สำเร็จ กรุณาติดตั้งเองจาก https://jrsoftware.org/isdl.php
pause
exit /b 1

:err
echo.
echo [ผิดพลาด] สร้างตัวติดตั้งไม่สำเร็จ ดูข้อความ error ด้านบน
pause
exit /b 1
