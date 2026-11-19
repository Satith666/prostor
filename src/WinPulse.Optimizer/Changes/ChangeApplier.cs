using System.Security;
using WinPulse.Collectors.Display;
using WinPulse.Core;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.Changes;

/// <summary>Выполняет одно изменение. Одинаково работает в основном процессе и во вспомогательном (с правами администратора).</summary>
public static class ChangeApplier
{
    public static ChangeResult Apply(Change c, int index)
    {
        var result = new ChangeResult { Index = index, Change = c };
        try
        {
            switch (c.Kind)
            {
                case ChangeKind.RegistrySet:
                case ChangeKind.RegistryDelete:
                    Reg.Apply(c);
                    break;

                case ChangeKind.PowerActivate:
                {
                    string guid = (c.SchemeGuid ?? "").ToLowerInvariant();
                    if (guid.Length == 0) throw new ArgumentException("SchemeGuid");
                    bool exists = PowerSchemes.List().Any(s => s.Guid == guid);
                    if (!exists)
                    {
                        if (!c.CreateIfMissing)
                            return Fail(result, Lang.T("Схема электропитания не найдена: ", "Power plan not found: ") + guid);
                        string? created = PowerSchemes.Duplicate(guid, out string? dupError);
                        if (created is null)
                            return Fail(result, Lang.T("Не удалось создать схему: ", "Could not create the plan: ") + dupError);
                        result.CreatedSchemeGuid = created;
                        guid = created;
                    }
                    string? error = PowerSchemes.SetActive(guid);
                    if (error is not null) return Fail(result, error);
                    break;
                }

                case ChangeKind.PowerDelete:
                {
                    string guid = (c.SchemeGuid ?? "").ToLowerInvariant();
                    if (PowerSchemes.IsWellKnown(guid))
                        return Fail(result, Lang.T("Стандартные схемы не удаляем", "Built-in plans are never deleted"));
                    if (!PowerSchemes.List().Any(s => s.Guid == guid)) break; // уже нет — это успех
                    string? error = PowerSchemes.Delete(guid);
                    if (error is not null) return Fail(result, error);
                    break;
                }

                case ChangeKind.DisplayRefresh:
                {
                    string? error = Displays.SetRefreshRate(c.DeviceName ?? "", c.Width, c.Height, c.Frequency);
                    if (error is not null) return Fail(result, error);
                    break;
                }

                case ChangeKind.DisplayAttach:
                {
                    string? error = Displays.Reattach(c.DeviceName ?? "", c.PositionX, c.PositionY, c.Width, c.Height, c.Frequency, c.BitsPerPixel);
                    if (error is not null) return Fail(result, error);
                    break;
                }

                case ChangeKind.NvidiaSetting:
                {
                    string? error = WinPulse.Collectors.Gpu.NvidiaDrs.Write(c.NvApp, c.NvAppName, c.NvSettingId, c.NvValue);
                    if (error is not null) return Fail(result, error);
                    break;
                }

                case ChangeKind.TaskEnabled:
                {
                    string? error = ScheduledTasks.SetEnabled(c.TaskPath ?? "", c.Data == "1");
                    if (error is not null) return Fail(result, error);
                    break;
                }

                default:
                    return Fail(result, "Unknown change kind");
            }
            result.Success = true;
        }
        catch (UnauthorizedAccessException)
        {
            Fail(result, Lang.T("Нет доступа — нужны права администратора.", "Access denied — administrator rights required."));
        }
        catch (SecurityException)
        {
            Fail(result, Lang.T("Нет доступа — нужны права администратора.", "Access denied — administrator rights required."));
        }
        catch (Exception ex)
        {
            Fail(result, ex.Message);
        }
        return result;
    }

    private static ChangeResult Fail(ChangeResult r, string message)
    {
        r.Success = false;
        r.Error = message;
        return r;
    }
}
