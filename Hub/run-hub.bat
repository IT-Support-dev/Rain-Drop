@echo off
chcp 65001 >nul
REM เปิดเซิร์ฟเวอร์ชุมชน Rain Drop Hub (รันบนเครื่องที่เปิดตลอด)
REM แนะนำให้คลิกขวา > Run as administrator เพื่อให้เปิดไฟร์วอลล์ได้
cd /d %~dp0
netsh advfirewall firewall delete rule name="Rain Drop Hub" >nul 2>&1
netsh advfirewall firewall add rule name="Rain Drop Hub" dir=in action=allow protocol=TCP localport=11500 profile=any >nul 2>&1
echo เปิด Rain Drop Hub ที่พอร์ต 11500 ... (ปิดหน้าต่างนี้เพื่อหยุดเซิร์ฟเวอร์)
RainDrop.Hub.exe
pause
