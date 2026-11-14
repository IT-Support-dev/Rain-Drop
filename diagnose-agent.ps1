<#
  ตรวจหาสาเหตุที่ Master เชื่อมต่อเครื่องลูกไม่ได้ (รันบนเครื่องลูก)
    .\diagnose-agent.ps1          ตรวจอย่างเดียว
    .\diagnose-agent.ps1 -Fix     ตรวจและแก้ให้อัตโนมัติ (ต้องรันแบบ Administrator)
#>
param([switch]$Fix, [int]$Port = 11400)

$exe  = Join-Path $env:ProgramFiles 'RainDrop\Agent\RainDrop.Agent.exe'
$key  = Join-Path $env:ProgramFiles 'RainDrop\Agent\agent-public.pem'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$problems = 0

function Ok($msg)   { Write-Host "  [ OK ]  $msg" -ForegroundColor Green }
function Bad($msg)  { Write-Host "  [ !! ]  $msg" -ForegroundColor Red; $script:problems++ }
function Info($msg) { Write-Host "          $msg" -ForegroundColor Gray }

Write-Host "`n=== Rain Drop Agent: ตรวจสอบการเชื่อมต่อ ===`n" -ForegroundColor Cyan
if ($Fix -and -not $isAdmin) { Write-Host "ต้องเปิด PowerShell แบบ Administrator จึงจะใช้ -Fix ได้`n" -ForegroundColor Yellow; $Fix = $false }

# 1) ติดตั้งแล้วหรือยัง
if (Test-Path $exe) { Ok "ติดตั้งโปรแกรมแล้ว: $exe" } else { Bad "ไม่พบโปรแกรม: ยังไม่ได้ติดตั้ง RainDrop-Agent-Setup.exe" }

# 2) ไฟล์กุญแจ
if (Test-Path $key) { Ok "พบไฟล์กุญแจของ Master" } else { Bad "ไม่พบไฟล์กุญแจ $key (ติดตั้งใหม่ด้วย Agent Setup)" }

# 3) โปรแกรมทำงานอยู่ไหม
$proc = Get-Process RainDrop.Agent -ErrorAction SilentlyContinue
if ($proc) { Ok "Agent กำลังทำงาน (PID $($proc.Id -join ', '))" }
else {
    Bad "Agent ไม่ได้ทำงาน (ไม่มีไอคอนโล่ที่มุมขวาล่าง)"
    Info "อาจถูกแอนตี้ไวรัสบล็อก ให้ตรวจที่ Windows Security > Protection history"
    if ($Fix -and (Test-Path $exe)) {
        # เปิดในชื่อผู้ใช้ปัจจุบันผ่าน explorer (ไม่ใช่สิทธิ์ admin)
        Start-Process explorer.exe -ArgumentList "`"$exe`""; Start-Sleep 3
        if (Get-Process RainDrop.Agent -ErrorAction SilentlyContinue) { Info "-> เปิด Agent ให้แล้ว" }
    }
}

# 4) เปิดพอร์ตรอแล้วหรือยัง
$listen = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($listen) {
    $owner = (Get-Process -Id $listen[0].OwningProcess -ErrorAction SilentlyContinue).ProcessName
    if ($owner -eq 'RainDrop.Agent') { Ok "กำลังรอการเชื่อมต่อที่พอร์ต $Port" }
    else { Bad "พอร์ต $Port ถูกโปรแกรมอื่นใช้อยู่: $owner" }
} else { Bad "ไม่มีโปรแกรมรอที่พอร์ต $Port" }

# 5) ไฟร์วอลล์
$rule = Get-NetFirewallRule -DisplayName 'Rain Drop Agent' -ErrorAction SilentlyContinue |
        Where-Object { $_.Enabled -eq 'True' } | Select-Object -First 1
$profiles = (Get-NetConnectionProfile | Select-Object -ExpandProperty NetworkCategory) -join ', '
Info "ประเภทเครือข่ายตอนนี้: $profiles"
if ($rule -and $rule.Enabled -eq 'True' -and ($rule.Profile -eq 'Any' -or "$($rule.Profile)" -match 'Public' -or $profiles -notmatch 'Public')) {
    Ok "ไฟร์วอลล์อนุญาตพอร์ต $Port (โปรไฟล์: $($rule.Profile))"
} else {
    if (-not $rule) { Bad "ไม่มีกฎไฟร์วอลล์ของ Rain Drop" }
    else { Bad "กฎไฟร์วอลล์ไม่ครอบคลุมเครือข่ายแบบ Public (กฎ: $($rule.Profile), เครือข่าย: $profiles)" }
    if ($Fix) {
        Get-NetFirewallRule -DisplayName 'Rain Drop Agent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName 'Rain Drop Agent' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Any | Out-Null
        New-NetFirewallRule -DisplayName 'Rain Drop Agent' -Direction Inbound -Protocol UDP -LocalPort $Port -Action Allow -Profile Any | Out-Null
        Info "-> เพิ่มกฎไฟร์วอลล์ทุกโปรไฟล์ให้แล้ว"
    }
}

# 6) แอนตี้ไวรัส / ไฟร์วอลล์ตัวอื่น
$av = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue |
      Where-Object { $_.displayName -notmatch 'Windows Defender|Microsoft Defender' }
if ($av) { Info "พบแอนตี้ไวรัสอื่น: $($av.displayName -join ', ') ซึ่งอาจมีไฟร์วอลล์ของตัวเอง ให้อนุญาตพอร์ต $Port ด้วย" }

# 7) IP ของเครื่องนี้
$ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
Write-Host "`n  IP ของเครื่องนี้ (ใส่ใน Master):" -ForegroundColor Cyan
$ips | ForEach-Object { Write-Host "    $($_.IPAddress)   ($($_.InterfaceAlias))" }

Write-Host ""
if ($problems -eq 0) {
    Write-Host "ฝั่งเครื่องลูกไม่พบปัญหา" -ForegroundColor Green
    Write-Host "ให้ทดสอบจากเครื่องแม่ด้วยคำสั่ง:  Test-NetConnection <IP ข้างบน> -Port $Port"
    Write-Host "ถ้าได้ TcpTestSucceeded : False แสดงว่ามีอุปกรณ์เครือข่ายกั้นระหว่างกัน (คนละ VLAN / Wi-Fi แบบ Client Isolation)"
} elseif (-not $Fix) {
    Write-Host "พบปัญหา $problems ข้อ ลองรันใหม่แบบ Administrator ด้วยคำสั่ง:  .\diagnose-agent.ps1 -Fix" -ForegroundColor Yellow
}
Write-Host ""
