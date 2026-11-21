<# : batch launcher - do not edit this block
@echo off
setlocal
set "WP_SELF=%~f0"
set "WP_MODE=%~1"
set "WP_BACKUP=%~2"
set "WP_RESULT=%~3"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& ([ScriptBlock]::Create([IO.File]::ReadAllText($env:WP_SELF, [Text.Encoding]::UTF8)))"
set "WP_EXIT=%ERRORLEVEL%"
if /i "%WP_MODE%"=="machine" exit /b %WP_EXIT%
if not defined WP_YES pause
exit /b %WP_EXIT%
#>

# =====================================================================================================
# WinPulse — полный сброс: возвращает ВСЁ, что WinPulse менял в Windows, к исходным значениям.
# Источник — резервные копии самого WinPulse: %LOCALAPPDATA%\WinPulse\backups\*.json
# (туда исходное значение записывается ДО любого изменения). Ничего сверх этого скрипт не меняет.
#
# Запуск: двойной щелчок по файлу. Права администратора будут запрошены только для системных
# параметров (HKLM, схема питания, задания Планировщика). Отчёт сохраняется на Рабочий стол.
# =====================================================================================================

$ErrorActionPreference = 'Continue'
$machineMode = ($env:WP_MODE -eq 'machine')
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$backupDir = if ($env:WP_BACKUP) { $env:WP_BACKUP } else { Join-Path $env:LOCALAPPDATA 'WinPulse\backups' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Collections.Generic.List[string]
$wellKnownSchemes = @(
    '381b4222-f694-41f0-9685-ff5bb260df2e', # Сбалансированная
    '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c', # Высокая производительность
    'a1841308-3541-4fab-bc81-f71556f20b4a', # Экономия энергии
    'e9a42b02-d5df-448d-aa00-03f14749eb61'  # Максимальная производительность
)

function Say([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    $log.Add($text)
}

function Add-Result($file, $entry, [bool]$ok, [string]$message) {
    $results.Add([pscustomobject]@{
        File     = $file
        TargetId = [string]$entry.TargetId
        Kind     = [string]$entry.Kind
        Title    = [string]$entry.Title
        Ok       = $ok
        Message  = $message
    })
}

function Is-MachineEntry($e) {
    switch ([string]$e.Kind) {
        'registry' { return ([string]$e.Hive -eq 'LocalMachine' -or [string]$e.Hive -eq '1') }
        'power'    { return $true }
        'task'     { return $true }
        default    { return $false }
    }
}

function Describe($e) {
    switch ([string]$e.Kind) {
        'registry' {
            $root = if (Is-MachineEntry $e) { 'HKLM' } else { 'HKCU' }
            $name = if ([string]::IsNullOrEmpty([string]$e.ValueName)) { '(по умолчанию)' } else { [string]$e.ValueName }
            $sub = if ($e.SubKey) { " → $($e.SubKey)" } else { '' }
            $what = if ($e.Existed) { "вернуть «$($e.Data)»" } else { 'удалить (до WinPulse параметра не было)' }
            return "$root\$($e.Key) → $name${sub}: $what"
        }
        'power'   { return "схема питания: вернуть «$($e.SchemeName)» ($($e.SchemeGuid))" }
        'display' { return "монитор $($e.DeviceName): вернуть $($e.Width)×$($e.Height), $($e.Frequency) Гц" }
        'detach'  { return "монитор $($e.DeviceName): снова включить ($($e.Width)×$($e.Height), $($e.Frequency) Гц)" }
        'task'    { return "задание Планировщика $($e.TaskPath): " + $(if ($e.Data -eq '1') { 'включить' } else { 'отключить' }) }
        'nvidia'  {
            $app = if ($e.NvAppName) { $e.NvAppName } elseif ($e.NvApp) { $e.NvApp } else { 'глобальные параметры' }
            return "драйвер NVIDIA ($app): настройка 0x{0:X8}" -f [uint32]$e.NvSettingId
        }
        default   { return "$($e.Kind): $($e.TargetId)" }
    }
}

# ----------------------------------------------------------------------------------------------------- реестр

function Open-Root([bool]$machine) {
    $hive = if ($machine) { [Microsoft.Win32.RegistryHive]::LocalMachine } else { [Microsoft.Win32.RegistryHive]::CurrentUser }
    return [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
}

function Convert-RegData([string]$kind, [string]$data) {
    if ($null -eq $data) { $data = '' }
    switch ($kind) {
        'DWord'       { return [int]::Parse($data, [Globalization.CultureInfo]::InvariantCulture) }
        'QWord'       { return [long]::Parse($data, [Globalization.CultureInfo]::InvariantCulture) }
        'MultiString' { return , ([string[]]($data -split "`n")) }
        'Binary'      { return , ([Convert]::FromBase64String($data)) }
        default       { return $data }
    }
}

# Строки вида «A=1;B=2;» (так Windows хранит настройки DirectX): меняем только один ключ.
function Edit-KeyValue([string]$text, [string]$key, $value, [bool]$remove) {
    $pairs = New-Object System.Collections.Generic.List[object]
    if ($text) {
        foreach ($part in $text.Split(';')) {
            $t = $part.Trim()
            if ($t.Length -eq 0) { continue }
            $eq = $t.IndexOf('=')
            if ($eq -le 0) { $pairs.Add(@($t, '')) } else { $pairs.Add(@($t.Substring(0, $eq).Trim(), $t.Substring($eq + 1).Trim())) }
        }
    }
    $found = $false
    for ($i = $pairs.Count - 1; $i -ge 0; $i--) {
        if ($pairs[$i][0] -ieq $key) {
            if ($remove) { $pairs.RemoveAt($i) } else { $pairs[$i] = @($pairs[$i][0], [string]$value); $found = $true }
        }
    }
    if (-not $remove -and -not $found) { $pairs.Add(@($key, [string]$value)) }
    $sb = New-Object System.Text.StringBuilder
    foreach ($p in $pairs) {
        if ($p[1].Length -eq 0) { [void]$sb.Append("$($p[0]);") } else { [void]$sb.Append("$($p[0])=$($p[1]);") }
    }
    return $sb.ToString()
}

function Restore-Registry($e) {
    $root = Open-Root (Is-MachineEntry $e)
    $k = $null
    try {
        $name = if ($null -eq $e.ValueName) { '' } else { [string]$e.ValueName }
        if ($e.SubKey) {
            $k = $root.CreateSubKey([string]$e.Key)
            $current = $k.GetValue($name, $null)
            $updated = Edit-KeyValue ([string]$current) ([string]$e.SubKey) $e.Data (-not $e.Existed)
            if ($updated.Length -eq 0) {
                if ($null -ne $current) { $k.DeleteValue($name, $false) }
            } else {
                $k.SetValue($name, $updated, [Microsoft.Win32.RegistryValueKind]::String)
            }
        } elseif ($e.Existed) {
            $k = $root.CreateSubKey([string]$e.Key)
            $kindName = if ($e.ValueKind) { [string]$e.ValueKind } else { 'String' }
            $kind = [Microsoft.Win32.RegistryValueKind]$kindName
            $value = Convert-RegData $kindName ([string]$e.Data)
            $k.SetValue($name, $value, $kind)
        } else {
            $k = $root.OpenSubKey([string]$e.Key, $true)
            if ($k) { $k.DeleteValue($name, $false) }
        }
        return $null
    } catch {
        return $_.Exception.Message
    } finally {
        if ($k) { $k.Dispose() }
        $root.Dispose()
    }
}

# ----------------------------------------------------------------------------------------------------- питание

function Restore-Power($e) {
    $guid = ([string]$e.SchemeGuid).ToLowerInvariant()
    if (-not $guid) { return 'в резервной копии нет схемы' }
    $list = (& powercfg.exe /list 2>&1 | Out-String).ToLowerInvariant()
    if (-not $list.Contains($guid)) { return "исходной схемы питания больше нет ($guid) — выберите схему вручную в «Электропитании»" }
    & powercfg.exe /setactive $guid 2>&1 | Out-Null
    $active = (& powercfg.exe /getactivescheme 2>&1 | Out-String).ToLowerInvariant()
    if (-not $active.Contains($guid)) { return 'powercfg не смог сделать схему активной' }
    $created = ([string]$e.CreatedSchemeGuid).ToLowerInvariant()
    if ($created -and $created -ne $guid -and ($wellKnownSchemes -notcontains $created) -and $list.Contains($created)) {
        & powercfg.exe /delete $created 2>&1 | Out-Null
    }
    return $null
}

# ----------------------------------------------------------------------------------------------------- Планировщик

function Restore-Task($e) {
    $path = [string]$e.TaskPath
    if (-not $path) { return 'в резервной копии нет пути задания' }
    if ($path -like '\Microsoft\*') { return 'системные задания Windows не трогаем' }
    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $task = $svc.GetFolder('\').GetTask($path)
        $task.Enabled = ($e.Data -eq '1')
        return $null
    } catch {
        return $_.Exception.Message
    }
}

# ----------------------------------------------------------------------------------------------------- мониторы

$displayCode = @'
using System;
using System.Runtime.InteropServices;

public static class WpDisplay
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion; public ushort dmDriverVersion; public ushort dmSize; public ushort dmDriverExtra;
        public uint dmFields; public int dmPositionX; public int dmPositionY; public uint dmDisplayOrientation; public uint dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel; public uint dmPelsWidth; public uint dmPelsHeight; public uint dmDisplayFlags; public uint dmDisplayFrequency;
        public uint dmICMMethod; public uint dmICMIntent; public uint dmMediaType; public uint dmDitherType; public uint dmReserved1; public uint dmReserved2;
        public uint dmPanningWidth; public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    static extern int ChangeDisplaySettingsExApply(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint a, IntPtr b, uint c, IntPtr d, uint flags);

    const uint DM_POSITION = 0x20, DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2, CDS_NORESET = 0x10000000;

    static DEVMODE New()
    {
        DEVMODE m = new DEVMODE();
        m.dmDeviceName = "";
        m.dmFormName = "";
        m.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
        return m;
    }

    public static string Current(string device)
    {
        DEVMODE m = New();
        if (!EnumDisplaySettings(device, -1, ref m)) return null;
        return string.Format("{0}x{1}, {2} Hz, {3}x{4}", m.dmPelsWidth, m.dmPelsHeight, m.dmDisplayFrequency, m.dmPositionX, m.dmPositionY);
    }

    public static int SetMode(string device, int width, int height, int hz)
    {
        DEVMODE m = New();
        if (!EnumDisplaySettings(device, -1, ref m)) return -100;
        m.dmPelsWidth = (uint)width;
        m.dmPelsHeight = (uint)height;
        m.dmDisplayFrequency = (uint)hz;
        m.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int test = ChangeDisplaySettingsEx(device, ref m, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != 0) return test;
        return ChangeDisplaySettingsEx(device, ref m, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
    }

    public static int Reattach(string device, int x, int y, int width, int height, int hz, int bits)
    {
        DEVMODE m = New();
        m.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | (bits > 0 ? DM_BITSPERPEL : 0);
        m.dmPositionX = x;
        m.dmPositionY = y;
        m.dmPelsWidth = (uint)width;
        m.dmPelsHeight = (uint)height;
        m.dmDisplayFrequency = (uint)hz;
        m.dmBitsPerPel = (uint)Math.Max(0, bits);
        int r = ChangeDisplaySettingsEx(device, ref m, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r == 0) r = ChangeDisplaySettingsExApply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (r == 0) return 0;
        // Запасной путь: Windows сама возвращает последнюю конфигурацию «Расширить».
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x80 | 0x4) == 0 ? 0 : r;
    }
}
'@

function Ensure-Display {
    if ('WpDisplay' -as [type]) { return $true }
    try {
        Add-Type -TypeDefinition $displayCode -Language CSharp
    } catch {
        Say "Не удалось подготовить работу с мониторами: $($_.Exception.Message)" 'Yellow'
    }
    return [bool]('WpDisplay' -as [type])
}

function Display-Error([int]$code) {
    switch ($code) {
        -100 { 'монитор не найден (подключён к другому разъёму или выключен)' }
        1    { 'нужна перезагрузка' }
        -1   { 'драйвер отказал в смене режима' }
        -2   { 'режим не поддерживается' }
        -3   { 'не удалось записать в реестр' }
        default { "ошибка $code" }
    }
}

function Restore-Display($e) {
    if (-not (Ensure-Display)) { return 'не удалось вызвать функции Windows для мониторов' }
    $r = [WpDisplay]::SetMode([string]$e.DeviceName, [int]$e.Width, [int]$e.Height, [int]$e.Frequency)
    if ($r -eq 0) { return $null } else { return (Display-Error $r) }
}

function Restore-Detach($e) {
    if (-not (Ensure-Display)) { return 'не удалось вызвать функции Windows для мониторов' }
    $r = [WpDisplay]::Reattach([string]$e.DeviceName, [int]$e.PositionX, [int]$e.PositionY, [int]$e.Width, [int]$e.Height, [int]$e.Frequency, [int]$e.BitsPerPixel)
    if ($r -eq 0) { return $null } else { return (Display-Error $r) }
}

# ----------------------------------------------------------------------------------------------------- резервные копии

function Read-Backups {
    $list = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $backupDir)) { return , $list }
    foreach ($f in Get-ChildItem -LiteralPath $backupDir -Filter '*.json' -File) {
        if ($f.Name -like 'reset-result-*') { continue }
        try {
            $json = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8) | ConvertFrom-Json
            $entries = @($json.Entries)
            if ($entries.Count -gt 0) { $list.Add([pscustomobject]@{ File = $f; Entries = $entries }) }
        } catch {
            Say "Файл резервной копии не читается: $($f.Name) — $($_.Exception.Message)" 'Red'
        }
    }
    return , $list
}

