# Шаги проверки Prostor на настоящей Windows (вызывается из .github/workflows/ci.yml).
param([Parameter(Mandatory)][string]$Step, [string]$Mode = 'quick')
$ErrorActionPreference = 'Stop'
$App = "$env:ProgramFiles\Prostor\Prostor.exe"
New-Item -ItemType Directory -Force reports | Out-Null

# Runs Prostor with output captured to reports\<name>.out.txt and printed into the CI log.
function Run([string]$Name, [string[]]$ArgList, [int]$TimeoutSec = 1800) {
    $out = "$PWD\reports\$Name.out.txt"; $err = "$PWD\reports\$Name.err.txt"
    $p = Start-Process -FilePath $App -ArgumentList $ArgList -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $p.Handle  # keeps the handle so ExitCode is available later
    $done = $p.WaitForExit($TimeoutSec * 1000)
    Get-Content $out, $err -ErrorAction SilentlyContinue | Write-Host
    if (-not $done) { $p.Kill(); throw "Prostor ${ArgList}: не уложился в $TimeoutSec с" }
    $p.WaitForExit()
    return $p.ExitCode
}
function LogTail { Get-Content "$env:LOCALAPPDATA\Prostor\prostor.log" -Tail 40 -ErrorAction SilentlyContinue | Write-Host }
function ShowJson($path) { if (Test-Path $path) { Get-Content $path -Raw | Write-Host } }

switch ($Step) {
    'install' {
        $setup = Get-ChildItem dist\Prostor-Setup-*.exe | Select-Object -First 1
        $p = Start-Process $setup.FullName -ArgumentList '/S' -PassThru -Wait
        if ($p.ExitCode -ne 0) { throw "установщик вернул $($p.ExitCode)" }
        if (-not (Test-Path $App)) { throw "Prostor.exe не установлен" }
        Get-Item $App | Format-List Name, Length, VersionInfo
    }
    'webview2' {
        $code = Run 'webview2' @('--webview2-probe', '--json', "$PWD\reports\webview2.json") 120
        ShowJson reports\webview2.json
        LogTail
        if ($code -ne 0) { Write-Warning "WebView2 не открылся: программа будет использовать окно Edge" }
    }
    'bait' {
        $code = Run 'bait-sandbox' @('--bait-test', '--no-console', '--json', "$PWD\reports\bait-sandbox.json") 900
        ShowJson reports\bait-sandbox.json
        if ($code -ne 0) { throw "тест приманок в песочнице не пройден" }
    }
    'bait-live' {
        $env:CI = 'true'; $env:PROSTOR_LIVE_BAIT = '1'
        $code = Run 'bait-live' @('--bait-test', '--live', '--no-console', '--json', "$PWD\reports\bait-live.json") 1800
        ShowJson reports\bait-live.json
        if ($code -ne 0) { throw "тест приманок на живой системе не пройден" }
    }
    'helper' {
        $env:PROSTOR_FORCE_HELPER = '1'; $env:PROSTOR_NOBROWSER = '1'
        $out = "$PWD\reports\helper-out.txt"
        $p = Start-Process -FilePath $App -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$PWD\reports\helper-err.txt"
        $url = $null
        for ($i = 0; $i -lt 100 -and -not $url; $i++) {
            Start-Sleep -Milliseconds 200
            if (Test-Path $out) { $m = Select-String -Path $out -Pattern 'http://\S+' | Select-Object -First 1; if ($m) { $url = $m.Matches[0].Value } }
        }
        if (-not $url) { $p.Kill(); throw "окно не сообщило адрес" }
        $base = $url -replace '/\?t=.*$', ''; $tok = $url -replace '^.*[?&]t=', ''
        $info = Invoke-RestMethod -Uri "$base/api/info" -Headers @{ 'X-Token' = $tok }
        Write-Host "admin=$($info.admin) helper=$($info.helper)"
        Invoke-RestMethod -Method Post -Uri "$base/api/clean/scan" -Headers @{ 'X-Token' = $tok } -ContentType 'application/json' -Body '{"IDs":["win_temp","wu_cache"]}' | Out-Null
        Start-Sleep 3
        $job = Invoke-RestMethod -Uri "$base/api/job?kind=scan" -Headers @{ 'X-Token' = $tok }
        Write-Host "scan via helper: running=$($job.running) bytes=$($job.bytes)"
        $p.Kill()
        $left = $null
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep 1
            $left = Get-CimInstance Win32_Process -Filter "Name='Prostor.exe'" | Where-Object { $_.CommandLine -like '*--helper*' }
            if (-not $left) { break }
        }
        Write-Host "помощник завершился через $i с"
        LogTail
        if (-not ($info.admin -and $info.helper)) { throw "помощник не подключился" }
        if ($left) { throw "помощник не завершился вместе с окном" }
    }
    'selftest' {
        $argList = @('--selftest', '--no-console', '--no-open')
        if ($Mode -ne 'full') { $argList += '--quick' }
        $code = Run 'selftest' $argList 2400
        Copy-Item "$env:LOCALAPPDATA\Prostor\selftest\report-*" reports\ -ErrorAction SilentlyContinue
        LogTail
        if ($code -ne 0) { throw "самопроверка нашла ошибки, отчёт в артефактах" }
    }
    'uninstall' {
        $code = Run 'uninstall' @('--uninstall', '--quiet') 300
        for ($i = 0; $i -lt 30 -and (Test-Path $App); $i++) { Start-Sleep 1 }
        if (Test-Path $App) { throw "программа не удалилась" }
        Write-Host "удалено, код $code"
    }
    default { throw "неизвестный шаг $Step" }
}
