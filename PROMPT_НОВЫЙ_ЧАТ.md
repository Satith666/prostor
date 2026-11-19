# Промпт для продолжения разработки WinPulse

Скопируй всё ниже в новый чат и прикрепи: архив `WinPulse.zip` и файл `docs/ТЗ_игры_в_окне.txt` (он также лежит в архиве).

---

Ты продолжаешь разработку программы **WinPulse** для Windows 10/11. Код пишешь ты сам, по этапам. Я запускаю сборку у себя и присылаю ошибки и впечатления. Отвечай по-русски, интерфейс программы — на русском (позже добавим английский).

## Где что лежит
- Архив проекта `WinPulse.zip` (прикреплён) — текущее состояние кода.
- План целиком: https://claude.ai/artifact/FZdnLWwbRcrLaPv1ramyhy
- Макеты интерфейса: https://claude.ai/artifact/7PJAeY44RpR6gXBhqqK7W4 — выбран **вариант B «Пульт»**: тёмная панель, боковое меню, живые плитки со спарклайнами, графики, таблицы «кто нагружает».
- ТЗ модуля «Игры в окне и без рамки» — `docs/ТЗ_игры_в_окне.txt` (прикреплён). Выполнить **полностью**.

## Что это за программа
Одна программа вместо пяти: точная диагностика «почему тормозит» (что грузит CPU, ОЗУ, диск, GPU, сеть — по приложениям и службам), безопасная оптимизация с откатом, игровой модуль с честным замером FPS, проверка драйверов, отдельная «Лаборатория» для профи.

Принципы (обязательны):
1. **Замер → действие → проверка «было/стало».** Ничего не обещаем — измеряем.
2. **Всё обратимо.** Перед изменением сохраняем исходное значение (или отметку «параметра не было»); откат одного действия и «Вернуть всё как было».
3. **Ничего без подтверждения.** Показываем, что именно меняется и зачем; после — итог (что сделано, что нет и почему, где нужна перезагрузка).
4. **Без мифов.** Не делаем: чистку реестра, «очистку ОЗУ», отключение Defender/обновлений/UAC/служб Windows/защит Spectre, bcdedit/HPET, Win32PrioritySeparation, сетевые «ускорители», приоритет «Реального времени», пугающие «найдено 2000 проблем».
5. **Драйверы только из официальных источников** (Windows Update API, NVIDIA, сайты производителей). Никаких сторонних баз.
6. **Не внедряемся в игры** (античиты): FPS — через ETW/PresentMon снаружи.
7. Права администратора — только для действий, которым они нужны (HKLM, powercfg и т. п.).
8. Код под GPL не копируем (иначе вся программа станет GPL). Можно: PresentMon (MIT), LibreHardwareMonitor (MPL-2.0), Bulk Crap Uninstaller (Apache-2.0), NVIDIA Profile Inspector (MIT), каталоги WinUtil / Win11Debloat / Sophia Script (MIT).

Интерфейс: три уровня — «Простой» (вердикты и кнопки), «Опытный» (по умолчанию: все модули, графики, таблицы), «Профи» (раздел «Лаборатория»: правила процессов, электропитание, DPC/прерывания, трассировка ETW, редакторы твиков и правил, CLI). Командная строка Ctrl+K, подсказка «?» у каждого термина, горячая клавиша «поймать тормоза».

