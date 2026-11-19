@echo off
chcp 65001 >nul
title Проверка Prostor
rem Полная самопроверка Prostor. Ничего, кроме собственных тестовых файлов, не удаляет.
rem Быстрый режим без анализа всего диска: "Проверка Prostor.cmd" --quick
set "EXE=%~dp0Prostor.exe"
if not exist "%EXE%" set "EXE=%ProgramFiles%\Prostor\Prostor.exe"
if not exist "%EXE%" (
  echo Не найден Prostor.exe. Установите Prostor или положите этот файл рядом с Prostor.exe.
  pause
  exit /b 2
)
echo Запуск проверки: "%EXE%"
echo Для самой полной проверки запустите этот файл от имени администратора.
echo.
start "" /wait "%EXE%" --selftest %*
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (echo Ошибок не найдено.) else (echo Найдены ошибки. Подробности в отчёте, он открылся в браузере.)
pause
exit /b %RC%
