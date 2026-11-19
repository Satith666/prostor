@echo off
rem WinPulse build script.
rem This file is saved in CP866 with CRLF line endings on purpose: cmd.exe reads .cmd files byte by byte,
rem and "chcp 866" below makes the Russian text readable on any Windows.

rem If started by double-click, reopen in a window that will not close on its own (even after an error).
if /i "%~1"=="/inner" goto inner
echo %cmdcmdline% | find /i "%~nx0" >nul
if errorlevel 1 goto inner
cmd /k call "%~f0" /inner
exit /b

:inner
setlocal
set "OLDCP="
for /f "tokens=2 delims=:." %%c in ('chcp') do set "OLDCP=%%c"
chcp 866 >nul
cd /d "%~dp0"
set "ROOT=%CD%"
set "LOCALSDK=%CD%\.dotnet"
set "TRIED=0"

echo ==========================================================
echo   WinPulse - сборка программы
echo ==========================================================
echo.

if not exist "WinPulse.sln" goto notunpacked
if not exist "src\WinPulse.App\WinPulse.App.csproj" goto notunpacked

rem ---------- поиск .NET SDK 10 ----------
:findsdk
call :usesdk
if "%SDKSTATE%"=="ok" goto build
if "%TRIED%"=="1" goto installfailed

if "%SDKSTATE%"=="old" echo [!] Найден .NET SDK %SDKVER%, а нужен 10 или новее.
if "%SDKSTATE%"=="none" echo [!] Не найден .NET SDK 10. Без него программу не собрать.
echo.
echo     Могу скачать его сам с официального сайта Microsoft (dot.net):
echo     - SDK ляжет в папку .dotnet рядом с build.cmd;
echo     - права администратора не нужны, в систему ничего не ставится;
echo     - удалить потом просто: стереть папку .dotnet;
echo     - скачается около 300 МБ, это несколько минут.
echo.
choice /c YN /n /m "Скачать .NET SDK 10 автоматически? Y - да, N - нет: "
if errorlevel 2 goto manualsdk
set "TRIED=1"
call :installsdk
goto findsdk

rem ---------- сборка ----------
:build
echo Найден .NET SDK %SDKVER%
if exist "build-errors.txt" del /q "build-errors.txt"
if exist "test-log.txt" del /q "test-log.txt"
echo.
echo [1/2] Собираю WinPulse.exe.
echo       Первый раз это 3-5 минут, нужен интернет (скачиваются пакеты .NET).
echo.
dotnet publish "src\WinPulse.App\WinPulse.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist -nologo -v:minimal "-flp:LogFile=build-errors.txt;ErrorsOnly"
if errorlevel 1 goto failed
if not exist "dist\WinPulse.exe" goto failed

echo.
echo [2/2] Проверяю тестами. Программа уже собрана, это проверка для разработчика...
dotnet test "tests\WinPulse.Core.Tests\WinPulse.Core.Tests.csproj" -c Release -nologo > "test-log.txt" 2>&1
if errorlevel 1 (set "TESTS=FAILED") else (set "TESTS=OK")

echo.
echo ==========================================================
echo   Готово! Программа: %ROOT%\dist\WinPulse.exe
echo ==========================================================
if "%TESTS%"=="FAILED" echo [!] Часть тестов не прошла. Программа работает, но пришлите файл test-log.txt.
if "%TESTS%"=="OK" if exist "test-log.txt" del /q "test-log.txt"
echo.
echo Пробный запуск с выдуманными данными: dist\WinPulse.exe --demo
echo.
choice /c YN /n /m "Запустить WinPulse сейчас? Y - да, N - нет: "
if errorlevel 2 goto end
start "" "%ROOT%\dist\WinPulse.exe"
goto end

:notunpacked
echo [!] Рядом с build.cmd нет файлов проекта.
echo     Похоже, build.cmd запущен прямо из ZIP-архива.
echo     Сначала распакуйте архив: правый клик по WinPulse.zip - "Извлечь все...",
echo     затем запустите build.cmd из распакованной папки.
goto end

:manualsdk
echo.
echo     Установить вручную:
echo     1. Откройте https://dotnet.microsoft.com/download/dotnet/10.0
echo     2. В колонке SDK скачайте Windows x64 (Installer) и установите.
echo        Нужен именно SDK, а не ".NET Runtime" и не "Desktop Runtime".
echo     3. Закройте это окно и снова запустите build.cmd.
goto end

:installfailed
echo.
echo [!] Автоматически установить .NET SDK не получилось (текст ошибки выше).
echo     Частые причины: нет интернета, мешает антивирус или прокси.
goto manualsdk

:failed
echo.
echo [!] Сборка не удалась.
echo     Пришлите разработчику файл build-errors.txt из папки:
echo     %ROOT%
if exist "build-errors.txt" explorer /select,"%ROOT%\build-errors.txt"
goto end

rem ---------- подпрограммы ----------

rem Ищет SDK: сначала локальный в .dotnet, затем установленный в системе.
rem Результат: SDKSTATE = ok / old / none, SDKVER = версия.
:usesdk
set "SDKSTATE=none"
set "SDKVER="
set "MAJOR="
if not exist "%LOCALSDK%\dotnet.exe" goto usesdk_sys
set "PATH=%LOCALSDK%;%PATH%"
set "DOTNET_ROOT=%LOCALSDK%"
set "DOTNET_MULTILEVEL_LOOKUP=0"
:usesdk_sys
where dotnet >nul 2>nul
if errorlevel 1 if exist "%ProgramW6432%\dotnet\dotnet.exe" set "PATH=%ProgramW6432%\dotnet;%PATH%"
where dotnet >nul 2>nul
if errorlevel 1 if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"
where dotnet >nul 2>nul
if errorlevel 1 exit /b
set "VER="
for /f "delims=" %%v in ('dotnet --version 2^>nul') do if not defined VER set "VER=%%v"
if not defined VER exit /b
for /f "tokens=1 delims=." %%m in ("%VER%") do set "MAJOR=%%m"
if not defined MAJOR exit /b
for /f "delims=0123456789" %%x in ("%MAJOR%") do exit /b
set "SDKVER=%VER%"
set "SDKSTATE=old"
if %MAJOR% LSS 10 exit /b
set "SDKSTATE=ok"
exit /b

rem Скачивает официальный скрипт dotnet-install.ps1 и ставит последний SDK 10.0 в папку .dotnet.
:installsdk
echo.
echo Скачиваю .NET SDK 10 в папку %LOCALSDK% ...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; $s=Join-Path $env:TEMP 'dotnet-install.ps1'; Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $s; & $s -Channel 10.0 -InstallDir $env:LOCALSDK"
echo.
exit /b

:end
echo.
pause
if defined OLDCP chcp %OLDCP% >nul 2>nul
endlocal
exit /b
