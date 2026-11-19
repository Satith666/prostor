namespace WinPulse.Core.Gaming;

public enum UpscalerTech
{
    Dlss,
    DlssFrameGen,
    DlssRayReconstruction,
    Fsr,
    Xess,
    Streamline,
}

/// <summary>Найденная в папке игры библиотека апскейлера.</summary>
public sealed record UpscalerDll(UpscalerTech Tech, string RelativePath, string? Version);

/// <summary>Что нашлось в одной игре.</summary>
public sealed class GameUpscalers
{
    public string Game { get; init; } = "";
    public string Path { get; init; } = "";
    public IReadOnlyList<UpscalerDll> Dlls { get; init; } = [];
    /// <summary>Название античита, если нашлись его файлы (EasyAntiCheat, BattlEye…).</summary>
    public string? AntiCheat { get; init; }

    public UpscalerDll? Find(UpscalerTech tech) => Dlls.FirstOrDefault(d => d.Tech == tech);
    public bool Has(UpscalerTech tech) => Dlls.Any(d => d.Tech == tech);
}

/// <summary>Что видеокарта умеет — для советов.</summary>
public sealed record GpuAbilities(bool IsRtx, bool HasDlssFrameGen, string Name);

/// <summary>
/// Узнаём апскейлеры по именам библиотек и даём советы. Подход как у DLSS Swapper и OptiScaler (GitHub),
/// но WinPulse ничего не подменяет — только показывает и объясняет.
/// </summary>
public static class UpscalerCatalog
{
    private static readonly Dictionary<string, UpscalerTech> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = UpscalerTech.Dlss,
        ["nvngx_dlssg.dll"] = UpscalerTech.DlssFrameGen,
        ["nvngx_dlssd.dll"] = UpscalerTech.DlssRayReconstruction,
        ["libxess.dll"] = UpscalerTech.Xess,
        ["libxess_dx11.dll"] = UpscalerTech.Xess,
        ["amd_fidelityfx_dx12.dll"] = UpscalerTech.Fsr,
        ["amd_fidelityfx_vk.dll"] = UpscalerTech.Fsr,
        ["amd_fidelityfx_upscaler_dx12.dll"] = UpscalerTech.Fsr,
        ["ffx_fsr2_api_x64.dll"] = UpscalerTech.Fsr,
        ["ffx_fsr2_api_dx12_x64.dll"] = UpscalerTech.Fsr,
        ["ffx_fsr2_api_vk_x64.dll"] = UpscalerTech.Fsr,
        ["sl.interposer.dll"] = UpscalerTech.Streamline,
    };

    // Признаки античита по именам файлов и папок.
    private static readonly (string Marker, string Name)[] AntiCheats =
    [
        ("easyanticheat", "Easy Anti-Cheat"),
        ("battleye", "BattlEye"),
        ("beservice", "BattlEye"),
        ("gameguard", "nProtect GameGuard"),
        ("xigncode", "XIGNCODE3"),
        ("equ8", "EQU8"),
        ("vgc.exe", "Riot Vanguard"),
        ("faceit", "FACEIT"),
        ("punkbuster", "PunkBuster"),
        ("anticheat", "античит"),
    ];

    public static UpscalerTech? Classify(string fileName) => Files.TryGetValue(fileName, out var t) ? t : null;

    public static string? AntiCheatOf(string relativePath)
    {
        string p = relativePath.ToLowerInvariant();
        foreach (var (marker, name) in AntiCheats)
            if (p.Contains(marker, StringComparison.Ordinal)) return name == "античит" ? Lang.T("античит", "anti-cheat") : name;
        return null;
    }

    /// <summary>«FSR 2» по имени файла, если версии в файле нет.</summary>
    public static string Label(UpscalerDll dll)
    {
        string name = System.IO.Path.GetFileName(dll.RelativePath).ToLowerInvariant();
        string tech = dll.Tech switch
        {
            UpscalerTech.Dlss => "DLSS",
            UpscalerTech.DlssFrameGen => Lang.T("DLSS генерация кадров", "DLSS Frame Generation"),
            UpscalerTech.DlssRayReconstruction => "DLSS Ray Reconstruction",
            UpscalerTech.Xess => "XeSS",
            UpscalerTech.Fsr => name.Contains("fsr2") ? "FSR 2" : "FSR 3+",
            UpscalerTech.Streamline => "Streamline",
            _ => dll.Tech.ToString(),
        };
        return dll.Version is null || dll.Tech == UpscalerTech.Fsr && name.Contains("fsr2") ? tech : $"{tech} {ShortVersion(dll.Version)}";
    }

    public static string ShortVersion(string version)
    {
        var parts = version.Split('.', ',', ' ').Where(p => p.Length > 0).ToArray();
        return string.Join(".", parts.Take(3));
    }

    /// <summary>Версия для сравнения: «3.7.10.0» → 3.7.10.0; без версии — null (идёт последней).</summary>
    public static Version? ParseVersion(string? version)
    {
        if (version is null) return null;
        var parts = version.Split('.', ',', ' ').Where(p => p.Length > 0).Take(4).ToArray();
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i])) return null;
        return nums.Length switch
        {
            0 => null,
            1 => new Version(nums[0], 0),
            2 => new Version(nums[0], nums[1]),
            3 => new Version(nums[0], nums[1], nums[2]),
            _ => new Version(nums[0], nums[1], nums[2], nums[3]),
        };
    }

    /// <summary>DLSS старше 3.7 (до DLSS 4 версии шли 2.x и 3.x; DLSS 4 — 310.x).</summary>
    public static bool IsOldDlss(string? version)
    {
        if (version is null) return false;
        var parts = version.Split('.', ',', ' ').Where(p => p.Length > 0).ToArray();
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)) return false;
        return major < 3 || major == 3 && minor < 7;
    }

    /// <summary>Советы по игре: что включить, что обновить, где осторожно.</summary>
    public static List<string> Advice(GameUpscalers g, GpuAbilities gpu)
    {
        var list = new List<string>();
        if (g.AntiCheat is not null)
            list.Add(Lang.T($"Онлайн-игра с античитом ({g.AntiCheat}): не подменяйте библиотеки — можно получить бан. Пользуйтесь только тем, что есть в настройках игры.",
                $"Online game with anti-cheat ({g.AntiCheat}): don't swap libraries — you risk a ban. Use only what the game's settings offer."));

        var dlss = g.Find(UpscalerTech.Dlss);
        bool hasFsr = g.Has(UpscalerTech.Fsr), hasXess = g.Has(UpscalerTech.Xess);
        string other = hasFsr && hasXess ? Lang.T("FSR и XeSS", "FSR and XeSS") : hasFsr ? "FSR" : "XeSS";

        if (dlss is not null && gpu.IsRtx)
        {
            list.Add(Lang.T("Включите DLSS в настройках игры. Если игра упирается в видеокарту, режим «Качество» обычно заметно прибавляет FPS почти без потери чёткости.",
                "Enable DLSS in the game's settings. If the game is GPU-bound, Quality mode usually adds noticeable FPS with almost no loss of sharpness."));
            if (IsOldDlss(dlss.Version) && g.AntiCheat is null)
                list.Add(Lang.T($"DLSS {ShortVersion(dlss.Version!)} — устаревшая версия. Новые заметно чётче в режимах «Баланс» и «Производительность», а значит можно взять режим быстрее. Обновить: NVIDIA App → Графика → переопределение DLSS (если игра поддерживается) или DLSS Swapper.",
                    $"DLSS {ShortVersion(dlss.Version!)} is outdated. Newer versions are noticeably sharper in Balanced and Performance modes, so you can pick a faster mode. Update via NVIDIA App → Graphics → DLSS override (if supported) or DLSS Swapper."));
        }
        else if (dlss is null && (hasFsr || hasXess) && gpu.IsRtx && g.AntiCheat is null)
        {
            list.Add(Lang.T($"В игре нет DLSS, только {other}. OptiScaler умеет подставить вместо них DLSS — на RTX картинка обычно чище. Только для одиночной игры.",
                $"The game has no DLSS, only {other}. OptiScaler can substitute DLSS — usually a cleaner image on RTX. Single-player only."));
        }
        else if (hasFsr || hasXess)
        {
            list.Add(Lang.T($"Включите {(hasXess && !hasFsr ? "XeSS" : "FSR")} в настройках игры — это прибавка FPS ценой небольшой потери чёткости.",
                $"Enable {(hasXess && !hasFsr ? "XeSS" : "FSR")} in the game's settings — more FPS at a small cost in sharpness."));
        }

        if (g.Has(UpscalerTech.DlssFrameGen) && gpu.IsRtx && !gpu.HasDlssFrameGen && g.AntiCheat is null)
            list.Add(Lang.T($"Генерация кадров DLSS работает только на RTX 40 и 50 — на {gpu.Name} её в меню не будет. Вместо неё OptiScaler может включить генерацию кадров FSR 3: заметно плавнее, но задержка чуть выше. Только для одиночной игры.",
                $"DLSS Frame Generation only works on RTX 40 and 50 — it won't show up on {gpu.Name}. Instead, OptiScaler can enable FSR 3 frame generation: noticeably smoother, slightly higher latency. Single-player only."));

        if (g.Dlls.Count == 0)
            list.Add(Lang.T("Библиотек DLSS, FSR или XeSS не найдено. Если в игре есть встроенное масштабирование (FSR 1, «Масштаб рендера») — оно тоже прибавит FPS.",
                "No DLSS, FSR or XeSS libraries found. If the game has built-in scaling (FSR 1, render scale), that adds FPS too."));
        return list;
    }

    /// <summary>Что умеет видеокарта по её имени.</summary>
    public static GpuAbilities Abilities(string gpuName)
    {
        bool rtx = gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase);
        bool fg = rtx && System.Text.RegularExpressions.Regex.IsMatch(gpuName, @"RTX\s*(40|50)\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string shortName = gpuName.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Replace("GeForce ", "", StringComparison.OrdinalIgnoreCase).Trim();
        return new GpuAbilities(rtx, fg, shortName.Length == 0 ? gpuName : shortName);
    }
}
