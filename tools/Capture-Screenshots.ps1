<#
.SYNOPSIS
  Genera las capturas de la interfaz que se muestran en el README (docs/screenshots).

.DESCRIPTION
  Crea datos FICTICIOS de demostración (jobs e historial) en una carpeta temporal, abre la
  interfaz con SQLBACKUPN_DATA apuntando a esa carpeta y captura la ventana principal y el
  editor de jobs (pestaña por pestaña, usando UI Automation). No toca la configuración real
  de %ProgramData%. Requiere haber compilado la solución.

.PARAMETER Only
  Códigos de escenario a ejecutar (01…09); sirve para repetir solo una parte. Los enlaces de los .md se recalculan igual.

.PARAMETER LinksOnly
  No abre la aplicación ni captura nada: solo reescribe en README.md y docs\*.md los enlaces a docs\screenshots\*.png
  añadiendo "?v=<huella>" (8 primeros caracteres del SHA-256 de la imagen). Así, cuando una imagen cambia, su
  dirección cambia y navegadores y CDN no muestran la versión anterior guardada en caché. Se ejecuta solo al
  terminar una captura normal.

.PARAMETER Exe
  Ruta de SqlBackupN.exe. Por defecto, la compilación Debug de la carpeta del repositorio.

.EXAMPLE
  dotnet build SqlBackupN.sln
  powershell -ExecutionPolicy Bypass -File tools\Capture-Screenshots.ps1
#>
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$Exe = "",
    [string[]]$Only = @(),   # solo estos escenarios, p. ej. -Only 05,08 (por defecto, todos)
    [switch]$LinksOnly   # no captura nada: solo actualiza los enlaces de los .md con la huella de cada imagen
)

$ErrorActionPreference = "Stop"
# "-Only 05,08" llega como un solo texto cuando se usa -File: se separa por comas.
$Only = @($Only | ForEach-Object { "$_" -split "," } | Where-Object { $_ })
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
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