$order = @{ 'detach' = 0; 'display' = 1; 'registry' = 2; 'task' = 3; 'nvidia' = 4; 'power' = 5 }

function Restore-Entries($backups, [bool]$machinePart) {
    foreach ($b in $backups) {
        $sorted = $b.Entries | Sort-Object { $v = $order[[string]$_.Kind]; if ($null -eq $v) { 9 } else { $v } }
        foreach ($e in $sorted) {
            if ((Is-MachineEntry $e) -ne $machinePart) { continue }
            $err = $null
            switch ([string]$e.Kind) {
                'registry' { $err = Restore-Registry $e }
                'power'    { $err = Restore-Power $e }
                'task'     { $err = Restore-Task $e }
                'display'  { $err = Restore-Display $e }
                'detach'   { $err = Restore-Detach $e }
                'nvidia'   { $err = 'MANUAL' }
                default    { $err = "неизвестный тип записи «$($e.Kind)»" }
            }
            if ($err -eq 'MANUAL') {
                Add-Result $b.File.Name $e $false 'вручную в Панели управления NVIDIA (см. ниже)'
                Say ("  [вручную] " + (Describe $e)) 'Yellow'
            } elseif ($null -eq $err) {
                Add-Result $b.File.Name $e $true ''
                Say ("  [готово]  " + (Describe $e)) 'Green'
            } else {
                Add-Result $b.File.Name $e $false $err
                Say ("  [ошибка]  " + (Describe $e) + " — $err") 'Red'
            }
        }
    }
}

