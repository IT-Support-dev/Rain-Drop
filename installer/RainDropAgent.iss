; ============================================================
;  Rain Drop Agent - ตัวติดตั้งสำหรับเครื่องลูก
;  สร้างด้วย build-installer.bat
;
;  เฟส C2a: ตัวติดตั้งไม่ฝังกุญแจใด ๆ และไม่มีรหัสผ่านเริ่มต้น
;  ผู้ติดตั้งต้องเตรียม 3 อย่าง:
;    1) รหัสผ่านผู้ดูแล ยาวอย่างน้อย 8 ตัวอักษร (ห้ามว่าง)
;    2) ไฟล์ enrollment bundle (.rdrop) ที่ส่งออกเฉพาะเครื่องนี้จาก Master
;    3) รหัสของ bundle นั้น (อย่างน้อย 12 ตัวอักษร ใช้ถอด PKCS#12 ตอนติดตั้งเท่านั้น)
;
;  ติดตั้งแบบเงียบ (ต้องส่งครบ ไม่งั้นล้มเหลวทันที):
;    RainDrop-Agent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ^
;        /PASSWORD=YourStrongPass /BUNDLE=C:\deploy\PC-01.rdrop /BUNDLEPASSWORD=YourBundlePass12
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
; ไม่ฝัง enrollment bundle ตอน build ผู้ติดตั้งเลือกไฟล์เฉพาะเครื่องในหน้าถัดไป
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

; 1) ถอด enrollment bundle ด้วยรหัส แล้วเขียน identity ลง private ที่ Users เข้าไม่ได้
Filename: "{app}\{#AppExe}"; Parameters: "--enroll ""{commonappdata}\RainDrop"" ""{code:GetBundlePath}"" ""{code:GetBundlePassword}"" ""{code:GetPassword}"" {#Port} ""{code:GetHub}"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังลงทะเบียนเครื่อง..."
; 2) ปิดสิทธิ์เขียนของ Users บนโฟลเดอร์ค่าตั้ง (ใช้ SID เพื่อไม่ขึ้นกับภาษาของ Windows)
;    S-1-5-18 = SYSTEM, S-1-5-32-544 = Administrators, S-1-5-32-545 = Users
Filename: "{sys}\icacls.exe"; \
    Parameters: """{commonappdata}\RainDrop"" /inheritance:r /grant:r ""*S-1-5-18:(OI)(CI)F"" ""*S-1-5-32-544:(OI)(CI)F"" ""*S-1-5-32-545:(OI)(CI)RX"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "ตั้งสิทธิ์โฟลเดอร์ค่าตั้ง..."
;    โฟลเดอร์ private เก็บ hash รหัสผ่าน ผู้ใช้ทั่วไปไม่มีสิทธิ์แม้แต่อ่าน
Filename: "{sys}\icacls.exe"; \
    Parameters: """{commonappdata}\RainDrop\private"" /inheritance:r /grant:r ""*S-1-5-18:(OI)(CI)F"" ""*S-1-5-32-544:(OI)(CI)F"""; \
    Flags: runhidden waituntilterminated
; 3) เปิดไฟร์วอลล์ทุกโปรไฟล์ (เครือข่ายใหม่ใน Windows มักถูกตั้งเป็น Public)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Rain Drop Agent"""; \
    Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Agent"" dir=in action=allow protocol=TCP localport={#Port} profile=any"; \
    Flags: runhidden waituntilterminated; StatusMsg: "กำลังตั้งค่าไฟร์วอลล์..."
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Rain Drop Agent"" dir=in action=allow protocol=UDP localport={#Port} profile=any"; \
    Flags: runhidden waituntilterminated
; 4) ติดตั้งและตั้งค่า Windows Service (สิทธิ์ SYSTEM, เริ่มอัตโนมัติตอนบูต)
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
  BundlePage: TInputFileWizardPage;
  BundlePasswordPage: TInputQueryWizardPage;

// อ่านค่าจากบรรทัดคำสั่ง เช่น /PASSWORD=xxx หรือ /BUNDLE=C:\path\PC-01.rdrop
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

function GetBundlePath(Param: String): String;
begin
  Result := CmdlineParam('/BUNDLE');
  if (Result = '') and Assigned(BundlePage) then
    Result := BundlePage.Values[0];
end;

function GetBundlePassword(Param: String): String;
begin
  Result := CmdlineParam('/BUNDLEPASSWORD');
  if (Result = '') and Assigned(BundlePasswordPage) then
    Result := BundlePasswordPage.Values[0];
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

  k := CmdlineParam('/BUNDLE');
  if (k = '') or (not FileExists(k)) then
  begin
    Log('Rain Drop: ติดตั้งแบบเงียบล้มเหลว - ต้องระบุ /BUNDLE= ไปยังไฟล์ .rdrop ที่มีอยู่จริง');
    SuppressibleMsgBox('ติดตั้งแบบเงียบต้องระบุ /BUNDLE= ไปยังไฟล์ enrollment ของเครื่องนี้', mbCriticalError, MB_OK, MB_OK);
    Result := False;
    Exit;
  end;
  if Length(CmdlineParam('/BUNDLEPASSWORD')) < 12 then
  begin
    SuppressibleMsgBox('ติดตั้งแบบเงียบต้องระบุ /BUNDLEPASSWORD= อย่างน้อย 12 ตัวอักษร', mbCriticalError, MB_OK, MB_OK);
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

  BundlePage := CreateInputFilePage(PasswordPage.ID,
    'Enrollment bundle ของเครื่องนี้',
    'เลือกไฟล์ .rdrop ที่ส่งออกเฉพาะเครื่องนี้จาก Master',
    'Master: เพิ่มเติม → ส่งออก enrollment bundle' + #13#10 +
    'ไฟล์นี้มีใบรับรองและกุญแจลับของ Agent เข้ารหัสด้วยรหัสผ่าน ห้ามใช้ไฟล์เดียวกันหลายเครื่อง');
  BundlePage.Add('ไฟล์ enrollment:', 'Rain Drop enrollment (*.rdrop)|*.rdrop|All files (*.*)|*.*', '.rdrop');
  BundlePasswordPage := CreateInputQueryPage(BundlePage.ID,
    'รหัสของ enrollment bundle',
    'ใส่รหัสที่ใช้ตอนส่งออกไฟล์นี้จาก Master',
    'รหัสนี้ใช้ถอดกุญแจตอนติดตั้งเท่านั้น ไม่ถูกเก็บลงเครื่องลูก');
  BundlePasswordPage.Add('รหัส bundle (อย่างน้อย 12 ตัวอักษร):', True);
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

  if Assigned(BundlePage) and (CurPageID = BundlePage.ID) then
  begin
    if not FileExists(BundlePage.Values[0]) then
    begin
      MsgBox('ไม่พบไฟล์ enrollment ที่เลือก', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
  if Assigned(BundlePasswordPage) and (CurPageID = BundlePasswordPage.ID) then
  begin
    if Length(BundlePasswordPage.Values[0]) < 12 then
    begin
      MsgBox('รหัส bundle ต้องยาวอย่างน้อย 12 ตัวอักษร', mbError, MB_OK);
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
