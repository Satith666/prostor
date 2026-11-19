# Проверка удаления программ (условие этапа 4): популярные программы ставятся
# через Chocolatey, один раз запускаются (чтобы создать свои настройки), затем
# Prostor удаляет их тихо вместе с остатками. Снимок системы до установки и
# после удаления должен совпасть: ничего нового не осталось и ничего из того,
# что было раньше, не пропало.
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force reports | Out-Null
$setup = Get-ChildItem dist\Prostor-Setup-*.exe | Select-Object -First 1
$p = Start-Process $setup.FullName -ArgumentList '/S' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw "установщик Prostor вернул $($p.ExitCode)" }
$App = "$env:ProgramFiles\Prostor\Prostor.exe"

$packages = @('7zip', 'notepadplusplus', 'vlc', 'audacity', 'keepass', 'putty', 'winscp', 'sumatrapdf', 'irfanview',
    'qbittorrent', 'greenshot', 'everything', 'windirstat', 'treesizefree', 'crystaldiskinfo', 'cpu-z', 'firefox',
    'winrar', 'obs-studio', 'telegram', 'foxitreader', 'handbrake')

$uninstKeys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')

function Entries {
    $out = @{}
    foreach ($k in $uninstKeys) {
        Get-ChildItem $k -ErrorAction SilentlyContinue | ForEach-Object {
            $n = $_.GetValue('DisplayName')
            if ($n -and -not $_.GetValue('SystemComponent') -and -not $_.GetValue('ParentKeyName')) { $out[$_.Name] = $n }
        }
    }
    return $out
}

function Snapshot {
    $items = New-Object 'System.Collections.Generic.HashSet[string]'
    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:APPDATA, $env:LOCALAPPDATA,
        "$env:LOCALAPPDATA\Programs", "$env:USERPROFILE\AppData\LocalLow", "$env:CommonProgramFiles", ${env:CommonProgramFiles(x86)},
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs",
        "$env:PUBLIC\Desktop", [Environment]::GetFolderPath('Desktop'))
    foreach ($r in $roots) {
        if ($r -and (Test-Path -LiteralPath $r)) {
            Get-ChildItem -LiteralPath $r -Force -ErrorAction SilentlyContinue | ForEach-Object { [void]$items.Add('файл|' + $_.FullName.ToLower()) }
        }
    }
    foreach ($k in @('HKCU:\Software', 'HKLM:\SOFTWARE', 'HKLM:\SOFTWARE\WOW6432Node')) {
        Get-ChildItem $k -ErrorAction SilentlyContinue | ForEach-Object { [void]$items.Add('реестр|' + $_.Name.ToLower()) }
    }
    foreach ($k in $uninstKeys) {
        Get-ChildItem $k -ErrorAction SilentlyContinue | ForEach-Object { [void]$items.Add('установлено|' + $_.Name.ToLower()) }
    }
    Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services' | ForEach-Object { [void]$items.Add('служба|' + $_.PSChildName.ToLower()) }
    Get-ChildItem "$env:SystemRoot\System32\Tasks" -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { [void]$items.Add('задача|' + $_.FullName.ToLower()) }
    return $items
}

# Не относится к программам: служебное Chocolatey, сам Prostor, временные файлы,
# фоновая работа Windows и среды GitHub (по последней части пути).
$noisePath = 'chocolatey|prostor|\\tasks\\microsoft\\'
$noiseLeaf = '^(temp|tmp|microsoft|packages|crashdumps|d3dscache|nvidia corporation|package cache|nuget|pip|npm|powershell|usoshared|softwaredistribution|installer|\.net|github|actions|.*\.tmp|.*\.log|.*\.etl)$'
function IsNoise([string]$item) {
    if ($item -match $noisePath) { return $true }
    $leaf = ($item -split '\\')[-1]
    return $leaf -match $noiseLeaf
}

$before = Snapshot
$entriesBefore = Entries
$testStart = Get-Date

