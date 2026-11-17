<#
.SYNOPSIS
  Genera las capturas de la interfaz que se muestran en el README (docs/screenshots).

.DESCRIPTION
  Crea datos FICTICIOS de demostración (jobs e historial) en una carpeta temporal, abre la
  interfaz con SQLBACKUPN_DATA apuntando a esa carpeta, captura la ventana y la cierra.
  No toca la configuración real de %ProgramData%. Requiere haber compilado la solución.

.EXAMPLE
  dotnet build SqlBackupN.sln
  powershell -ExecutionPolicy Bypass -File tools\Capture-Screenshots.ps1
#>
param([string]$Repo = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NativeWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}
"@
[void][NativeWin]::SetProcessDPIAware()

$exe = Join-Path $Repo "src\SqlBackupN.App\bin\Debug\net9.0-windows\SqlBackupN.exe"
if (-not (Test-Path $exe)) { throw "No se encontró $exe. Ejecute 'dotnet build SqlBackupN.sln' primero." }
$outDir = Join-Path $Repo "docs\screenshots"
New-Item -ItemType Directory $outDir -Force | Out-Null

# ---------- Datos ficticios ----------
$today = (Get-Date).Date
function Utc($daysAgo, $hour, $minute = 0) {
    $today.AddDays(-$daysAgo).AddHours($hour).AddMinutes($minute).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

function New-Job($id, $name, $server, [string[]]$dbNames, $steps, $dests, $retDays, $hook) {
    [ordered]@{
        id = $id; name = $name; enabled = $true; createdUtc = (Utc 40 0)
        connection = [ordered]@{ server = $server; useWindowsAuthentication = $true; trustServerCertificate = $true }
        databases = [ordered]@{
            mode = $(if ($dbNames.Count -eq 0) { "AllUserDatabases" } else { "Explicit" })
            names = @($dbNames); exclude = @()
        }
        steps = @($steps | ForEach-Object { [ordered]@{ id = [guid]::NewGuid().ToString(); type = $_[0]; cron = $_[1]; enabled = $true } })
        destinations = @($dests | ForEach-Object { [ordered]@{ name = $_[0]; kind = "Folder"; path = $_[1] } })
        options = [ordered]@{ compression = $true; checksum = $true; copyOnly = $false; verifyAfterBackup = $true; commandTimeoutSeconds = 0
                              pathTemplate = '{Server}\{Database}\{Type}\{Database}_{Type}_{Timestamp}' }
        retention = [ordered]@{ keepDays = $retDays; keepLast = 3 }
        notifications = [ordered]@{ webhookUrl = $hook; onSuccess = $false; onWarning = $true; onFailure = $true }
    }
}

$idVentas = "11111111-1111-1111-1111-111111111111"
$idConta  = "22222222-2222-2222-2222-222222222222"
$idLogs   = "33333333-3333-3333-3333-333333333333"

$jobVentas = New-Job $idVentas "Ventas nocturno" "SRV-DEMO\SQL2019" @() `
    @(@("Full", "0 22 * * 0"), @("Differential", "0 22 * * 1-6"), @("TransactionLog", "*/15 * * * *")) `
    @(@("Disco local", "D:\Backups"), @("NAS oficina", "\\nas01\backups\sql")) 30 "https://hooks.example.test/aviso"
$jobConta = New-Job $idConta "Contabilidad" "SRV-DEMO\SQL2019" @("Contabilidad", "Nomina") `
    @(,@("Full", "0 23 * * *")) `
    @(@("Disco local", "D:\Backups"), @("NAS oficina", "\\nas01\backups\sql")) 14 "https://hooks.example.test/aviso"
$jobLogs = New-Job $idLogs "Inventario (logs)" "SRV-DEMO\SQL2019" @("Inventario") `
    @(,@("TransactionLog", "*/30 * * * *")) `
    @(,@("Disco local", "D:\Backups")) 7 ""

function New-Result($db, $status, $sizeMb, $seconds, $error = $null, $warnings = @()) {
    [ordered]@{
        database = $db; status = $status; filePath = "D:\Backups\SRV-DEMO_SQL2019\$db\FULL\$db.bak"
        sizeBytes = [long]($sizeMb * 1MB); duration = [TimeSpan]::FromSeconds($seconds).ToString()
        error = $error; warnings = @($warnings)
    }
}
function New-Rec($id, $name, $daysAgo, $hour, $type, $status, $results) {
    [ordered]@{
        id = [guid]::NewGuid().ToString(); jobId = $id; jobName = $name; stepId = [guid]::NewGuid().ToString()
        type = $type; startedUtc = (Utc $daysAgo $hour); finishedUtc = (Utc $daysAgo $hour 4)
        status = $status; results = @($results)
    }
}

$history = @()
# Ventas: todo correcto
$history += New-Rec $idVentas "Ventas nocturno" 0 7  "TransactionLog" "Succeeded" @((New-Result "Ventas" "Succeeded" 12 3), (New-Result "Compras" "Succeeded" 4 2))
$history += New-Rec $idVentas "Ventas nocturno" 1 22 "Differential"   "Succeeded" @((New-Result "Ventas" "Succeeded" 310 41), (New-Result "Compras" "Succeeded" 95 18))
$history += New-Rec $idVentas "Ventas nocturno" 2 22 "Differential"   "Succeeded" @((New-Result "Ventas" "Succeeded" 298 39), (New-Result "Compras" "Succeeded" 90 17))
$history += New-Rec $idVentas "Ventas nocturno" 3 22 "Full"           "Succeeded" @((New-Result "Ventas" "Succeeded" 2150 212), (New-Result "Compras" "Succeeded" 640 96))
# Contabilidad: advertencia reciente (falló la copia al NAS)
$warn = "No se pudo copiar a 'NAS oficina': el recurso de red no está disponible."
$history += New-Rec $idConta "Contabilidad" 1 23 "Full" "Warning"   @((New-Result "Contabilidad" "Warning" 880 94 $null @($warn)), (New-Result "Nomina" "Warning" 120 21 $null @($warn)))
$history += New-Rec $idConta "Contabilidad" 2 23 "Full" "Succeeded" @((New-Result "Contabilidad" "Succeeded" 874 92), (New-Result "Nomina" "Succeeded" 118 20))
$history += New-Rec $idConta "Contabilidad" 3 23 "Full" "Succeeded" @((New-Result "Contabilidad" "Succeeded" 870 91), (New-Result "Nomina" "Succeeded" 117 20))
# Inventario: fallos de log
$err = "BACKUP LOG no es válido: la base 'Inventario' usa el modelo de recuperación SIMPLE."
$history += New-Rec $idLogs "Inventario (logs)" 0 8 "TransactionLog" "Failed" @(New-Result "Inventario" "Failed" 0 1 $err)
$history += New-Rec $idLogs "Inventario (logs)" 0 7 "TransactionLog" "Failed" @(New-Result "Inventario" "Failed" 0 1 $err)

$scenarios = @(
    @{ file = "01-panel-principal";   order = @($jobVentas, $jobConta, $jobLogs) },
    @{ file = "02-job-con-advertencia"; order = @($jobConta, $jobVentas, $jobLogs) },
    @{ file = "03-job-con-fallo";     order = @($jobLogs, $jobVentas, $jobConta) }
)

$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($s in $scenarios) {
    $dir = Join-Path $env:TEMP "sqlbackupn-shot-$($s.file)"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir "jobs.json"), (ConvertTo-Json -InputObject @($s.order) -Depth 8), $utf8)
    $lines = $history | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 8 -Compress }
    [IO.File]::WriteAllLines((Join-Path $dir "history.jsonl"), $lines, $utf8)

    $env:SQLBACKUPN_DATA = $dir
    $proc = Start-Process $exe -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(25)
        do { Start-Sleep -Milliseconds 300; $proc.Refresh() } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
        if ($proc.MainWindowHandle -eq 0) { throw "La ventana no apareció ($($s.file))." }
        Start-Sleep -Seconds 3   # deja cargar jobs e historial

        $rect = New-Object NativeWin+RECT
        [void][NativeWin]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)
        $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][NativeWin]::PrintWindow($proc.MainWindowHandle, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT
        $g.ReleaseHdc($hdc); $g.Dispose()
        $path = Join-Path $outDir "$($s.file).png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        Write-Host "OK $path ($w x $h)"
    }
    finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
