using NvAPIWrapper;
using NvAPIWrapper.DRS;
using WinPulse.Core;

namespace WinPulse.Collectors.Gpu;

/// <summary>
/// Профили драйвера NVIDIA (DRS) через NvAPIWrapper (github.com/falahati/NvAPIWrapper, LGPL-3.0).
/// Профиль игры ищется по имени exe; если профиля нет — создаётся «WinPulse — игра».
/// Перед записью код настройки сверяется с её именем в драйвере (см. <see cref="IsKnown"/>):
/// ошибка в коде превращается в «не поддерживается», а не в испорченный профиль.
/// </summary>
public static class NvidiaDrs
{
    /// <summary>Режим управления электропитанием (Power management mode). 1 — максимальная производительность.</summary>
    public const uint PowerMode = 0x1057EB71;
    public const uint PowerModeMax = 1;
    /// <summary>Максимум заранее подготовленных кадров (в панели — «Режим низкой задержки»: 1 = «Вкл.»).</summary>
    public const uint PreRenderLimit = 0x007BA09E;
    /// <summary>Ограничение кадров драйвером (Max Frame Rate), кадров в секунду; 0 — выключено.</summary>
    public const uint FrameRateLimit = 0x10835002;
    /// <summary>Размер кэша шейдеров на диске, МБ (0xFFFFFFFF — без ограничения).</summary>
    public const uint ShaderCacheSize = 0x00AC8497;

    private static readonly object Lock = new();

    // Отдельный метод: если NvAPIWrapper.dll рядом нет, ошибка загрузки возникнет здесь, внутри try у Available,
    // а не при компиляции вызывающего метода.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Initialize() => NVIDIA.Initialize();
    private static bool? _available;

    /// <summary>NVAPI загрузился (есть видеокарта NVIDIA и её драйвер).</summary>
    public static bool Available
    {
        get
        {
            lock (Lock)
            {
                if (_available is null)
                {
                    try
                    {
                        Initialize();
                        _available = true;
                    }
                    catch (Exception ex)
                    {
                        Diag.Report("nvapi-init", ex);
                        _available = false;
                    }
                }
                return _available.Value;
            }
        }
    }

    /// <summary>Имя настройки в драйвере по коду или null.</summary>
    public static string? SettingName(uint id)
    {
        if (!Available) return null;
        try
        {
            lock (Lock) return SettingInfo.FromId(id).Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Драйвер знает настройку с этим кодом, и её имя содержит одно из ожидаемых слов.</summary>
    public static bool IsKnown(uint id, params string[] nameParts)
    {
        string? name = SettingName(id);
        return name is not null && nameParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Значение, заданное прямо в профиле игры (exe) или глобальном профиле (null). Found = false — в профиле не задано.</summary>
    public static (bool Found, uint? Value) ReadLocal(string? exe, uint id)
    {
        if (!Available) return (false, null);
        lock (Lock)
        {
            try
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = exe is null ? session.BaseProfile : session.FindApplicationProfile(exe);
                if (profile is null) return (false, null);
                var setting = profile.GetSetting(id);
                bool local = setting.SettingLocation.ToString().Contains("Current", StringComparison.OrdinalIgnoreCase);
                return (local, setting.CurrentValue is uint u ? u : null);
            }
            catch
            {
                return (false, null);
            }
        }
    }

    /// <summary>Действующее значение для игры: из её профиля, иначе из глобального. null — не задано нигде (значение драйвера по умолчанию).</summary>
    public static uint? ReadEffective(string? exe, uint id)
    {
        if (!Available) return null;
        lock (Lock)
        {
            try
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                if (exe is not null)
                {
                    var profile = session.FindApplicationProfile(exe);
                    if (profile is not null)
                    {
                        try
                        {
                            if (profile.GetSetting(id).CurrentValue is uint v) return v;
                        }
                        catch
                        {
                            // В профиле игры не задано — смотрим глобальный.
                        }
                    }
                }
                try
                {
                    return session.BaseProfile.GetSetting(id).CurrentValue is uint g ? g : null;
                }
                catch
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                Diag.Report("nvapi-read", ex);
                return null;
            }
        }
    }

    /// <summary>Записать значение (null — удалить из профиля, то есть вернуть значение по умолчанию). Возвращает текст ошибки или null.</summary>
    public static string? Write(string? exe, string? friendlyName, uint id, uint? value)
    {
        if (!Available) return Lang.T("Драйвер NVIDIA недоступен.", "The NVIDIA driver isn't available.");
        lock (Lock)
        {
            try
            {
                using var session = DriverSettingsSession.CreateAndLoad();
                DriverSettingsProfile profile;
                if (exe is null)
                {
                    profile = session.BaseProfile;
                }
                else
                {
                    profile = session.FindApplicationProfile(exe) ?? CreateProfile(session, exe, friendlyName);
                }

                if (value is null)
                {
                    try
                    {
                        profile.DeleteSetting(id);
                    }
                    catch
                    {
                        // Настройки в профиле нет — значит, уже по умолчанию.
                    }
                }
                else
                {
                    profile.SetSetting(id, value.Value);
                }
                session.Save();
                return null;
            }
            catch (Exception ex)
            {
                Diag.Report("nvapi-write", ex);
                string text = ex.Message;
                return text.Contains("PRIVILEGE", StringComparison.OrdinalIgnoreCase) || text.Contains("ACCESS", StringComparison.OrdinalIgnoreCase)
                    ? Lang.T("Драйвер не разрешил запись без прав администратора. Перезапустите WinPulse от администратора (кнопка внизу меню) и повторите.",
                        "The driver refused to save without admin rights. Restart WinPulse as administrator (button at the bottom of the menu) and retry.")
                    : Lang.T("Драйвер NVIDIA вернул ошибку: ", "The NVIDIA driver returned an error: ") + text;
            }
        }
    }

    private static DriverSettingsProfile CreateProfile(DriverSettingsSession session, string exe, string? friendlyName)
    {
        string name = "WinPulse - " + (string.IsNullOrWhiteSpace(friendlyName) ? Path.GetFileNameWithoutExtension(exe) : friendlyName);
        var profile = DriverSettingsProfile.CreateProfile(session, name, null);
        ProfileApplication.CreateApplication(profile, exe, friendlyName ?? exe, null, null, false, null);
        return profile;
    }
}