## Стек и архитектура
- C# / **.NET 10 (LTS)**, **WPF** со своей тёмной темой (без сторонних UI-библиотек), MVVM без фреймворков (свои ObservableObject/RelayCommand).
- Графики — свои лёгкие контролы (FrameworkElement + OnRender + StreamGeometry). Цель: сама программа ≤ 80 МБ ОЗУ и ≤ 1% CPU.
- Проекты: `WinPulse.Core` (net10.0, без Windows — модели, анализ, форматирование, парсеры; тестируется), `WinPulse.Collectors` (net10.0-windows, сбор данных), `WinPulse.Optimizer` (net10.0-windows, действия/журнал/откат), `WinPulse.App` (WPF, AssemblyName `WinPulse`), позже `WinPulse.Service`, `WinPulse.Cli`. Тесты — xUnit.
- Сборка: `build.cmd` (CP866 + CRLF; запуск двойным щелчком, окно не закрывается; без SDK предлагает поставить его в `.dotnet` через dotnet-install.ps1; сначала `dotnet publish` в один self-contained exe win-x64, потом тесты в `test-log.txt`), GitHub Actions на windows-latest (`.github/workflows/build.yml`).
- Права: приложение запускается без админа (`asInvoker`). Для HKLM/powercfg — вспомогательный запуск самого себя с правами: `WinPulse.exe --elevated-ops <файл.json>` через `Verb = "runas"`; он выполняет операции, пишет результат в JSON и выходит. Отмена UAC — это ошибка 1223, её показываем как «отменено».
- Настройки и резервные копии: `%LOCALAPPDATA%\WinPulse\` (`settings.json`, `backups\*.json`, `logs\winpulse.log`).

## Текущее состояние
**Написано (в архиве), но ни разу не компилировалось** — в чате не было .NET SDK и NuGet:
- Этап 0: решение (5 проектов), `Directory.Build.props`, `build.cmd` (ошибки пишет в `build-errors.txt`), CI, README.
- Этап 1 (монитор ресурсов): `WinPulse.Core` — модели, `ProcessRateCalculator`, `AppGrouper` (по пути exe, svchost по службам, WebView2/conhost к владельцу), `AppHistory` (тренд памяти → утечки), `SystemHistory`, `MemoryMath`, `VerdictRules`, `MonitorEngine`, `DemoSystemSource` (`--demo`), `Edid`; `WinPulse.Collectors` — NtQuerySystemInformation, PDH (английские имена), DXGI, D3DKMT (HAGS), диски (IOCTL), сеть, службы, мониторы/EDID/смена частоты/отключение монитора; интерфейс «Пульт»: Обзор, Процессор, Память, Диски, Видеокарта, Сеть, режим «Профи», RU/EN оболочки.
- Этап 2 «Проблемы»: `Core/Diagnostics` (EventItem, DiagnosticInput, Finding, BugChecks, ProblemDetectors — 17 детекторов, хронология), `Collectors/Diagnostics` (EventLogReader на wevtapi, DeviceProblemsReader на cfgmgr32, DiskHealthReader через PowerShell Get-PhysicalDisk, SystemChecks, DiagnosticsCollector), страница `ProblemsView`. Только чтение, ничего не меняет.
- Этап 3: общий движок `FixModule` (Optimizer) и `FixModuleViewModel`/`FixModuleView` (App) для «Игр в окне» и «Оптимизации Windows» (`Optimizer/Optimization`, 8 пунктов); «Автозагрузка» (`Core/Startup`: CommandLine, StartupApproved, StartupKnowledge; `Optimizer/Startup/StartupManager`; `Optimizer/Platform/ScheduledTasks` на COM), ChangeKind.TaskEnabled. Этап 5 частично: «Драйверы» (`Core/Drivers`, `Collectors/Drivers/DriverInventory`). Описание: `docs/Оптимизация_и_автозагрузка.md`.
- Модуль «Игры в окне»: `WinPulse.Optimizer` — `Change`, `BackupStore`, `ChangeExecutor`, `ElevatedRunner/ElevatedHost` (`--elevated-ops`, белый список), `PowerSchemes`, 13 пунктов; страница с RU/EN. Описание: `docs/Игры_в_окне_как_сделано.md`.
Код лежит в GitHub: репозиторий Satith666/prostor, ветка `winpulse` (orphan, не связана с `main` — там другой проект, Prostor, его не трогать). GitHub Actions собирает exe и гоняет тесты на windows-latest; ошибки компилятора видны без авторизации через API `/repos/Satith666/prostor/check-runs/<job_id>/annotations`. Первая полная сборка прошла (коммит fa1c997). Дальше — проверка на живой системе: если программа падает, попроси %LOCALAPPDATA%\WinPulse\logs\winpulse.log.

## Порядок работы
1. Довести сборку до зелёной.
2. Проверить этап 1 у меня (критерии: собственная нагрузка ≤ 1% CPU; цифры совпадают с Диспетчером задач ±5%) и модуль «Игры в окне» на тестовой системе из ТЗ.
4. Дальше по плану: этап 4 игры и FPS (PresentMon); доделать этап 5 (сравнение с последними версиями драйверов); этап 3 оптимизация и откат (каталог 20 действий, пресеты), 4 игры и FPS (PresentMon, игровой режим, оверлей, A/B-сравнение), 5 драйверы и программы (WUA, NVIDIA API, winget, удаление), 6 глубокая диагностика (ETW: диск/сеть/DPC по процессам, самописец, хронология), 7 «Лаборатория», 8 релиз (служба, MSI/WiX, подпись, автообновление, RU/EN).
После каждого этапа давай архив и короткую инструкцию, что проверить.

Работая без компилятора: минимум NuGet-зависимостей, аккуратные P/Invoke, без экзотического синтаксиса, честно говори, что не проверено.

## Этап 1 — техническое решение (уже продумано)
**Поток данных.** `ISystemSource` (Core) → `RawSnapshot` раз в секунду; реализации `WindowsSystemSource` (Collectors) и `DemoSystemSource` (Core, детерминированные фейковые данные; запуск `WinPulse.exe --demo`). `MonitorEngine.Tick()` считает скорости, группирует процессы в приложения, ведёт историю и вердикты и отдаёт неизменяемый `MonitorFrame` (история — копиями массивов). В UI — цикл `PeriodicTimer(1 c)` в `Task.Run` → `Dispatcher.InvokeAsync(Apply)`; если UI ещё применяет прошлый кадр — кадр пропускаем.

**Процессы** — один вызов `NtQuerySystemInformation(SystemProcessInformation=5)` (буфер растёт при `STATUS_INFO_LENGTH_MISMATCH 0xC0000004`). Только x64. Смещения `SYSTEM_PROCESS_INFORMATION` (x64): NextEntryOffset 0, NumberOfThreads 4, WorkingSetPrivateSize 8, CreateTime 32, UserTime 40, KernelTime 48, ImageName.Length 56 (ushort, байты), ImageName.Buffer 64, UniqueProcessId 80, InheritedFromUniqueProcessId 88, HandleCount 96, SessionId 100, WorkingSetSize 144, PagefileUsage 184, ReadTransferCount 232, WriteTransferCount 240, OtherTransferCount 248. PID 0 → «Idle» (исключаем).
CPU% процесса = Δ(User+Kernel) / (Δt · 1e7 · логических ядер) · 100. Ключ процесса — (PID, CreateTime). Ввод-вывод процесса = весь ввод-вывод (диск, сеть, устройства) — так и подписывать.

**Группировка в приложения:** по полному пути exe (`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION=0x1000)` + `QueryFullProcessImageNameW`, кэш по (PID, CreateTime)); имя и издатель — `FileVersionInfo` (FileDescription, CompanyName), кэш по пути. `svchost.exe` → отдельная группа на процесс с именами служб (`EnumServicesStatusExW`, SC_ENUM_PROCESS_INFO, SERVICE_WIN32=0x30, SERVICE_ACTIVE=1; структура ENUM_SERVICE_STATUS_PROCESS = 2 указателя + 9 DWORD, шаг 56 байт; обновлять раз в 10 с). Вспомогательные процессы `msedgewebview2.exe`, `conhost.exe` приписываем ближайшему предку-приложению (проверять CreateTime родителя ≤ ребёнка; не приписывать к explorer/services/svchost/csrss и т. п.). System/Registry/Memory Compression/Secure System → группа «Система». База знаний известных процессов с русскими описаниями и флагом «системный, не трогать» (проверять, что путь в папке Windows). Иконки — `ExtractIconExW` → `Imaging.CreateBitmapSourceFromHIcon` (Freeze, кэш).

**Память:** `GlobalMemoryStatusEx`, `GetPhysicallyInstalledSystemMemory` (резерв оборудования = установлено − TotalPhys), `GetPerformanceInfo` (commit total/limit, ядро — в страницах). PDH (только `PdhAddEnglishCounterW` — на русской Windows имена счётчиков локализованы!): `\Memory\Standby Cache Reserve Bytes` + `Normal Priority` + `Core` (кэш), `\Memory\Modified Page List Bytes`, `\Memory\Free & Zero Page List Bytes`, `\Memory\Page Reads/sec` (жёсткие ошибки). Разбивка: программы = Σ private WS (кроме Memory Compression, System, Registry); сжатая = WS процесса «Memory Compression»; система и общая = (Total − Available − Modified) − программы − сжатая; плюс изменённая, кэш, свободно, зарезервировано. Вердикт «хватает / мало / не хватает» — по жёстким ошибкам/с, доступной памяти и commit, а не по проценту занятости. Утечка: наклон линейной регрессии памяти приложения за час (точка раз в 10 с) > 200 МБ/ч при R² > 0,8 и памяти > 300 МБ.

**CPU:** `\Processor Information(_Total)\% Processor Utility` (запасной вариант `\Processor(_Total)\% Processor Time`), `% Processor Performance` × `Processor Frequency` = реальная частота. Имя CPU — реестр `HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString`.

**PDH:** `PDH_FMT_DOUBLE|PDH_FMT_NOCAP100`; `PDH_FMT_COUNTERVALUE` = CStatus @0, double @8 (16 байт); элемент массива `PDH_FMT_COUNTERVALUE_ITEM_W` = указатель на имя @0, CStatus @8, double @16 (24 байта); `PDH_MORE_DATA 0x800007D2`; валидные CStatus — 0 и 1. Скоростям нужны два сбора.

**Диски:** `\PhysicalDisk(*)\% Idle Time` (активность = 100 − idle), `Avg. Disk sec/Transfer` (×1000 = мс), `Disk Read/Write Bytes/sec`, `Current Disk Queue Length`; экземпляры вида «0 C: D:». Тип и модель: `CreateFileW("\\.\PhysicalDriveN", 0, 3, …, OPEN_EXISTING)` + `IOCTL_STORAGE_QUERY_PROPERTY 0x2D1400`: StorageDeviceProperty (ProductIdOffset @16, BusType @28; NVMe = 17) и StorageDeviceSeekPenaltyProperty = 7 (байт @8 ≠ 0 → HDD). Свободное место — `DriveInfo`.

**GPU:** адаптеры через DXGI (`CreateDXGIFactory1`, IID IDXGIFactory1 `770aae78-f26f-4dba-a829-253c83d1b387`; vtable: EnumAdapters1 = 12, IDXGIAdapter1::GetDesc1 = 10, Release = 2; `DXGI_ADAPTER_DESC1`: Description 128 WCHAR @0, VendorId @256 (0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel), DedicatedVideoMemory @272, AdapterLuid Low @296 / High @300, Flags @304, бит 2 = программный адаптер — пропускать). PDH: `\GPU Engine(*)\Utilization Percentage` (экземпляр `pid_N_luid_0xHHHHHHHH_0xLLLLLLLL_phys_0_eng_E_engtype_3D`), `\GPU Adapter Memory(*)\Dedicated Usage`, `\GPU Process Memory(*)\Dedicated Usage`. Загрузка адаптера = максимум по движкам суммы процессов; процесса — максимум по движкам. LUID сравнивать без учёта регистра.

**Сеть:** `NetworkInterface` (Up, не Loopback/Tunnel) + `GetIPStatistics()` → разница BytesReceived/BytesSent. По процессам — только на этапе 6 (ETW).

**UI этапа 1:** боковое меню (Обзор, Процессор, Память, Диск, Видеокарта, Сеть; будущие разделы — с пометкой этапа), верхняя панель с уровнем Простой/Опытный/Профи и статусом («обновлено…, без прав администратора»; кнопка «перезапустить с правами»). Обзор: 5 плиток со спарклайнами, большой график выбранного ресурса, «Кто нагружает», список вердиктов. Страницы ресурсов: строка KPI, график, таблица приложений (раскрывается до процессов; в «Профи» видны PID, дескрипторы, потоки), окно усреднения «сейчас / 1 мин / 5 мин». Таблицы — `ItemsControl` + синхронизация `ObservableCollection` перемещениями (без пересоздания строк, чтобы не терять раскрытие и прокрутку). Иконки меню — свои Path-геометрии 24×24. Тёмный заголовок окна — `DwmSetWindowAttribute(20 / 19)`. Глобальный перехват ошибок в лог.

## Модуль «Игры в окне и без рамки» — решения по реализации
Выполнить все 13 пунктов ТЗ, все требования к безопасности, интерфейсу (тёмная тема, RU и EN) и запреты.
- Модель пункта: статус `в порядке / можно улучшить / недоступно` + причина, объяснение простыми словами, пометки «нужны права администратора» и «нужна перезагрузка», «рекомендуется» (для «Исправить всё рекомендуемое»), кнопки «Исправить» и «Вернуть как было». Сверху — «Игра в окне: X из Y настроено».
- Изменения описываются данными (`PlannedChange`: запись/удаление параметра реестра HKCU/HKLM, смена схемы питания, частоты монитора, ручная отметка) → экран подтверждения со списком изменений → исполнитель → итоговый отчёт.
- Резервная копия `%LOCALAPPDATA%\WinPulse\backups\windowed-games.json`: для каждого параметра — исходное значение или «не существовал»; сохраняется **только при первом изменении**, чтобы «Вернуть всё» вернуло исходное состояние. HKLM читаем без прав, а пишем через `--elevated-ops`.
- Версия Windows: `Environment.OSVersion.Version.Build` (≥ 22000 — Windows 11; пункт 1 требует ≥ 22557; пункт 2 — ≥ 18362). Производитель видеокарты — VendorId из DXGI.
- Мониторы: `EnumDisplayDevicesW` (флаги ATTACHED_TO_DESKTOP 0x1, PRIMARY 0x4) → `EnumDisplaySettingsW` (текущая частота при ENUM_CURRENT_SETTINGS = −1; максимальная — перебором режимов с тем же разрешением) → масштаб через `EnumDisplayMonitors` + `GetMonitorInfoW` + `GetDpiForMonitor`. Смена частоты второго монитора — `ChangeDisplaySettingsExW` с сохранением прежней частоты.
- Поддержка VRR по EDID: интерфейсное имя монитора (`EnumDisplayDevicesW(adapter, 0, …, EDD_GET_DEVICE_INTERFACE_NAME=1)`, вида `\\?\DISPLAY#GSM5B7F#5&…&UID4352#{…}`) → реестр `HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\GSM5B7F\5&…&UID4352\Device Parameters\EDID`. Признаки: AMD VSDB (OUI 00-00-1A) в CTA-расширении, блок Adaptive-Sync (тег 0x2B) в DisplayID, дескриптор пределов (0xFD, байты 5/6 = мин/макс Гц) с широким диапазоном при максимальном режиме монитора ≥ 75 Гц. Итог: «поддерживается» / «вероятно поддерживается» / «не обнаружено» — EDID может ошибаться, поэтому при «не обнаружено» не запрещать включение, а предупреждать. Парсер EDID положить в Core и покрыть тестами.
- Пункт 3 (G-SYNC в окне): NVAPI-идентификаторы настроек не проверены — пока пошаговая инструкция (NVIDIA / AMD Adrenalin / Intel) и ручная отметка «сделано». NVAPI — только после проверки по исходникам NVIDIA Profile Inspector (MIT).
- Пункт 10: «Проверить без второго монитора» — `DisplaySwitch.exe /internal`, возврат — `/extend`, с диалогом автоотката через 15 с.
- Пункт 12: схемы питания — Экономия `a1841308-3541-4fab-bc81-f71556f20b4a`, Сбалансированная `381b4222-f694-41f0-9685-ff5bb260df2e`, Высокая производительность `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` (если её нет — `powercfg /duplicatescheme`); прежнюю схему сохранять; если `powercfg /setactive` без прав не сработает — повторить через `--elevated-ops`.
- Пункт 9: искать процессы `wallpaper32.exe`/`wallpaper64.exe`, `Lively.exe`, `Discord.exe`, `NVIDIA Overlay.exe`, `GameBar.exe`, `RTSS.exe`, `MSIAfterburner.exe`, браузеры с заметной загрузкой GPU (вероятно видео). Только советы и, по желанию, закрытие с подтверждением (`CloseMainWindow`), ничего не закрывать молча.
- Выбор игры: путь к exe + «Обзор…» + список запущенных процессов с окнами; хранить в `settings.json`.
- Локализация: класс `Loc` с индексатором и `PropertyChanged("Item[]")` при смене языка; в XAML — `{Binding [ключ], Source={x:Static l:Loc.I}}`; ключи без точек.
- После изменений в UserGpuPreferences напоминать перезапустить игру.