# --- установка
$installed = @()
foreach ($pkg in $packages) {
    Write-Host "== установка $pkg"
    # не -Wait: он ждёт и программы, которые установщик запустил после себя (Greenshot)
    $p = Start-Process choco -ArgumentList @('install', $pkg, '-y', '--no-progress', '--limit-output', '--ignore-checksums') -PassThru -NoNewWindow
    $null = $p.Handle
    if (-not $p.WaitForExit(10 * 60 * 1000)) { $p.Kill(); Write-Warning "$pkg не установился за 10 минут"; continue }
    if ($p.ExitCode -eq 0) { $installed += $pkg } else { Write-Warning "$pkg не установился ($($p.ExitCode))" }
}
# программы, запущенные установщиками, закрываются: иначе их файлы заняты
$appDirs = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, "$env:LOCALAPPDATA\Programs", $env:APPDATA, $env:LOCALAPPDATA) | Where-Object { $_ }
Get-Process | Where-Object { $_.Path -and $_.Path -notmatch 'chocolatey|prostor' } |
    Where-Object { $path = $_.Path; @($appDirs | Where-Object { $path.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 } |
    Where-Object { $_.StartTime -gt $testStart } |
    ForEach-Object { Write-Host "   закрыта запущенная установщиком программа: $($_.Path)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
$entriesAfter = Entries
$new = @{}
foreach ($k in $entriesAfter.Keys) { if (-not $entriesBefore.ContainsKey($k)) { $new[$k] = $entriesAfter[$k] } }
Write-Host "Установлено пакетов: $($installed.Count) из $($packages.Count); новых программ в списке: $($new.Count)"
$new.GetEnumerator() | Sort-Object Value | ForEach-Object { Write-Host "   $($_.Value)" }

# --- один запуск каждой программы: появятся её настройки в AppData
$shell = New-Object -ComObject WScript.Shell
$startMenu = @("$env:ProgramData\Microsoft\Windows\Start Menu\Programs", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs")
$links = Get-ChildItem $startMenu -Recurse -Filter *.lnk -ErrorAction SilentlyContinue |
    Where-Object { -not $before.Contains('файл|' + $_.FullName.ToLower()) -and $_.Name -notmatch 'uninst|удал|readme|help|website|license|manual' }
$started = 0
foreach ($l in $links) {
    $t = $shell.CreateShortcut($l.FullName).TargetPath
    if (-not $t -or -not (Test-Path -LiteralPath $t) -or $t -notmatch '\.exe$') { continue }
    try {
        $proc = Start-Process -FilePath $t -PassThru -WindowStyle Minimized
        Start-Sleep -Seconds 6
        $dir = Split-Path $t
        Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue
        if (-not $proc.HasExited) { $proc.Kill() }
        $started++
    } catch { Write-Warning "не запустился $t" }
}
Write-Host "Запущено программ: $started"
Start-Sleep -Seconds 5

# --- удаление через Prostor
$removed = 0; $results = @()
foreach ($e in ($new.GetEnumerator() | Sort-Object Value)) {
    $name = $e.Value
    $safe = ($name -replace '[^\w]+', '_')
    $json = "$PWD\reports\uninstall-$safe.json"
    $p = Start-Process -FilePath $App -ArgumentList @('--remove-program', "`"$name`"", '--json', "`"$json`"") -PassThru -NoNewWindow
    $null = $p.Handle
    if (-not $p.WaitForExit(10 * 60 * 1000)) { $p.Kill(); Write-Warning "${name}: удаление не уложилось в 10 минут"; continue }
    $r = if (Test-Path $json) { Get-Content $json -Raw | ConvertFrom-Json } else { $null }
    $ok = $r -and $r.report -and $r.report.removed
    if ($ok) { $removed++ }
    $left = if ($r) { @($r.remaining).Count } else { -1 }
    $quiet = if ($r -and $r.report) { $r.report.quiet } else { $false }
    $cleaned = if ($r -and $r.cleaned) { $r.cleaned.removed } else { 0 }
    Write-Host ("{0,-45} удалена: {1,-5} тихо: {2,-5} остатков убрано: {3,3}, осталось найденных: {4}  {5}" -f $name, $ok, $quiet, $cleaned, $left, $(if ($r) { $r.error } else { 'нет отчёта' }))
    $results += [pscustomobject]@{ name = $name; removed = [bool]$ok; quiet = $quiet; cleaned = $cleaned; error = $(if ($r) { $r.error } else { 'нет отчёта' }) }
}
# Chocolatey помнит пакеты, но программ уже нет: только его собственные записи
Start-Sleep -Seconds 5

$after = Snapshot
$leftover = @($after | Where-Object { -not $before.Contains($_) -and -not (IsNoise $_) } | Sort-Object)
$lost = @($before | Where-Object { -not $after.Contains($_) -and -not (IsNoise $_) } | Sort-Object)
$results | ConvertTo-Json | Set-Content reports\uninstall-summary.json
$leftover | Set-Content reports\uninstall-leftovers.txt
$lost | Set-Content reports\uninstall-lost.txt

Write-Host ""
Write-Host "Программ удалено: $removed из $($new.Count)"
Write-Host "Осталось после удаления (нет в снимке до установки): $($leftover.Count)"
$leftover | ForEach-Object { Write-Host "   + $_" }
Write-Host "Пропало из того, что было до установки: $($lost.Count)"
$lost | ForEach-Object { Write-Host "   - $_" }
if ($lost.Count -gt 0) { throw "Prostor удалил то, что было до установки программ" }
if ($removed -lt 20) { throw "удалено меньше 20 программ" }
if ($leftover.Count -gt 0) { throw "после удаления остались следы программ" }
Write-Host "Все программы удалены без остатков"
