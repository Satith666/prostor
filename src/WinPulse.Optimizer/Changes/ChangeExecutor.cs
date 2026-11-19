using WinPulse.Collectors;
using WinPulse.Core;

namespace WinPulse.Optimizer.Changes;

/// <summary>
/// Применяет пачку изменений:
/// 1) сохраняет исходные значения в резервную копию (до любого изменения);
/// 2) изменения без прав администратора — сразу;
/// 3) изменения HKLM и powercfg — одним запуском с правами администратора;
/// 4) итог по каждому изменению: получилось или нет и почему.
/// </summary>
public sealed class ChangeExecutor
{
    private readonly BackupStore _backup;

    public ChangeExecutor(BackupStore backup) => _backup = backup;

    public BackupStore Backup => _backup;

    public async Task<List<ChangeResult>> ApplyAsync(IReadOnlyList<Change> changes)
    {
        var results = new ChangeResult?[changes.Count];
        var freshTargets = new HashSet<string>();

        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.IsRevert) continue;
            try
            {
                var target = c.TargetId;
                bool created = await Task.Run(() => _backup.SaveOriginal(c));
                if (created) freshTargets.Add(target);
            }
            catch (Exception ex)
            {
                results[i] = Fail(i, c, Lang.T("Не удалось сохранить резервную копию, поэтому изменение не применялось: ",
                    "Could not save the backup, so the change was not applied: ") + ex.Message);
            }
        }

        bool elevated = OsInfo.IsElevated();
        var adminIndexes = new List<int>();
        for (int i = 0; i < changes.Count; i++)
        {
            if (results[i] is not null) continue;
            var c = changes[i];
            if (c.NeedsAdmin && !elevated)
            {
                adminIndexes.Add(i);
                continue;
            }
            int index = i;
            results[i] = await Task.Run(() => ChangeApplier.Apply(c, index));
        }

        if (adminIndexes.Count > 0)
        {
            var batch = adminIndexes.Select(i => changes[i]).ToList();
            List<ChangeResult>? elevatedResults = null;
            string? failure = null;
            try
            {
                elevatedResults = await ElevatedRunner.RunAsync(batch);
                if (elevatedResults is null)
                    failure = Lang.T("Права администратора не выданы — изменение не применено.",
                        "Administrator rights were not granted — the change was not applied.");
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            for (int k = 0; k < adminIndexes.Count; k++)
            {
                int i = adminIndexes[k];
                var r = elevatedResults?.FirstOrDefault(x => x.Index == k);
                results[i] = r is null
                    ? Fail(i, changes[i], failure ?? Lang.T("Нет результата от вспомогательного процесса", "No result from the helper process"))
                    : new ChangeResult { Index = i, Success = r.Success, Error = r.Error, CreatedSchemeGuid = r.CreatedSchemeGuid };
            }
        }

        var list = new List<ChangeResult>(changes.Count);
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            var r = results[i]!;
            r.Index = i;
            r.Change = c;
            list.Add(r);

            try
            {
                if (!r.Success && freshTargets.Contains(c.TargetId))
                {
                    // Ничего не поменялось — резервная копия этой цели не нужна.
                    _backup.Remove(c.TargetId);
                }
                if (r.Success && c.Kind == ChangeKind.PowerActivate && !c.IsRevert && r.CreatedSchemeGuid is not null)
                {
                    var entry = _backup.Get(c.TargetId);
                    if (entry is not null)
                    {
                        entry.CreatedSchemeGuid = r.CreatedSchemeGuid;
                        _backup.Update(entry);
                    }
                }
                if (r.Success && c.RevertTargetId is not null)
                {
                    _backup.Remove(c.RevertTargetId);
                }
            }
            catch (Exception ex)
            {
                Diag.Report("backup-update", ex);
            }
        }
        return list;
    }

    private static ChangeResult Fail(int index, Change c, string error) =>
        new() { Index = index, Change = c, Success = false, Error = error };
}
