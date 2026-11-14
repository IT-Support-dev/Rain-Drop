; ============================================================
;  Rain Drop Master - ตัวติดตั้งสำหรับเครื่องผู้ดูแล
;  สร้างด้วย build-installer.bat (ต้องติดตั้ง Inno Setup 6.3 ขึ้นไป)
;
;  ตัวติดตั้งนี้ไม่มีกุญแจลับอยู่ข้างในแล้ว (เฟส C1)
;  กุญแจลับถูกสร้างบนเครื่อง Master ตอนเปิดครั้งแรก และเข้ารหัสด้วย DPAPI ผูกกับบัญชี Windows นั้น
; ============================================================

#define AppName    "Rain Drop Master"
#define AppVersion "1.0.0"
#define AppExe     "RainDrop.Master.exe"

[Setup]
AppId={{7E3F2A41-5C1B-4D8E-9F20-RAINDROPMASTR}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Rain Drop
DefaultDirName={autopf}\RainDrop\Master
DefaultGroupName=Rain Drop
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename=RainDrop-Master-Setup
SetupIconFile=..\assets\raindrop.ico
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=force
UsedUserAreasWarning=no

[Tasks]
Name: "desktopicon"; Description: "สร้างไอคอนบน Desktop"; GroupDescription: "ไอคอนเพิ่มเติม:"

[Files]
Source: "..\publish\Master\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; ไม่ฝังคู่กุญแจใด ๆ ในตัวติดตั้งอีกต่อไป
; Master จะสร้างคู่กุญแจเองตอนเปิดครั้งแรก เก็บกุญแจลับเข้ารหัสด้วย DPAPI ที่ %AppData%\RainDrop
; แล้วส่งออก enrollment bundle (.rdrop) เฉพาะเครื่องให้ผู้ใช้ติดตั้ง Agent ผ่านเมนู "เพิ่มเติม"

[Registry]
; อนุญาตให้ใช้ไมโครโฟน (สำหรับข้อความเสียง) ทั้งระดับเครื่องและสำหรับโปรแกรมแบบ Desktop
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"; \
    ValueType: string; ValueName: "Value"; ValueData: "Allow"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"; \
    ValueType: string; ValueName: "Value"; ValueData: "Allow"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged"; \
    ValueType: string; ValueName: "Value"; ValueData: "Allow"

[Icons]
Name: "{autoprograms}\Rain Drop Master"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Rain Drop Master"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; อนุญาตให้ Master รับคำตอบจากการค้นหาเครื่องลูกอัตโนมัติ (UDP)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Master"""; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Master"" dir=in action=allow program=""{app}\{#AppExe}"" profile=any"; \
    Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "เปิด Rain Drop Master"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Master"""; \
    Flags: runhidden; RunOnceId: "RemoveFirewall"
