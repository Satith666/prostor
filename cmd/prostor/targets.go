package main

// Catalogue of cleanup locations. Paths use %ENV% variables, * wildcards and
// a few placeholders resolved at runtime: {steam}, {locallow}.
// Only caches, temporary files, logs and dumps are listed: nothing here
// holds passwords, cookies, history, saved games or user documents.

type Category struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	Desc string `json:"desc"`
}

var categories = []Category{
	{"system", "Система Windows", "Временные файлы, кэш обновлений, отчёты об ошибках, журналы"},
	{"browsers", "Браузеры", "Только кэш. Пароли, вкладки, история и куки не затрагиваются"},
	{"apps", "Программы", "Кэш мессенджеров, плееров, редакторов и лаунчеров"},
	{"games", "Игры и видеокарта", "Кэш шейдеров, игровых лаунчеров и остатки драйверов"},
	{"dev", "Разработка", "Кэши пакетных менеджеров и сборщиков"},
	{"privacy", "Конфиденциальность", "Следы недавней активности"},
}

// Chromium-based browser cache folders, relative to the "User Data" folder.
var chromiumProfileCaches = []string{
	`*\Cache`, `*\Code Cache`, `*\GPUCache`, `*\DawnCache`, `*\DawnGraphiteCache`, `*\DawnWebGPUCache`,
	`*\Service Worker\CacheStorage`, `*\Service Worker\ScriptCache`, `*\Media Cache`,
	`ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`, `component_crx_cache`, `Crashpad\reports`,
}

