; ============================================================
;  Rain Drop Agent - ตัวติดตั้งสำหรับเครื่องลูก
;  สร้างด้วย build-installer.bat
;
;  กด Install แล้วจบ: โปรแกรมจะติดตั้งเป็น Windows Service (สิทธิ์ SYSTEM)
;  ที่เปิด Agent ทุกครั้งที่บูตเครื่อง และปลุกกลับทันทีถ้าถูกปิด
;  ผู้ใช้ทั่วไปปิดไม่ได้ ต้องใช้รหัสของผู้ดูแลระบบ
;
;  ติดตั้งแบบเงียบ (หลายเครื่องพร้อมกัน) พร้อมกำหนดรหัส:
;    RainDrop-Agent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /PASSWORD=mypass
; ============================================================

#define AppName    "Rain Drop Agent"
#define AppVersion "1.0.0"
#define AppExe     "RainDrop.Agent.exe"
#define SvcExe     "RainDrop.AgentService.exe"
#define SvcName    "RainDropAgentSvc"
#define Port       "11400"

[Setup]
AppId={{7E3F2A41-5C1B-4D8E-9F20-RAINDROPAGENT}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Rain Drop
DefaultDirName={autopf}\RainDrop\Agent
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\installer-output
OutputBaseFilename=RainDrop-Agent-Setup
SetupIconFile=..\assets\raindrop.ico
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=force
UsedUserAreasWarning=no

[Files]
Source: "..\publish\Agent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\publish\AgentService\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; กุญแจสาธารณะของ Master ถูกฝังมาในตัวติดตั้ง ไม่ต้องคัดลอกเอง
Source: "..\keys\master-public.pem"; DestDir: "{app}"; DestName: "agent-public.pem"; Flags: ignoreversion
; สคริปต์ตรวจสอบปัญหาการเชื่อมต่อ
Source: "..\diagnose-agent.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; อนุญาตให้ใช้ไมโครโฟน (สำหรับข้อความเสียง) ทั้งระดับเครื่องและสำหรับโปรแกรมแบบ Desktop
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"; \
    ValueType: string; ValueName: "Value"; ValueData: "Allow"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"; \
    ValueType: string; ValueName: "Value"; ValueData: "Allow"
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged"; \
    ValueType: string; ValueName: "Value"; ValueData: "Allow"

[Run]
; 1) เขียนไฟล์ตั้งค่าพร้อมรหัสผ่าน (hash) สำหรับการออกจากโปรแกรม
Filename: "{app}\{#AppExe}"; Parameters: "--init-config ""{app}"" ""{code:GetPassword}"" {#Port} ""{code:GetHub}"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังตั้งค่า..."
; 2) เปิดไฟร์วอลล์ทุกโปรไฟล์ (เครือข่ายใหม่ใน Windows มักถูกตั้งเป็น Public)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Agent"""; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Agent"" dir=in action=allow protocol=TCP localport={#Port} profile=any"; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังตั้งค่าไฟร์วอลล์..."
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Agent"" dir=in action=allow protocol=UDP localport={#Port} profile=any"; \
    Flags: runhidden waituntilterminated
; 3) ติดตั้งและตั้งค่า Windows Service (สิทธิ์ SYSTEM, เริ่มอัตโนมัติตอนบูต)
Filename: "{sys}\sc.exe"; Parameters: "stop {#SvcName}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete {#SvcName}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; \
    Parameters: "create {#SvcName} binPath= ""{app}\{#SvcExe}"" start= auto obj= LocalSystem DisplayName= ""Rain Drop Agent Service"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังติดตั้งบริการ..."
Filename: "{sys}\sc.exe"; Parameters: "description {#SvcName} ""ดูแลให้ Rain Drop Agent ทำงานอยู่เสมอ"""; \
    Flags: runhidden waituntilterminated
; ให้ Windows เปิด Service ใหม่เองถ้าล้ม (หลัง 5 วินาที)
Filename: "{sys}\sc.exe"; Parameters: "failure {#SvcName} reset= 0 actions= restart/5000/restart/5000/restart/5000"; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#SvcName}"; Flags: runhidden waituntilterminated; \
    StatusMsg: "กำลังเริ่มบริการ..."

[UninstallRun]
; หยุดและลบ Service ก่อน แล้วค่อยปิด Agent และลบกฎไฟร์วอลล์
Filename: "{sys}\sc.exe"; Parameters: "stop {#SvcName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#SvcName}"; Flags: runhidden; RunOnceId: "DelSvc"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Agent"""; \
    Flags: runhidden; RunOnceId: "RemoveFirewall"

[Code]
var
  PasswordPage: TInputQueryWizardPage;

// รหัสผ่านจากบรรทัดคำสั่ง /PASSWORD=xxx (ใช้ตอนติดตั้งแบบเงียบ)
function CmdlinePassword(): String;
var
  i: Integer;
  s: String;
begin
  Result := '';
  for i := 1 to ParamCount do
  begin
    s := ParamStr(i);
    if Pos('/PASSWORD=', Uppercase(s)) = 1 then
      Result := Copy(s, Length('/PASSWORD=') + 1, MaxInt);
  end;
end;

procedure InitializeWizard();
begin
  // หน้าให้ตั้งรหัสผ่านของผู้ดูแล (เฉพาะตอนติดตั้งแบบมีหน้าต่าง)
  PasswordPage := CreateInputQueryPage(wpSelectDir,
    'ตั้งค่า Rain Drop Agent',
    'รหัสผ่านผู้ดูแล และเซิร์ฟเวอร์ชุมชน',
    'รหัสผ่าน: ผู้ใช้ทั่วไปจะปิดโปรแกรมได้ก็ต่อเมื่อใส่รหัสนี้ (เว้นว่าง = "raindrop")' + #13#10 +
    'เซิร์ฟเวอร์ชุมชน: IP ของเครื่องที่รัน Rain Drop Hub (เว้นว่างได้ ตั้งทีหลังก็ได้)');
  PasswordPage.Add('รหัสผ่าน:', True);
  PasswordPage.Add('เซิร์ฟเวอร์ชุมชน (เช่น 192.168.1.10):', False);
end;

function GetPassword(Param: String): String;
begin
  Result := CmdlinePassword();
  if (Result = '') and Assigned(PasswordPage) and (PasswordPage.Values[0] <> '') then
    Result := PasswordPage.Values[0];
  if Result = '' then
    Result := 'raindrop';
end;

// ที่อยู่เซิร์ฟเวอร์ชุมชนจาก /HUB=... หรือจากช่องกรอกในหน้ารหัสผ่าน (ช่องที่ 2)
function GetHub(Param: String): String;
var
  i: Integer;
  s: String;
begin
  Result := '';
  for i := 1 to ParamCount do
  begin
    s := ParamStr(i);
    if Pos('/HUB=', Uppercase(s)) = 1 then
      Result := Copy(s, Length('/HUB=') + 1, MaxInt);
  end;
  if (Result = '') and Assigned(PasswordPage) then
    Result := PasswordPage.Values[1];
end;

// ปิด Agent/Service ตัวเก่าก่อนคัดลอกไฟล์ทับ (กรณีอัปเดต)
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#SvcName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
