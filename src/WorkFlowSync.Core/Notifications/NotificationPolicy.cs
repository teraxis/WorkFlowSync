using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Core.Notifications;

/// <summary>What to say after a pass, or nothing at all.</summary>
public sealed record Notification(string Title, string Body, bool IsProblem);

/// <summary>
/// Decides whether a finished pass is worth interrupting the user for, and with what words
/// (docs/plan-etap5.md §5.6b). Pure: no timers, no UI, no clock of its own — so every rule below is
/// a unit test rather than a thing to try out by waiting.
///
/// The governing idea is that a sync tool that talks too much gets muted, and a muted tool cannot warn
/// you when it matters. Hence: one message per pass however many files it carried, nothing at all when a
/// pass changed nothing, and problems that ignore quiet hours.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>
    /// Builds the message for a pass, or null when the pass is not worth mentioning.
    /// </summary>
    /// <param name="added">Files that appeared in the user's folders during this pass.</param>
    /// <param name="errors">Failures the pass logged.</param>
    /// <param name="heldBack">Removals a safeguard refused to carry out — always worth saying.</param>
    /// <param name="localNow">The user's wall clock, for the quiet-hours check.</param>
    public static Notification? ForPass(SyncConfig config, int added, int errors, int heldBack, DateTimeOffset localNow)
    {
        if (config.Notifications == NotifyMode.Off) return null;

        if (errors > 0)
            return new Notification("WorkFlowSync", Plural(errors,
                "Не вдалося обробити один файл", "Не вдалося обробити {0} файли", "Не вдалося обробити {0} файлів") +
                ". Подробиці — у журналі.", IsProblem: true);

        if (heldBack > 0)
            return new Notification("WorkFlowSync",
                $"Видалення ({heldBack}) не перенесено: схоже на збій доступу, а не на ваші дії. Перевірка повториться.",
                IsProblem: true);

        if (config.Notifications == NotifyMode.ErrorsOnly) return null;
        if (added <= 0) return null;                       // a pass that changed nothing has nothing to say
        if (InQuietHours(config, localNow)) return null;   // routine news can wait until morning

        return new Notification("WorkFlowSync", Plural(added,
            "Додано новий файл", "Додано {0} нові файли", "Додано {0} нових файлів"), IsProblem: false);
    }

    /// <summary>True while routine messages are held back. A range may cross midnight; equal bounds mean never.</summary>
    public static bool InQuietHours(SyncConfig config, DateTimeOffset localNow)
    {
        var from = Normalise(config.QuietHoursFrom);
        var to = Normalise(config.QuietHoursTo);
        if (from == to) return false;

        var hour = localNow.Hour;
        return from < to
            ? hour >= from && hour < to          // 9 → 18, an ordinary daytime range
            : hour >= from || hour < to;         // 22 → 8, across midnight
    }

    private static int Normalise(int hour) => hour is >= 0 and <= 23 ? hour : 0;

    /// <summary>Ukrainian counts need three forms: 1 файл, 2 файли, 5 файлів.</summary>
    internal static string Plural(int count, string one, string few, string many)
    {
        var mod100 = count % 100;
        var mod10 = count % 10;
        if (mod100 is >= 11 and <= 14) return string.Format(many, count);
        return mod10 switch
        {
            1 => count == 1 ? one : string.Format(few, count),
            2 or 3 or 4 => string.Format(few, count),
            _ => string.Format(many, count),
        };
    }
}