func chromium(base string) []string {
	out := make([]string, 0, len(chromiumProfileCaches))
	for _, c := range chromiumProfileCaches {
		out = append(out, base+`\`+c)
	}
	return out
}

func operaPaths(name string) []string {
	var out []string
	for _, root := range []string{`%LOCALAPPDATA%\Opera Software\` + name, `%APPDATA%\Opera Software\` + name} {
		for _, c := range []string{`Cache`, `Code Cache`, `GPUCache`, `ShaderCache`, `GrShaderCache`, `Service Worker\CacheStorage`, `Service Worker\ScriptCache`, `Crash Reports`} {
			out = append(out, root+`\`+c)
		}
	}
	return out
}

func electron(base string) []string {
	var out []string
	for _, c := range []string{`Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `DawnGraphiteCache`, `Crashpad\reports`, `Service Worker\CacheStorage`, `blob_storage`} {
		out = append(out, base+`\`+c)
	}
	return out
}

func joinPaths(groups ...[]string) []string {
	var out []string
	for _, g := range groups {
		out = append(out, g...)
	}
	return out
}

type Target struct {
	ID      string   `json:"id"`
	Cat     string   `json:"cat"`
	Name    string   `json:"name"`
	Desc    string   `json:"desc"`
	Admin   bool     `json:"admin"`
	Default bool     `json:"default"`
	Caution bool     `json:"caution"`
	Risk    string   `json:"risk"` // safe | caution | expert (set in init)
	Procs   []string `json:"procs"`

	Paths    []string `json:"-"`
	Masks    []string `json:"-"` // lowercase name masks; empty = every file
	MinAge   int      `json:"-"` // only files older than N days
	Recent   bool     `json:"-"` // apply the "skip recent files" guard
	Flat     bool     `json:"-"` // do not descend into subfolders
	KeepDirs bool     `json:"-"` // keep empty subfolders
	Special  string   `json:"-"` // recycle | takeown | services:a,b
}

var chromiumBrowserProcs = []string{"chrome.exe", "msedge.exe", "browser.exe", "brave.exe", "vivaldi.exe", "opera.exe", "firefox.exe"}

var targets = []Target{
	// ------------------------------------------------------------ system
	{ID: "user_temp", Cat: "system", Name: "Временные файлы пользователя",
		Desc:  "Папка %TEMP%: остатки установщиков, распаковок и программ. Свежие файлы пропускаются.",
		Paths: []string{`%TEMP%`, `%LOCALAPPDATA%\Temp`}, Recent: true, Default: true},
	{ID: "win_temp", Cat: "system", Name: "Временные файлы Windows",
		Desc:  "C:\\Windows\\Temp — временные файлы служб и установщиков.",
		Paths: []string{`%SystemRoot%\Temp`}, Recent: true, Admin: true, Default: true},
	{ID: "wu_cache", Cat: "system", Name: "Кэш Центра обновления Windows",
		Desc:  "Уже установленные пакеты обновлений. Служба обновления будет перезапущена.",
		Paths: []string{`%SystemRoot%\SoftwareDistribution\Download`}, Admin: true, Default: true,
		Special: "services:wuauserv"},
	{ID: "delivery_opt", Cat: "system", Name: "Кэш оптимизации доставки",
		Desc:  "Файлы обновлений, которыми Windows делится с другими ПК.",
		Paths: []string{`%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache`, `%SystemRoot%\SoftwareDistribution\DeliveryOptimization`},
		Admin: true, Default: true, Special: "services:DoSvc"},
	{ID: "wer", Cat: "system", Name: "Отчёты об ошибках Windows",
		Desc:    "Архив и очередь отчётов о сбоях программ (WER).",
		Paths:   []string{`%ProgramData%\Microsoft\Windows\WER\ReportArchive`, `%ProgramData%\Microsoft\Windows\WER\ReportQueue`, `%ProgramData%\Microsoft\Windows\WER\Temp`, `%LOCALAPPDATA%\Microsoft\Windows\WER`},
		Default: true},
	{ID: "sys_dumps", Cat: "system", Name: "Дампы памяти системы",
		Desc:  "MEMORY.DMP, минидампы и отчёты ядра после «синих экранов». Могут занимать гигабайты.",
		Paths: []string{`%SystemRoot%\MEMORY.DMP`, `%SystemRoot%\Minidump`, `%SystemRoot%\LiveKernelReports`},
		Admin: true, Default: true},
	{ID: "app_dumps", Cat: "system", Name: "Дампы падений программ",
		Desc:  "Файлы .dmp, которые программы оставляют при аварийном завершении.",
		Paths: []string{`%LOCALAPPDATA%\CrashDumps`}, Default: true},
	{ID: "win_logs", Cat: "system", Name: "Журналы Windows и установки",
		Desc:  "Файлы журналов (.log, .etl, .cab) старше 7 дней из Windows\\Logs и Panther.",
		Paths: []string{`%SystemRoot%\Logs`, `%SystemRoot%\Panther`, `%SystemRoot%\debug`, `%SystemRoot%\System32\LogFiles\setupcln`},
		Masks: []string{"*.log", "*.etl", "*.cab", "*.old", "*.lo_", "*.txt", "*.xml"}, MinAge: 7, KeepDirs: true,
		Admin: true, Default: true},
	{ID: "thumbcache", Cat: "system", Name: "Кэш эскизов и значков",
		Desc:  "Миниатюры Проводника. Пересоздаются автоматически; занятые файлы пропускаются.",
		Paths: []string{`%LOCALAPPDATA%\Microsoft\Windows\Explorer`}, Masks: []string{"thumbcache_*.db", "iconcache_*.db"},
		Flat: true, Default: true},
	{ID: "inetcache", Cat: "system", Name: "Кэш интернета Windows",
		Desc:  "Кэш WinINet, который используют встроенные приложения и старые компоненты.",
		Paths: []string{`%LOCALAPPDATA%\Microsoft\Windows\INetCache`}, Default: true},
	{ID: "uwp_temp", Cat: "system", Name: "Временные файлы приложений Microsoft Store",
		Desc:    "Папки Temp, TempState и INetCache внутри пакетов приложений.",
		Paths:   []string{`%LOCALAPPDATA%\Packages\*\AC\Temp`, `%LOCALAPPDATA%\Packages\*\AC\INetCache`, `%LOCALAPPDATA%\Packages\*\TempState`},
		Default: true},
	{ID: "recycle", Cat: "system", Name: "Корзина",
		Desc:    "Файлы в Корзине на всех дисках. После очистки их нельзя будет восстановить.",
		Special: "recycle", Default: true},
	{ID: "prefetch", Cat: "system", Name: "Данные Prefetch",
		Desc:  "Данные ускорения запуска программ. Польза от удаления небольшая, первые запуски станут медленнее.",
		Paths: []string{`%SystemRoot%\Prefetch`}, Masks: []string{"*.pf"}, Flat: true, Admin: true, Caution: true},
	{ID: "windows_old", Cat: "system", Name: "Предыдущая установка Windows (Windows.old)",
		Desc:  "Остаётся после обновления Windows. После удаления откат на прошлую версию станет невозможен.",
		Paths: []string{`%SystemDrive%\Windows.old`}, Admin: true, Caution: true, Special: "takeown"},
	{ID: "upgrade_leftovers", Cat: "system", Name: "Временные файлы обновления Windows",
		Desc:  "Папки $WINDOWS.~BT, $WINDOWS.~WS и $GetCurrent после крупного обновления. Не удаляйте во время обновления.",
		Paths: []string{`%SystemDrive%\$WINDOWS.~BT`, `%SystemDrive%\$WINDOWS.~WS`, `%SystemDrive%\$GetCurrent`, `%SystemDrive%\ESD\Download`},
		Admin: true, Caution: true, Special: "takeown"},

	// ------------------------------------------------------------ browsers
	{ID: "chrome", Cat: "browsers", Name: "Google Chrome", Desc: "Кэш страниц, кода и шейдеров во всех профилях.",
		Paths: chromium(`%LOCALAPPDATA%\Google\Chrome\User Data`), Procs: []string{"chrome.exe"}, Default: true},
	{ID: "edge", Cat: "browsers", Name: "Microsoft Edge", Desc: "Кэш страниц, кода и шейдеров во всех профилях.",
		Paths: chromium(`%LOCALAPPDATA%\Microsoft\Edge\User Data`), Procs: []string{"msedge.exe"}, Default: true},
	{ID: "yandex", Cat: "browsers", Name: "Яндекс Браузер", Desc: "Кэш страниц, кода и шейдеров во всех профилях.",
		Paths: chromium(`%LOCALAPPDATA%\Yandex\YandexBrowser\User Data`), Procs: []string{"browser.exe"}, Default: true},
	{ID: "opera", Cat: "browsers", Name: "Opera и Opera GX", Desc: "Кэш страниц и шейдеров.",
		Paths: joinPaths(operaPaths("Opera Stable"), operaPaths("Opera GX Stable")), Procs: []string{"opera.exe"}, Default: true},
	{ID: "brave", Cat: "browsers", Name: "Brave", Desc: "Кэш страниц, кода и шейдеров во всех профилях.",
		Paths: chromium(`%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data`), Procs: []string{"brave.exe"}, Default: true},
	{ID: "vivaldi", Cat: "browsers", Name: "Vivaldi", Desc: "Кэш страниц, кода и шейдеров во всех профилях.",
		Paths: chromium(`%LOCALAPPDATA%\Vivaldi\User Data`), Procs: []string{"vivaldi.exe"}, Default: true},
	{ID: "firefox", Cat: "browsers", Name: "Mozilla Firefox", Desc: "Кэш cache2, эскизы и кэш запуска во всех профилях.",
		Paths: []string{`%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2`, `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\startupCache`,
			`%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\thumbnails`, `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\jumpListCache`,
			`%APPDATA%\Mozilla\Firefox\Profiles\*\shader-cache`, `%APPDATA%\Mozilla\Firefox\Crash Reports\pending`},
		Procs: []string{"firefox.exe"}, Default: true},

	// ------------------------------------------------------------ apps
	{ID: "telegram", Cat: "apps", Name: "Telegram Desktop", Desc: "Кэш медиа и стикеров. Файлы заново подгрузятся при открытии чатов. Скачанные вами файлы не трогаются.",
		Paths: []string{`%APPDATA%\Telegram Desktop\tdata\user_data*\cache`, `%APPDATA%\Telegram Desktop\tdata\user_data*\media_cache`,
			`%LOCALAPPDATA%\Packages\TelegramMessengerLLP.TelegramDesktop_*\LocalCache\Roaming\Telegram Desktop UWP\tdata\user_data*\cache`,
			`%LOCALAPPDATA%\Packages\TelegramMessengerLLP.TelegramDesktop_*\LocalCache\Roaming\Telegram Desktop UWP\tdata\user_data*\media_cache`},
		Procs: []string{"telegram.exe"}, Default: true},
	{ID: "discord", Cat: "apps", Name: "Discord", Desc: "Кэш изображений, кода и GPU (включая PTB и Canary).",
		Paths: joinPaths(electron(`%APPDATA%\discord`), electron(`%APPDATA%\discordptb`), electron(`%APPDATA%\discordcanary`)),
		Procs: []string{"discord.exe", "discordptb.exe", "discordcanary.exe"}, Default: true},
	{ID: "spotify", Cat: "apps", Name: "Spotify", Desc: "Кэш треков для потокового воспроизведения (скачанные плейлисты не трогаются).",
		Paths: []string{`%LOCALAPPDATA%\Spotify\Storage`, `%LOCALAPPDATA%\Spotify\Browser\Cache`, `%LOCALAPPDATA%\Packages\SpotifyAB.SpotifyMusic_*\LocalCache\Spotify\Storage`},
		Procs: []string{"spotify.exe"}, Default: true},
	{ID: "vscode", Cat: "apps", Name: "Visual Studio Code", Desc: "Кэш интерфейса, журналы и загруженные пакеты расширений.",
		Paths: joinPaths(electron(`%APPDATA%\Code`), []string{`%APPDATA%\Code\CachedData`, `%APPDATA%\Code\CachedExtensionVSIXs`, `%APPDATA%\Code\logs`}),
		Procs: []string{"code.exe"}, Default: true},
	{ID: "teams", Cat: "apps", Name: "Microsoft Teams (классический)", Desc: "Кэш и временные файлы.",
		Paths: joinPaths(electron(`%APPDATA%\Microsoft\Teams`), []string{`%APPDATA%\Microsoft\Teams\tmp`}), Procs: []string{"teams.exe"}, Default: true},
	{ID: "onedrive_logs", Cat: "apps", Name: "Журналы OneDrive", Desc: "Журналы синхронизации старше 7 дней.",
		Paths: []string{`%LOCALAPPDATA%\Microsoft\OneDrive\logs`}, MinAge: 7, Default: true},
	{ID: "java_cache", Cat: "apps", Name: "Кэш Java", Desc: "Кэш загруженных Java-приложений и апплетов.",
		Paths: []string{`{locallow}\Sun\Java\Deployment\cache`}, Default: true},
	{ID: "adobe_media", Cat: "apps", Name: "Медиакэш Adobe", Desc: "Кэш Premiere Pro и After Effects. Пересоздаётся при открытии проектов (первое открытие станет медленнее).",
		Paths: []string{`%APPDATA%\Adobe\Common\Media Cache Files`, `%APPDATA%\Adobe\Common\Media Cache`, `%APPDATA%\Adobe\Common\Peak Files`},
		Procs: []string{"adobe premiere pro.exe", "afterfx.exe"}, Caution: true},

	// ------------------------------------------------------------ games
	{ID: "d3d_cache", Cat: "games", Name: "Кэш шейдеров DirectX", Desc: "Пересоздаётся при запуске игр. Помогает при артефактах после обновления драйвера.",
		Paths: []string{`%LOCALAPPDATA%\D3DSCache`}, Default: true},
	{ID: "nvidia_cache", Cat: "games", Name: "Кэш шейдеров NVIDIA", Desc: "DXCache, GLCache и NV_Cache. После очистки первые запуски игр могут подтормаживать.",
		Paths:   []string{`%LOCALAPPDATA%\NVIDIA\DXCache`, `%LOCALAPPDATA%\NVIDIA\GLCache`, `%LOCALAPPDATA%\NVIDIA\OptixCache`, `{locallow}\NVIDIA\PerDriverVersion\DXCache`, `%ProgramData%\NVIDIA Corporation\NV_Cache`},
		Caution: true},
	{ID: "amd_cache", Cat: "games", Name: "Кэш шейдеров AMD", Desc: "DxCache, DxcCache, GLCache, VkCache. После очистки первые запуски игр могут подтормаживать.",
		Paths:   []string{`%LOCALAPPDATA%\AMD\DxCache`, `%LOCALAPPDATA%\AMD\DxcCache`, `%LOCALAPPDATA%\AMD\GLCache`, `%LOCALAPPDATA%\AMD\VkCache`, `%LOCALAPPDATA%\AMD\CN\logs`},
		Caution: true},
	{ID: "intel_cache", Cat: "games", Name: "Кэш шейдеров Intel", Desc: "Кэш шейдеров встроенной и дискретной графики Intel.",
		Paths: []string{`{locallow}\Intel\ShaderCache`, `%LOCALAPPDATA%\Intel\ShaderCache`}, Caution: true},
	{ID: "driver_leftovers", Cat: "games", Name: "Распакованные установщики драйверов", Desc: "Папки C:\\NVIDIA, C:\\AMD и загрузки драйверов NVIDIA — остаются после установки и больше не нужны.",
		Paths: []string{`%SystemDrive%\NVIDIA`, `%SystemDrive%\AMD`, `%ProgramData%\NVIDIA Corporation\Downloader`}, Admin: true, Default: true},
	{ID: "steam", Cat: "games", Name: "Steam — кэш и журналы", Desc: "Веб-кэш клиента, журналы и дампы. Игры и сохранения не затрагиваются.",
		Paths: []string{`{steam}\appcache\httpcache`, `{steam}\logs`, `{steam}\dumps`, `%LOCALAPPDATA%\Steam\htmlcache`},
		Procs: []string{"steam.exe", "steamwebhelper.exe"}, Default: true},
	{ID: "steam_shaders", Cat: "games", Name: "Steam — кэш шейдеров игр", Desc: "Предкомпилированные шейдеры. Будут заново собраны при запуске игр.",
		Paths: []string{`{steam}\steamapps\shadercache`}, Procs: []string{"steam.exe"}, Caution: true},
	{ID: "epic", Cat: "games", Name: "Epic Games Launcher", Desc: "Веб-кэш и журналы лаунчера.",
		Paths: []string{`%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache*`, `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Logs`},
		Procs: []string{"epicgameslauncher.exe"}, Default: true},
	{ID: "roblox", Cat: "games", Name: "Roblox и Roblox Studio", Desc: "Журналы и загрузки установщика. Плейсы и плагины не затрагиваются.",
		Paths: []string{`%LOCALAPPDATA%\Roblox\logs`, `%LOCALAPPDATA%\Roblox\Downloads`},
		Procs: []string{"robloxplayerbeta.exe", "robloxstudiobeta.exe"}, MinAge: 1, Default: true},
	{ID: "minecraft", Cat: "games", Name: "Minecraft — журналы и веб-кэш", Desc: "Старые журналы и кэш лаунчера. Миры и моды не затрагиваются.",
		Paths: []string{`%APPDATA%\.minecraft\logs`, `%APPDATA%\.minecraft\webcache2`, `%APPDATA%\.minecraft\crash-reports`},
		Procs: []string{"minecraft.exe", "minecraftlauncher.exe"}, MinAge: 1, Default: true},

	// ------------------------------------------------------------ dev
	{ID: "pip", Cat: "dev", Name: "Кэш pip (Python)", Desc: "Загруженные пакеты Python. Установленные библиотеки не затрагиваются.",
		Paths: []string{`%LOCALAPPDATA%\pip\Cache`}, Default: true},
	{ID: "npm", Cat: "dev", Name: "Кэш npm, Yarn и pnpm", Desc: "Кэш пакетов Node.js. Папки node_modules проектов не затрагиваются.",
		Paths: []string{`%LOCALAPPDATA%\npm-cache\_cacache`, `%LOCALAPPDATA%\npm-cache\_logs`, `%LOCALAPPDATA%\Yarn\Cache`, `%LOCALAPPDATA%\pnpm-cache`}, Default: true},
	{ID: "gobuild", Cat: "dev", Name: "Кэш сборки Go", Desc: "go-build — промежуточные результаты компиляции.",
		Paths: []string{`%LOCALAPPDATA%\go-build`}, Default: true},
	{ID: "nuget", Cat: "dev", Name: "HTTP-кэш NuGet", Desc: "Кэш загрузок NuGet (папка пакетов .nuget\\packages не затрагивается).",
		Paths: []string{`%LOCALAPPDATA%\NuGet\v3-cache`, `%LOCALAPPDATA%\NuGet\plugins-cache`}, Default: true},
	{ID: "gradle", Cat: "dev", Name: "Кэш Gradle", Desc: "Зависимости и результаты сборок Gradle. Следующая сборка скачает всё заново.",
		Paths: []string{`%USERPROFILE%\.gradle\caches`}, Caution: true},
	{ID: "cargo", Cat: "dev", Name: "Кэш Cargo (Rust)", Desc: "Архивы загруженных crate-пакетов.",
		Paths: []string{`%USERPROFILE%\.cargo\registry\cache`}, Caution: true},

	// ------------------------------------------------------------ privacy
	{ID: "recent", Cat: "privacy", Name: "Недавние документы", Desc: "Список недавно открытых файлов в Проводнике (закреплённые списки не трогаются).",
		Paths: []string{`%APPDATA%\Microsoft\Windows\Recent`}, Masks: []string{"*.lnk"}, Flat: true},
}

// riskOf classifies a target for the interface: "safe" — the files are
// recreated by the programs themselves; "caution" — something noticeable
// changes (first runs get slower, the Recycle Bin is gone); "expert" — the
// action cannot be undone (Windows.old, upgrade files).
func riskOf(t *Target) string {
	switch {
	case t.Special == "takeown":
		return "expert"
	case t.Caution || t.Special == "recycle":
		return "caution"
	}
	return "safe"
}

func init() {
	for i := range targets {
		targets[i].Risk = riskOf(&targets[i])
	}
}

func targetByID(id string) *Target {
	for i := range targets {
		if targets[i].ID == id {
			return &targets[i]
		}
	}
	return nil
}
