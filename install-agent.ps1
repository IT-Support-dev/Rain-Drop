#Requires -RunAsAdministrator
<#
  ติดตั้ง Rain Drop Agent บนเครื่องลูกด้วยสคริปต์ (ทางเลือกแทนตัวติดตั้งสำเร็จรูป)
  ติดตั้งเป็น Windows Service ที่เปิด Agent ทุกครั้งที่บูต และปลุกกลับถ้าถูกปิด
  ตัวอย่าง (เฟส C2a - enrollment bundle):
    .\install-agent.ps1 -Bundle .\PC-01.rdrop -BundlePassword MyBundlePass12 -Password MyStrongPass123
  ตัวอย่าง (เฟส C2b - pairing code):
    .\install-agent.ps1 -Master 192.168.1.10:11400 -Code ABCD1234EFGH5678 -Password MyStrongPass123
    .\install-agent.ps1 -Master 192.168.1.10:11400 -Code (Get-Content code.txt) -Password MyStrongPass123 -Port 11400
#>
param(
    [string]$Bundle,
    [string]$BundlePassword,
    [string]$Master,
    [string]$Code,
    [string]$Source = "$PSScriptRoot\publish",
    [int]$Port = 11400,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$Hub = ''
)

# ต้องระบุ either bundle (C2a) หรือ master+code (C2b) ไม่ใช่ทั้งสองอย่างพร้อมกัน
if (-not [string]::IsNullOrEmpty($Bundle) -and -not [string]::IsNullOrEmpty($Master)) {
    throw 'ระบุได้เฉพาะอย่างใดอย่างหนึ่งระหว่าง -Bundle (C2a) หรือ -Master + -Code (C2b) ไม่ใช่ทั้งสองอย่าง'
}
if (-not [string]::IsNullOrEmpty($Bundle)) {
    if ($BundlePassword.Length -lt 12) { throw 'รหัส bundle ต้องยาวอย่างน้อย 12 ตัวอักษร' }
    if (-not (Test-Path $Bundle)) { throw "ไม่พบไฟล์ enrollment: $Bundle" }
}
if (-not [string]::IsNullOrEmpty($Master)) {
    if ([string]::IsNullOrEmpty($Code)) { throw 'เมื่อระบุ -Master ต้องระบุ -Code ด้วย (pairing code 16 ตัวอักษร)' }
    if ($Code.Length -lt 16) { throw 'Pairing code ต้องยาว 16 ตัวอักษร (base32)' }
}

# บังคับรหัสผ่านอย่างน้อย 8 ตัวอักษร ไม่มีค่าเริ่มต้น 'raindrop' อีกต่อไป (เฟส C1)
if ($Password.Length -lt 8) {
    throw 'รหัสผ่านต้องยาวอย่างน้อย 8 ตัวอักษร (ระบุ -Password ให้ครบ ห้ามเว้นว่าง)'
}

$ErrorActionPreference = 'Stop'
$svc  = 'RainDropAgentSvc'
$dest = Join-Path $env:ProgramFiles 'RainDrop\Agent'

# หยุดและลบ Service เดิม (ถ้ามี) ก่อนคัดลอกไฟล์ทับ
& sc.exe stop $svc   2>$null | Out-Null
& sc.exe delete $svc 2>$null | Out-Null
Get-Process RainDrop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $Source 'Agent\*')        -Destination $dest -Recurse -Force
Copy-Item -Path (Join-Path $Source 'AgentService\*') -Destination $dest -Recurse -Force
if (Test-Path "$PSScriptRoot\diagnose-agent.ps1") { Copy-Item "$PSScriptRoot\diagnose-agent.ps1" $dest -Force }

# C2b: enroll ด้วย pairing code ส่งผ่าน stdin ของโปรเซสลูกเท่านั้น (ไม่ขึ้น command line)
if (-not [string]::IsNullOrEmpty($Master)) {
    $configDir = Join-Path $env:ProgramData 'RainDrop'
    $codeTemp = Join-Path $env:ProgramData 'RainDrop\private\raindrop_code.tmp'
    $Code | Out-File -FilePath $codeTemp -Encoding utf8 -NoNewline
    try {
        // ส่งรหัสผ่านไฟล์ --code-file เท่านั้น (ห้ามอยู่บน command line, ห้ามส่งผ่าน stdin)
        & (Join-Path $dest 'RainDrop.Agent.exe') --enroll-pairing "$configDir" "$Master" --code-file "$codeTemp" | Out-Null
    } finally {
        if (Test-Path $codeTemp) { Remove-Item $codeTemp -Force }
    }
    if ($LASTEXITCODE -ne 0) { throw "enroll-pairing ไม่สำเร็จ (exit $LASTEXITCODE) ตรวจว่า Master เปิดพอร์ต enrollment (11401) และรหัสถูกต้องหรือไม่" }
} else {
    # C2a: ลงทะเบียนด้วย enrollment bundle เฉพาะเครื่อง (PKCS#12 มีรหัส)
    $configDir = Join-Path $env:ProgramData 'RainDrop'
    & (Join-Path $dest 'RainDrop.Agent.exe') --enroll "$configDir" "$Bundle" "$BundlePassword" "$Password" $Port "$Hub" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "enroll ไม่สำเร็จ (exit $LASTEXITCODE) ตรวจไฟล์ bundle และรหัสทั้งสองชุด" }

# ย้ำ ACL ด้วย SID มาตรฐาน (ไม่ขึ้นกับภาษา Windows): SYSTEM/Admins เต็มสิทธิ์, Users อ่านได้อย่างเดียว
& icacls.exe "$configDir" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
& icacls.exe "$configDir\private" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null

# อนุญาตไมโครโฟน (สำหรับข้อความเสียง)
$mic = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone'
New-Item -Path $mic -Force | Out-Null
Set-ItemProperty -Path $mic -Name 'Value' -Value 'Allow'

# ไฟร์วอลล์ทุกโปรไฟล์ (TCP = ควบคุม, UDP = ค้นหาอัตโนมัติ)
Get-NetFirewallRule -DisplayName 'Rain Drop Agent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'Rain Drop Agent' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'Rain Drop Agent' -Direction Inbound -Protocol UDP -LocalPort $Port -Action Allow -Profile Any | Out-Null

# ติดตั้ง Service (SYSTEM, เริ่มอัตโนมัติ, ให้ Windows เปิดใหม่เองถ้าล้ม)
& sc.exe create $svc binPath= "`"$dest\RainDrop.AgentService.exe`"" start= auto obj= LocalSystem DisplayName= "Rain Drop Agent Service" | Out-Null
& sc.exe description $svc "ดูแลให้ Rain Drop Agent ทำงานอยู่เสมอ" | Out-Null
& sc.exe failure $svc reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
& sc.exe start $svc | Out-Null

Write-Host "ติดตั้งเสร็จแล้วที่ $dest" -ForegroundColor Green
Write-Host "Service '$svc' กำลังทำงาน และจะเปิด Agent ให้ทุกครั้งที่บูตเครื่อง" -ForegroundColor Green
Write-Host "ตั้งรหัสผ่านผู้ดูแลแล้ว (ไม่แสดงรหัสเพื่อความปลอดภัย) รหัสถูกเก็บเป็น hash ใน $configDir\private" -ForegroundColor Yellow
if (-not [string]::IsNullOrEmpty($Master)) {
    Write-Host "Pairing code ถูกอ่านจาก stdin ของโปรเซสลูกแล้วทิ้งทันที ไม่เหลือบนเครื่องหลังติดตั้ง" -ForegroundColor Yellow
}
