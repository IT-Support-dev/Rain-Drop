#Requires -RunAsAdministrator
<#
  ติดตั้ง Rain Drop Agent บนเครื่องลูกด้วยสคริปต์ (ทางเลือกแทนตัวติดตั้งสำเร็จรูป)
  ติดตั้งเป็น Windows Service ที่เปิด Agent ทุกครั้งที่บูต และปลุกกลับถ้าถูกปิด
  ตัวอย่าง:
    .\install-agent.ps1 -PublicKey .\master-public.pem -Password MyStrongPass123
    .\install-agent.ps1 -PublicKey \\server\share\master-public.pem -Password MyStrongPass123 -Port 11400
#>
param(
    [Parameter(Mandatory = $true)][string]$PublicKey,
    [string]$Source = "$PSScriptRoot\publish",
    [int]$Port = 11400,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$Hub = ''
)

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

# กุญแจสาธารณะของ Master
Copy-Item -Path $PublicKey -Destination (Join-Path $dest 'agent-public.pem') -Force

# ตั้งค่า + รหัสผ่าน (PBKDF2 hash) สำหรับการออกจากโปรแกรม
# ค่าตั้งเก็บที่ %ProgramData%\RainDrop ซึ่งตั้ง ACL ให้ผู้ใช้ทั่วไปเขียนไม่ได้
# agent.json ไม่มีรหัสผ่าน, hash อยู่ใน private\credentials.json ที่ Users อ่านไม่ได้
$configDir = Join-Path $env:ProgramData 'RainDrop'
& (Join-Path $dest 'RainDrop.Agent.exe') --init-config "$configDir" "$Password" $Port "$Hub" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ตั้งรหัสผ่านไม่สำเร็จ (exit $LASTEXITCODE) ตรวจว่ารหัสผ่านยาว >= 8 และไม่มีอักขระควบคุม" }

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