## Обновление (19.11.2026, вечер)
- Дизайн «Кокпит» (вариант A из трёх макетов): Theme/Colors.xaml + Controls.xaml, шрифт заголовков Bahnschrift, RingGauge в Controls/Charts.cs.
- Новые разделы меню: ИГРЫ — «FPS-лаборатория» (GamesViewModel/GamesView), «Железо и FPS» (Optimizer/Hardware/HardwareModule, диагностика без изменений), «Игры в окне»; СИСТЕМА — «Проблемы», «Оптимизация» (разделы «Для игр» и «Windows без лишнего»), «Автозагрузка», «Драйверы».
- Core/Gaming: FrameStats, PresentMonCsv, GameDetector, GameSessionTracker, SessionAnalyzer. Collectors/Gaming/PresentMon.cs (поиск/скачивание/замер с чтением CSV на лету), Collectors/Hardware/HardwareReader.cs (WMI через PowerShell, PCIe через cfgmgr32, библиотеки Steam/Epic).
- Optimizer/Gaming/GameBoost.cs — игровой режим (настройки в AppSettings.AutoBoost/Boost), включается по событию начала сессии.
- CI: PresentMon кладётся рядом с exe; режим `WinPulse.exe --demo --lang ru --screenshots <папка>` снимает все страницы, снимки публикуются в ветку `winpulse-shots`; упавшие тесты выводятся аннотациями (`/repos/.../check-runs/<job>/annotations`).
- План и статус этапов: docs/План_прокачки.md. Следующее: датчики (LibreHardwareMonitor), драйвер NVIDIA по играм (NvAPIWrapper, через elevated-помощника), апскейлинг (DLSS Swapper/OptiScaler — скачивание релизов, только одиночные игры).
