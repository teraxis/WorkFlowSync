using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

/// <summary>
/// The «recently appeared» list behind the tray window (docs/plan-etap5.md §5.6). It is a query over the
/// state, not a second log: `first_seen` is written once and never changes (invariant 5), so it already
/// answers the question exactly and there is no copy of the truth to keep in step.
/// </summary>
public class RecentChangesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfs-recent-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly DateTimeOffset _now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    public RecentChangesTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StateStore(Path.Combine(_dir, "state.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private StateEntry Row(string pair, string path, DateTimeOffset firstSeen, EntryKind kind = EntryKind.File,
        EntryStatus status = EntryStatus.Active, long size = 100) => new()
    {
        PairName = pair, RelativePath = path, Kind = kind, Status = status,
        FirstSeenUtc = firstSeen, LastSeenUtc = firstSeen,
        SourceSize = kind == EntryKind.File ? size : null,
    };

    [Fact]
    public void The_newest_files_come_first()
    {
        _store.Upsert(new[]
        {
            Row("vrp", @"Засідання\старий.docx", _now.AddDays(-3)),
            Row("vrp", @"Засідання\найновіший.docx", _now),
            Row("vrp", @"Засідання\вчорашній.docx", _now.AddDays(-1)),
        });

        var recent = _store.RecentlyAdded();

        Assert.Equal(new[] { @"Засідання\найновіший.docx", @"Засідання\вчорашній.docx", @"Засідання\старий.docx" },
            recent.Select(r => r.RelativePath));
        Assert.Equal(100, recent[0].Size);
        Assert.Equal("vrp", recent[0].Pair);
    }

    [Fact]
    public void Folders_are_left_out_of_a_list_of_new_documents()
    {
        _store.Upsert(new[]
        {
            Row("vrp", @"Нова тека", _now, EntryKind.Directory),
            Row("vrp", @"Нова тека\документ.docx", _now),
        });

        Assert.Equal(@"Нова тека\документ.docx", Assert.Single(_store.RecentlyAdded()).RelativePath);
    }

    [Fact]
    public void What_the_user_deleted_or_changed_locally_is_not_announced_as_new()
    {
        _store.Upsert(new[]
        {
            Row("vrp", "видалений.docx", _now, status: EntryStatus.Tombstone),
            Row("vrp", "змінений.docx", _now, status: EntryStatus.LocalModified),
            Row("vrp", "новий.docx", _now),
        });

        Assert.Equal("новий.docx", Assert.Single(_store.RecentlyAdded()).RelativePath);
    }

    [Fact]
    public void Every_pair_is_shown_together()
    {
        _store.Upsert(new[] { Row("vrp", "а.docx", _now), Row("робота", "б.docx", _now.AddMinutes(-1)) });

        var recent = _store.RecentlyAdded();

        Assert.Equal(2, recent.Count);
        Assert.Equal(new[] { "vrp", "робота" }, recent.Select(r => r.Pair));
    }

    [Fact]
    public void The_list_is_capped_so_a_million_rows_cannot_reach_the_window()
    {
        _store.Upsert(Enumerable.Range(0, 300).Select(i => Row("vrp", $"файл{i:D3}.docx", _now.AddMinutes(-i))));

        Assert.Equal(10, _store.RecentlyAdded(10).Count);
        Assert.Equal(300, _store.RecentlyAdded(1000).Count);
        Assert.Single(_store.RecentlyAdded(0));                  // clamped, never zero or negative
    }

    [Fact]
    public void Only_what_appeared_after_a_given_moment_is_counted()
    {
        _store.Upsert(new[]
        {
            Row("vrp", "давній.docx", _now.AddHours(-2)),
            Row("vrp", "свіжий.docx", _now.AddMinutes(-5)),
            Row("vrp", "щойно.docx", _now),
        });

        Assert.Equal(2, _store.CountAddedSince(_now.AddMinutes(-30)));
        Assert.Equal(@"свіжий.docx", _store.RecentlyAdded(since: _now.AddMinutes(-30)).Last().RelativePath);
        Assert.Equal(0, _store.CountAddedSince(_now.AddMinutes(5)));
    }

    [Fact]
    public void An_empty_state_answers_with_an_empty_list_rather_than_failing()
    {
        Assert.Empty(_store.RecentlyAdded());
        Assert.Equal(0, _store.CountAddedSince(_now.AddYears(-1)));
    }
}
