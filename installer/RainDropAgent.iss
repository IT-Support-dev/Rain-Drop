; ============================================================
;  Rain Drop Agent - ตัวติดตั้งสำหรับเครื่องลูก
;  สร้างด้วย build-installer.bat
;
;  เฟส C1: ตัวติดตั้งไม่ฝังกุญแจใด ๆ และไม่มีรหัสผ่านเริ่มต้นอีกต่อไป
;  ผู้ติดตั้งต้องเตรียม 2 อย่าง:
;    1) รหัสผ่านผู้ดูแล ยาวอย่างน้อย 8 ตัวอักษร (ห้ามว่าง)
;    2) ไฟล์ master-public.pem ที่ export จากเครื่อง Master
;       (Master: ปุ่ม "ตั้งค่า / กุญแจ" หรือ RainDrop.Master.exe --export-public-key <โฟลเดอร์>)
;
;  ติดตั้งแบบเงียบ (ต้องส่งทั้งสองค่า ไม่งั้นตัวติดตั้งล้มเหลวทันที):
;    RainDrop-Agent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ^
;        /PASSWORD=YourStrongPass /PUBLICKEY=C:\deploy\master-public.pem
; ============================================================

#define AppName    "Rain Drop Agent"
#define AppVersion "1.0.0"
#define AppExe     "RainDrop.Agent.exe"
#define SvcExe     "RainDrop.AgentService.exe"
#define SvcName    "RainDropAgentSvc"
#define Port       "11400"
#define MinPasswordLen 8

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
; ไม่มีการฝัง master-public.pem ตอน build แล้ว ผู้ติดตั้งเลือกไฟล์เองในหน้าถัดไป
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
; 1) คัดลอกกุญแจสาธารณะที่ผู้ติดตั้งเลือก (ไม่ฝังมากับตัวติดตั้ง)
Filename: "{cmd}"; Parameters: "/c copy /y ""{code:GetPublicKeyPath}"" ""{app}\agent-public.pem"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "คัดลอกกุญแจสาธารณะ..."
; 2) เขียนค่าตั้ง: agent.json (ไม่มีรหัส) + private\credentials.json (PBKDF2 hash)
Filename: "{app}\{#AppExe}"; Parameters: "--init-config ""{commonappdata}\RainDrop"" ""{code:GetPassword}"" {#Port} ""{code:GetHub}"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังตั้งค่า..."
; 3) ปิดสิทธิ์เขียนของ Users บนโฟลเดอร์ค่าตั้ง (ใช้ SID เพื่อไม่ขึ้นกับภาษาของ Windows)
;    S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators, S-1-5-32-545 = Users
Filename: "{sys}\icacls.exe"; \
    Parameters: """{commonappdata}\RainDrop"" /inheritance:r /grant:r ""*S-1-5-18:(OI)(CI)F"" ""*S-1-5-32-544:(OI)(CI)F"" ""*S-1-5-32-545:(OI)(CI)RX"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "ตั้งสิทธิ์โฟลเดอร์ค่าตั้ง..."
;    โฟลเดอร์ private เก็บ hash รหัสผ่าน ผู้ใช้ทั่วไปไม่มีสิทธิ์แม้แต่อ่าน
Filename: "{sys}\icacls.exe"; \
    Parameters: """{commonappdata}\RainDrop\private"" /inheritance:r /grant:r ""*S-1-5-18:(OI)(CI)F"" ""*S-1-5-32-544:(OI)(CI)F"""; \
    Flags: runhidden waituntilterminated
; 4) เปิดไฟร์วอลล์ทุกโปรไฟล์ (เครือข่ายใหม่ใน Windows มักถูกตั้งเป็น Public)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Agent"""; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Agent"" dir=in action=allow protocol=TCP localport={#Port} profile=any"; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังตั้งค่าไฟร์วอลล์..."
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Agent"" dir=in action=allow protocol=UDP localport={#Port} profile=any"; \
    Flags: runhidden waituntilterminated
; 5) ติดตั้งและตั้งค่า Windows Service (สิทธิ์ SYSTEM, เริ่มอัตโนมัติตอนบูต)
Filename: "{sys}\sc.exe"; Parameters: "stop {#SvcName}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "delete {#SvcName}"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; \
    Parameters: "create {#SvcName} binPath= ""{app}\{#SvcExe}"" start= auto obj= LocalSystem DisplayName= ""Rain Drop Agent Service"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังติดตั้งบริการ..."
Filename: "{sys}\sc.exe"; Parameters: "description {#SvcName} ""ดูแลให้ Rain Drop Agent ทำงานอยู่เสมอ"""; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#SvcName} reset= 0 actions= restart/5000/restart/5000/restart/5000"; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#SvcName}"; Flags: runhidden waituntilterminated; \
    StatusMsg: "กำลังเริ่มบริการ..."

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#SvcName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#SvcName}"; Flags: runhidden; RunOnceId: "DelSvc"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Agent"""; \
    Flags: runhidden; RunOnceId: "RemoveFirewall"

[Code]
var
  PasswordPage: TInputQueryWizardPage;
  KeyPage: TInputFileWizardPage;

// อ่านค่าจากบรรทัดคำสั่ง เช่น /PASSWORD=xxx หรือ /PUBLICKEY=C:\path\key.pem
function CmdlineParam(const Name: String): String;
var
  i: Integer;
  s: String;
  prefix: String;
begin
  Result := '';
  prefix := Uppercase(Name) + '=';
  for i := 1 to ParamCount do
  begin
    s := ParamStr(i);
    if Pos(prefix, Uppercase(s)) = 1 then
      Result := Copy(s, Length(prefix) + 1, MaxInt);
  end;
end;

function PasswordOk(const P: String): Boolean;
begin
  Result := Length(P) >= {#MinPasswordLen};
end;

function GetPassword(Param: String): String;
begin
  Result := CmdlineParam('/PASSWORD');
  if (Result = '') and Assigned(PasswordPage) then
    Result := PasswordPage.Values[0];
end;

function GetPublicKeyPath(Param: String): String;
begin
  Result := CmdlineParam('/PUBLICKEY');
  if (Result = '') and Assigned(KeyPage) then
    Result := KeyPage.Values[0];
end;

function GetHub(Param: String): String;
begin
  Result := CmdlineParam('/HUB');
  if (Result = '') and Assigned(PasswordPage) then
    Result := PasswordPage.Values[1];
end;

// โหมดเงียบ: ถ้าไม่ส่งรหัสผ่านหรือกุญแจสาธารณะ ต้องล้มเหลวพร้อมข้อความชัดเจน ห้ามใช้ค่าเริ่มต้น
function InitializeSetup(): Boolean;
var
  p, k: String;
begin
  Result := True;
  if not WizardSilent then Exit;

  p := CmdlineParam('/PASSWORD');
  if not PasswordOk(p) then
  begin
    Log('Rain Drop: ติดตั้งแบบเงียบล้มเหลว - ต้องระบุ /PASSWORD= ที่ยาวอย่างน้อย {#MinPasswordLen} ตัวอักษร (ไม่มีรหัสผ่านเริ่มต้นแล้ว)');
    SuppressibleMsgBox('ติดตั้งแบบเงียบต้องระบุ /PASSWORD= ที่ยาวอย่างน้อย {#MinPasswordLen} ตัวอักษร' + #13#10 +
      'ตัวอย่าง: RainDrop-Agent-Setup.exe /VERYSILENT /PASSWORD=YourStrongPass /PUBLICKEY=C:\deploy\master-public.pem',
      mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;

  k := CmdlineParam('/PUBLICKEY');
  if (k = '') or (not FileExists(k)) then
  begin
    Log('Rain Drop: ติดตั้งแบบเงียบล้มเหลว - ต้องระบุ /PUBLICKEY= ไปยังไฟล์ master-public.pem ที่มีอยู่จริง');
    SuppressibleMsgBox('ติดตั้งแบบเงียบต้องระบุ /PUBLICKEY= ไปยังไฟล์ master-public.pem ที่ได้จากเครื่อง Master',
      mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;
end;

procedure InitializeWizard();
begin
  PasswordPage := CreateInputQueryPage(wpSelectDir,
    'ตั้งค่า Rain Drop Agent',
    'รหัสผ่านผู้ดูแล และเซิร์ฟเวอร์ชุมชน',
    'รหัสผ่าน: ผู้ใช้ทั่วไปจะปิดโปรแกรมได้ก็ต่อเมื่อใส่รหัสนี้ (บังคับอย่างน้อย {#MinPasswordLen} ตัวอักษร ไม่มีค่าเริ่มต้น)' + #13#10 +
    'เซิร์ฟเวอร์ชุมชน: IP ของเครื่องที่รัน Rain Drop Hub (เว้นว่างได้ ตั้งทีหลังก็ได้)');
  PasswordPage.Add('รหัสผ่านผู้ดูแล (อย่างน้อย {#MinPasswordLen} ตัวอักษร):', True);
  PasswordPage.Add('เซิร์ฟเวอร์ชุมชน (เช่น 192.168.1.10):', False);

  KeyPage := CreateInputFilePage(PasswordPage.ID,
    'กุญแจสาธารณะของ Master',
    'เลือกไฟล์ master-public.pem ที่ได้จากเครื่อง Master',
    'บนเครื่อง Master เปิด Rain Drop Master แล้วกด "ตั้งค่า / กุญแจ" เพื่อเปิดโฟลเดอร์ที่มีไฟล์นี้' + #13#10 +
    'ตัวติดตั้งไม่ฝังกุญแจมาให้อีกต่อไป และไม่ต้องใช้กุญแจลับบนเครื่องลูก');
  KeyPage.Add('ไฟล์ master-public.pem:', 'PEM files (*.pem)|*.pem|All files (*.*)|*.*', '.pem');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if Assigned(PasswordPage) and (CurPageID = PasswordPage.ID) then
  begin
    if not PasswordOk(PasswordPage.Values[0]) then
    begin
      MsgBox('รหัสผ่านต้องยาวอย่างน้อย {#MinPasswordLen} ตัวอักษร และห้ามเว้นว่าง', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if Assigned(KeyPage) and (CurPageID = KeyPage.ID) then
  begin
    if not FileExists(KeyPage.Values[0]) then
    begin
      MsgBox('ไม่พบไฟล์กุญแจสาธารณะที่เลือก กรุณาเลือกไฟล์ master-public.pem จากเครื่อง Master', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
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