function Stop-WinPulseTraces {
    # Сессии ETW WinPulse (замер задержек, PresentMon), если программа закрылась посреди замера.
    $names = & logman.exe query -ets 2>$null | ForEach-Object { ($_ -split '\s{2,}')[0].Trim() } | Where-Object { $_ -like 'WinPulse*' }
    foreach ($n in $names) {
        & logman.exe stop $n -ets 2>&1 | Out-Null
        Say "  [готово]  остановлена служебная сессия трассировки «$n»" 'Green'
    }
}

# ===================================================================================================== режим администратора

if ($machineMode) {
    Say 'WinPulse: возврат системных параметров (права администратора)' 'Cyan'
    Get-Process -Name WinPulse -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Stop-WinPulseTraces
    $backups = Read-Backups
    Restore-Entries $backups $true
    if ($env:WP_RESULT) {
        try { $results | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $env:WP_RESULT -Encoding UTF8 } catch { }
    }
    exit 0
}

# ===================================================================================================== основной режим

Clear-Host
Say '================================================================' 'Cyan'
Say ' WinPulse — возврат всех изменений Windows к исходным значениям' 'Cyan'
Say '================================================================' 'Cyan'
Say ''
Say "Резервные копии: $backupDir"

# 1. Закрываем WinPulse, чтобы он ничего не менял параллельно.
$running = @(Get-Process -Name WinPulse -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Say 'Закрываю WinPulse…'
    foreach ($p in $running) { try { [void]$p.CloseMainWindow() } catch { } }
    Start-Sleep -Seconds 4
    Get-Process -Name WinPulse -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$backups = Read-Backups
$all = @($backups | ForEach-Object { $_.Entries })
if ($all.Count -eq 0) {
    Say ''
    Say 'Резервных копий с изменениями нет: WinPulse ничего не менял в Windows через свои разделы' 'Yellow'
    Say '(или изменения уже возвращены кнопкой «Вернуть всё как было»).' 'Yellow'
} else {
    Say ''
    Say "Будет возвращено к исходному: $($all.Count)" 'White'
    foreach ($b in $backups) {
        Say "  $($b.File.Name):"
        foreach ($e in $b.Entries) { Say ('    • ' + (Describe $e)) }
    }
    Say ''
    $go = $true
    if (-not $env:WP_YES) {
        $answer = Read-Host 'Вернуть всё это? Введите Д (да) или Н (нет) и нажмите Enter'
        $go = ($answer -match '^\s*(д|да|y|yes|l)\s*$')
    }
    if (-not $go) {
        Say 'Отменено — ничего не изменено.' 'Yellow'
        exit 1
    }

    # 2. То, что относится к вашей учётной записи (HKCU, мониторы), — без прав администратора.
    Say ''
    Say 'Параметры вашей учётной записи и мониторы:' 'Cyan'
    Restore-Entries $backups $false

    # 3. Системные параметры — с правами администратора.
    $machineEntries = @($all | Where-Object { Is-MachineEntry $_ })
    Say ''
    Say 'Системные параметры (HKLM, электропитание, Планировщик):' 'Cyan'
    if ($machineEntries.Count -eq 0) {
        Say '  нечего возвращать'
    } elseif ($isAdmin) {
        Stop-WinPulseTraces
        Restore-Entries $backups $true
    } else {
        $resultFile = Join-Path $backupDir "reset-result-$stamp.json"
        Say '  Windows спросит разрешение администратора — нажмите «Да».' 'White'
        try {
            Start-Process -FilePath $env:WP_SELF -ArgumentList @('machine', "`"$backupDir`"", "`"$resultFile`"") -Verb RunAs -Wait -WindowStyle Hidden
            if (Test-Path -LiteralPath $resultFile) {
                $machineResults = @([IO.File]::ReadAllText($resultFile, [Text.Encoding]::UTF8) | ConvertFrom-Json)
                foreach ($r in $machineResults) {
                    $results.Add($r)
                    $line = if ($r.Ok) { "  [готово]  $($r.Title) ($($r.TargetId))" } else { "  [ошибка]  $($r.Title) ($($r.TargetId)) — $($r.Message)" }
                    Say $line $(if ($r.Ok) { 'Green' } else { 'Red' })
                }
                Remove-Item -LiteralPath $resultFile -ErrorAction SilentlyContinue
            } else {
                Say '  Помощник с правами администратора не вернул результат.' 'Red'
            }
        } catch {
            Say '  Права администратора не выданы — системные параметры не возвращены. Запустите файл ещё раз и нажмите «Да».' 'Red'
        }
    }

    # 4. Полностью возвращённые резервные копии убираем в архив (не удаляем), чтобы WinPulse не предлагал откат ещё раз.
    $archive = Join-Path $backupDir "restored-$stamp"
    foreach ($b in $backups) {
        $mine = @($results | Where-Object { $_.File -eq $b.File.Name })
        if ($mine.Count -eq $b.Entries.Count -and @($mine | Where-Object { -not $_.Ok }).Count -eq 0) {
            New-Item -ItemType Directory -Force -Path $archive | Out-Null
            Move-Item -LiteralPath $b.File.FullName -Destination $archive -Force
        }
    }
}

# 5. Итог
$ok = @($results | Where-Object { $_.Ok }).Count
$failed = @($results | Where-Object { -not $_.Ok -and $_.Kind -ne 'nvidia' })
$nvidia = @($all | Where-Object { [string]$_.Kind -eq 'nvidia' })
Say ''
Say '================================================================' 'Cyan'
Say " Возвращено: $ok   Не получилось: $($failed.Count)   Вручную: $($nvidia.Count)" 'White'
Say '================================================================' 'Cyan'
foreach ($f in $failed) { Say "  ✗ $($f.Title): $($f.Message)" 'Red' }

if ($nvidia.Count -gt 0) {
    Say ''
    Say 'Настройки драйвера NVIDIA скрипт вернуть не может — это делается в два щелчка:' 'Yellow'
    Say '  Панель управления NVIDIA → «Управление параметрами 3D» →'
    Say '  вкладка «Глобальные параметры» → кнопка «Восстановить»;'
    $apps = @($nvidia | Where-Object { $_.NvApp } | ForEach-Object { if ($_.NvAppName) { "$($_.NvAppName) ($($_.NvApp))" } else { $_.NvApp } } | Select-Object -Unique)
    if ($apps.Count -gt 0) {
        Say '  вкладка «Программные настройки» → выберите игру → кнопка «Восстановить» для:'
        foreach ($a in $apps) { Say "    • $a" }
    }
}

$needRestart = @($results | Where-Object { $_.Ok -and ($_.Kind -eq 'registry') -and ($_.TargetId -like 'reg:localmachine*') }).Count -gt 0
Say ''
if ($needRestart) {
    Say 'ПЕРЕЗАГРУЗИТЕ компьютер: часть системных параметров (планировщик видеокарты, MPO, изоляция ядра, файл подкачки) вступает в силу только после перезагрузки.' 'Yellow'
} else {
    Say 'Если что-то ещё выглядит не так — выйдите из учётной записи и войдите снова (настройки мыши и клавиатуры применяются при входе).'
}

# 6. Текущее состояние ключевых параметров — для разбора, если проблема останется.
Say ''
Say '--- Текущее состояние (для диагностики) ---'
function Show-Value([bool]$machine, [string]$key, [string]$name) {
    $root = Open-Root $machine
    try {
        $k = $root.OpenSubKey($key)
        $v = if ($k) { $k.GetValue($name, $null) } else { $null }
        if ($k) { $k.Dispose() }
        $shown = if ($null -eq $v) { '(нет)' } elseif ($v -is [array]) { ($v -join ' | ') } else { [string]$v }
        Say ("  " + $(if ($machine) { 'HKLM' } else { 'HKCU' }) + "\$key\$name = $shown")
    } catch {
        Say "  $key\$name = (не прочитать)"
    } finally {
        $root.Dispose()
    }
}
Show-Value $true  'SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'HwSchMode'
Show-Value $true  'SOFTWARE\Microsoft\Windows\Dwm' 'OverlayTestMode'
Show-Value $true  'SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' 'Enabled'
Show-Value $true  'SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management' 'PagingFiles'
Show-Value $true  'SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled'
Show-Value $false 'Software\Microsoft\DirectX\UserGpuPreferences' 'DirectXUserGlobalSettings'
Show-Value $false 'Control Panel\Mouse' 'MouseSpeed'
Show-Value $false 'System\GameConfigStore' 'GameDVR_Enabled'
Show-Value $false 'Software\Microsoft\GameBar' 'AutoGameModeEnabled'
Say ('  Схема питания: ' + ((& powercfg.exe /getactivescheme 2>&1 | Out-String).Trim()))
if (Ensure-Display) {
    for ($i = 1; $i -le 8; $i++) {
        $mode = [WpDisplay]::Current("\\.\DISPLAY$i")
        if ($mode) { Say "  \\.\DISPLAY$i — $mode" }
    }
}

try {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $report = Join-Path $desktop "WinPulse-сброс-$stamp.txt"
    [IO.File]::WriteAllLines($report, $log, (New-Object System.Text.UTF8Encoding($true)))
    Say ''
    Say "Отчёт сохранён: $report" 'Cyan'
} catch { }

if ($failed.Count -gt 0) { exit 2 } else { exit 0 }