# ---------------------------------------------------------------- Enlaces con huella (evita imágenes en caché)
function Update-ImageLinks {
    $shots = Join-Path $Repo "docs\screenshots"
    $docs = @(Join-Path $Repo "README.md") + @(Get-ChildItem (Join-Path $Repo "docs") -Filter *.md -File | ForEach-Object FullName)
    $hashes = @{}
    foreach ($png in Get-ChildItem $shots -Filter *.png) {
        $hashes[$png.Name] = (Get-FileHash $png.FullName -Algorithm SHA256).Hash.Substring(0, 8).ToLowerInvariant()
    }
    foreach ($doc in $docs) {
        $text = [IO.File]::ReadAllText($doc)
        $updated = $text
        foreach ($name in $hashes.Keys) {
            $pattern = "(screenshots/" + [regex]::Escape($name) + ")(\?v=[0-9a-f]+)?"
            $updated = [regex]::Replace($updated, $pattern, ('$1?v=' + $hashes[$name]))
        }
        if ($updated -ne $text) {
            [IO.File]::WriteAllText($doc, $updated, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "Enlaces actualizados: $doc"
        }
    }
}

if ($LinksOnly) { Update-ImageLinks; return }

if (-not $Exe) { $Exe = Join-Path $Repo "src\SqlBackupN.App\bin\Debug\net9.0-windows\SqlBackupN.exe" }
if (-not (Test-Path $Exe)) { throw "No se encontró $Exe. Ejecute 'dotnet build SqlBackupN.sln' primero." }
$outDir = Join-Path $Repo "docs\screenshots"
New-Item -ItemType Directory $outDir -Force | Out-Null

# ---------------------------------------------------------------- Datos ficticios
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
$idRota   = "44444444-4444-4444-4444-444444444444"

$jobVentas = New-Job $idVentas "Ventas nocturno" "SRV-DEMO\SQL2019" @() `
    @(@("Full", "0 22 * * 0"), @("Differential", "0 22 * * 1-6"), @("TransactionLog", "*/15 * * * *")) `
    @(@("Disco local", "D:\Backups"), @("NAS oficina", "\\nas01\backups\sql")) 30 "https://hooks.example.test/aviso"
$jobConta = New-Job $idConta "Contabilidad" "SRV-DEMO\SQL2019" @("Contabilidad", "Nomina") `
    @(,@("Full", "0 23 * * *")) `
    @(@("Disco local", "D:\Backups"), @("NAS oficina", "\\nas01\backups\sql")) 14 "https://hooks.example.test/aviso"
$jobLogs = New-Job $idLogs "Inventario (logs)" "SRV-DEMO\SQL2019" @("Inventario") `
    @(,@("TransactionLog", "*/30 * * * *")) `
    @(,@("Disco local", "D:\Backups")) 7 ""
# Job escrito a mano con errores: sirve para mostrar la validación del editor.
$jobRota = New-Job $idRota "Tesorería (revisar)" "SRV-DEMO\SQL2019" @("Tesoreria") `
    @(@("Full", "99 25 * * *"), @("Differential", "0 22 * * 1-6")) `
    @(,@("Carpeta local", "Backups\Tesoreria")) 30 "no-es-una-url"

# Job con una copia en Google Drive (conectada) y otro con Drive sin conectar.
# Las credenciales de Google son compartidas (google-client.json en la carpeta de datos), no van en el destino.
$driveOk = [ordered]@{ name = "Google Drive"; kind = "GoogleDrive"; path = ""
    drive = [ordered]@{ clientId = ""; protectedClientSecret = $null; protectedRefreshToken = "demo"
                        accountEmail = "respaldos@centinela.example"; folderName = "SQL Backup N" } }
$driveSin = [ordered]@{ name = "Google Drive"; kind = "GoogleDrive"; path = ""
    drive = [ordered]@{ clientId = ""; protectedClientSecret = $null; protectedRefreshToken = $null
                        accountEmail = $null; folderName = "SQL Backup N" } }
$jobNube = New-Job "55555555-5555-5555-5555-555555555555" "Respaldo en la nube" "SRV-DEMO\SQL2019" @("Contabilidad") `
    @(,@("Full", "0 23 * * *")) @(,@("Disco local", "D:\Backups")) 30 "https://hooks.example.test/aviso"
$jobNube.destinations = @($jobNube.destinations) + @($driveOk)
$jobNubeSin = New-Job "66666666-6666-6666-6666-666666666666" "Nube sin conectar" "SRV-DEMO\SQL2019" @("Contabilidad") `
    @(,@("Full", "0 23 * * *")) @(,@("Disco local", "D:\Backups")) 30 ""
$jobNubeSin.destinations = @($jobNubeSin.destinations) + @($driveSin)

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
$history += New-Rec $idVentas "Ventas nocturno" 0 7  "TransactionLog" "Succeeded" @((New-Result "Ventas" "Succeeded" 12 3), (New-Result "Compras" "Succeeded" 4 2))
$history += New-Rec $idVentas "Ventas nocturno" 1 22 "Differential"   "Succeeded" @((New-Result "Ventas" "Succeeded" 310 41), (New-Result "Compras" "Succeeded" 95 18))
$history += New-Rec $idVentas "Ventas nocturno" 2 22 "Differential"   "Succeeded" @((New-Result "Ventas" "Succeeded" 298 39), (New-Result "Compras" "Succeeded" 90 17))
$history += New-Rec $idVentas "Ventas nocturno" 3 22 "Full"           "Succeeded" @((New-Result "Ventas" "Succeeded" 2150 212), (New-Result "Compras" "Succeeded" 640 96))
$warn = "No se pudo copiar a 'NAS oficina': el recurso de red no está disponible."
$history += New-Rec $idConta "Contabilidad" 1 23 "Full" "Warning"   @((New-Result "Contabilidad" "Warning" 880 94 $null @($warn)), (New-Result "Nomina" "Warning" 120 21 $null @($warn)))
$history += New-Rec $idConta "Contabilidad" 2 23 "Full" "Succeeded" @((New-Result "Contabilidad" "Succeeded" 874 92), (New-Result "Nomina" "Succeeded" 118 20))
$history += New-Rec $idConta "Contabilidad" 3 23 "Full" "Succeeded" @((New-Result "Contabilidad" "Succeeded" 870 91), (New-Result "Nomina" "Succeeded" 117 20))
$err = "BACKUP LOG no es válido: la base 'Inventario' usa el modelo de recuperación SIMPLE."
$history += New-Rec $idLogs "Inventario (logs)" 0 8 "TransactionLog" "Failed" @(New-Result "Inventario" "Failed" 0 1 $err)
$history += New-Rec $idLogs "Inventario (logs)" 0 7 "TransactionLog" "Failed" @(New-Result "Inventario" "Failed" 0 1 $err)

# ---------------------------------------------------------------- Utilidades de captura
function Save-WindowShot([IntPtr]$hwnd, [string]$name) {
    $rect = New-Object NativeWin+RECT
    [void][NativeWin]::GetWindowRect($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][NativeWin]::PrintWindow($hwnd, $hdc, 2)   # 2 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    $path = Join-Path $outDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "OK $name ($w x $h)"
}

function Find-ByName($root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "No se encontró el control '$name'."
}

function Invoke-Button($root, [string]$name) {
    $el = Find-ByName $root $name
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Select-Tab($root, [string]$name) {
    $el = Find-ByName $root $name
    $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 700
}

function Scroll-ToBottom($root) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty), $true
    foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        $sp = $el.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($sp.Current.VerticallyScrollable) {
            $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
            Start-Sleep -Milliseconds 600
            return
        }
    }
}

function Wait-Dialog([IntPtr]$mainHandle, [string]$pattern) {
    # WPF expone los diálogos modales como ventanas descendientes de su propietaria.
    $main = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window)
    $deadline = (Get-Date).AddSeconds(20)
    do {
        foreach ($w in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($w.Current.Name -match $pattern) { return $w }
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "No apareció la ventana '$pattern'."
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
function Invoke-Scenario($file, $jobs, $action, [switch]$GoogleClient) {
    if ($Only.Count -gt 0 -and $Only -notcontains $file) { return }
    $dir = Join-Path $env:TEMP "sqlbackupn-shot-$file"
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $dir | Out-Null
    if ($jobs.Count -gt 0) {
        [IO.File]::WriteAllText((Join-Path $dir "jobs.json"), (ConvertTo-Json -InputObject @($jobs) -Depth 8), $utf8)
        $lines = $history | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 8 -Compress }
        [IO.File]::WriteAllLines((Join-Path $dir "history.jsonl"), $lines, $utf8)
    }

    if ($GoogleClient) {
        # Credenciales de Google de demostración, cifradas igual que la aplicación (DPAPI, ámbito equipo).
        Add-Type -AssemblyName System.Security
        $entropy = [Text.Encoding]::UTF8.GetBytes("SqlBackupN.v1")
        $secret = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes("secreto-de-demostracion"), $entropy, "LocalMachine")
        $client = [ordered]@{ clientId = "123456789012-demo.apps.googleusercontent.com"; protectedClientSecret = [Convert]::ToBase64String($secret) }
        [IO.File]::WriteAllText((Join-Path $dir "google-client.json"), (ConvertTo-Json -InputObject $client), $utf8)
    }

    $env:SQLBACKUPN_DATA = $dir
    $proc = Start-Process $Exe -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(25)
        do { Start-Sleep -Milliseconds 300; $proc.Refresh() } while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
        if ($proc.MainWindowHandle -eq 0) { throw "La ventana no apareció ($file)." }
        Start-Sleep -Seconds 3   # deja cargar jobs e historial
        & $action $proc
    }
    finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- Ventana principal
$main = {
    param($name)
    $n = $name
    { param($proc) Save-WindowShot $proc.MainWindowHandle $n }.GetNewClosure()
}

Invoke-Scenario "01" @($jobVentas, $jobConta, $jobLogs) (& $main "01-panel-principal")
Invoke-Scenario "02" @($jobConta, $jobVentas, $jobLogs) (& $main "02-job-con-advertencia")
Invoke-Scenario "03" @($jobLogs, $jobVentas, $jobConta) (& $main "03-job-con-fallo")
Invoke-Scenario "04" @() (& $main "04-bienvenida")

# ---------------------------------------------------------------- Editor de jobs
# Abre el editor con el botón indicado, fotografía las pestañas pedidas y lo cierra con Cancelar.
function New-EditorAction($button, $dialogPattern, $shots) {
    $b = $button; $p = $dialogPattern; $s = $shots
    {
        param($proc)
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
        Invoke-Button $root $b
        $dialog = Wait-Dialog $proc.MainWindowHandle $p
        Start-Sleep -Seconds 2
        $dialog = Wait-Dialog $proc.MainWindowHandle $p   # se vuelve a buscar: evita tomar la ventana a medio abrir
        foreach ($shot in $s) {
            if ($shot.tab) { Select-Tab $dialog $shot.tab }
            if ($shot.scroll) { Scroll-ToBottom $dialog }
            Save-WindowShot ([IntPtr]$dialog.Current.NativeWindowHandle) $shot.name
        }
        Invoke-Button $dialog "Cancelar"
    }.GetNewClosure()
}

Invoke-Scenario "05" @($jobVentas, $jobConta, $jobLogs) (New-EditorAction "Editar" "^Editar job" @(
    @{ tab = "General y servidor";   name = "05-editor-general" },
    @{ tab = "Programación";         name = "06-editor-programacion" },
    @{ tab = "Destinos";             name = "07-editor-destinos" },
    @{ tab = "Opciones de backup";   name = "08-editor-opciones" },
    @{ tab = "Retención y avisos";   name = "09-editor-retencion-avisos" }))

Invoke-Scenario "06" @($jobRota, $jobVentas) (New-EditorAction "Editar" "^Editar job" @(
    @{ tab = "Programación";         name = "10-editor-validacion-programacion" },
    @{ tab = "Destinos";             name = "11-editor-validacion-destinos" }))

Invoke-Scenario "07" @($jobVentas) (New-EditorAction "Nuevo job" "^Nuevo job" @(
    @{ tab = "General y servidor";   name = "12-editor-nuevo-job" }))

Invoke-Scenario "08" @($jobNube) (New-EditorAction "Editar" "^Editar job" @(
    @{ tab = "Destinos"; scroll = $true; name = "13-editor-google-drive" })) -GoogleClient

Invoke-Scenario "09" @($jobNubeSin) (New-EditorAction "Editar" "^Editar job" @(
    @{ tab = "Destinos"; name = "15-editor-google-credenciales" },
    @{ tab = "Destinos"; scroll = $true; name = "14-editor-google-drive-sin-conectar" }))

# Los enlaces de los .md llevan la huella de cada imagen para que no se muestre una versión antigua en caché.
Update-ImageLinks
