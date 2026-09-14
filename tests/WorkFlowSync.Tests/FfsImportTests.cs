using System.Xml.Linq;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Ffs;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public class FfsImportTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <FreeFileSync XmlType="BATCH" XmlFormat="23">
            <Compare><Variant>TimeAndSize</Variant><Symlinks>Follow</Symlinks></Compare>
            <Filter>
                <Include><Item>*</Item></Include>
                <Exclude>
                    <Item>\Друга дисциплінарна палата\Говорилка 22-11-2023.docx</Item>
                    <Item>\Друга дисциплінарна палата\2025_рік</Item>
                    <Item>\Друга дисциплінарна палата\*Робочий.docx</Item>
                    <Item>*.tmp</Item>
                    <Item>\Друга дисциплінарна палата\Говорилка 22-11-2023.docx</Item>
                </Exclude>
            </Filter>
            <FolderPairs>
                <Pair>
                    <Left>E:\vrp\</Left>
                    <Right>E:\OneDrive\Робоча папка\</Right>
                    <Filter><Exclude><Item>\Тимчасове</Item></Exclude></Filter>
                </Pair>
            </FolderPairs>
        </FreeFileSync>
        """;

    [Fact]
    public void Parses_pairs_and_excludes()
    {
        var batch = FfsBatch.Parse(XDocument.Parse(Sample));
        Assert.Equal(5, batch.GlobalExcludes.Count);
        var pair = Assert.Single(batch.Pairs);
        Assert.Equal(@"E:\vrp\", pair.Left);
        Assert.Equal(@"\Тимчасове", Assert.Single(pair.Excludes));
    }

    [Fact]
    public void Matches_config_pair_by_source_ignoring_trailing_slash_and_case()
    {
        var batch = FfsBatch.Parse(XDocument.Parse(Sample));
        var cfg = new SyncConfig { Pairs = { new FolderPair { Name = "vrp", Source = @"e:\VRP", Target = @"D:\x" } } };
        Assert.Same(cfg.Pairs[0], FfsBatchImporter.MatchPair(cfg, batch.Pairs[0]));
        Assert.Null(FfsBatchImporter.MatchPair(new SyncConfig(), batch.Pairs[0]));
    }

    [Fact]
    public void Wildcards_become_patterns_and_paths_become_tombstones()
    {
        var batch = FfsBatch.Parse(XDocument.Parse(Sample));
        var pair = new FolderPair { Name = "vrp", Source = @"Q:\does-not-exist", Target = @"D:\x", Exclude = { "*.tmp" } };
        var existing = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Друга дисциплінарна палата\2025_рік"] = new StateEntry
            {
                PairName = "vrp", RelativePath = @"Друга дисциплінарна палата\2025_рік", Kind = EntryKind.Directory,
                Status = EntryStatus.Active, FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch,
            },
        };

        var r = FfsBatchImporter.Prepare(batch, batch.Pairs[0], pair, existing, DateTimeOffset.UtcNow, probeSource: true);

        Assert.Equal(1, r.Patterns);                      // *Робочий.docx
        Assert.Equal(1, r.PatternsAlreadyPresent);        // *.tmp
        Assert.Contains(@"\Друга дисциплінарна палата\*Робочий.docx", pair.Exclude);
        Assert.Equal(3, r.Tombstones);                    // Говорилка (dup collapsed), 2025_рік (converted), Тимчасове
        Assert.Equal(1, r.ActiveConverted);
        Assert.Equal(0, r.Probed);                        // source missing → no stat calls
        var govorylka = r.Rows.Single(x => x.RelativePath.EndsWith("Говорилка 22-11-2023.docx"));
        Assert.Equal(EntryKind.File, govorylka.Kind);
        Assert.Equal(EntryStatus.Tombstone, govorylka.Kind == EntryKind.File ? govorylka.Status : EntryStatus.Active);
        var tymchasove = r.Rows.Single(x => x.RelativePath == "Тимчасове");
        Assert.Equal(EntryKind.Directory, tymchasove.Kind);
    }

    [Fact]
    public void Forget_removes_a_subtree_case_insensitively()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wfs-forget-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new StateStore(path);
            StateEntry Row(string p) => new() { PairName = "t", RelativePath = p, Kind = EntryKind.File, Status = EntryStatus.Tombstone, FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch };
            store.Upsert(new[] { Row(@"Архів\a.txt"), Row(@"Архів\b\c.txt"), Row(@"Архівний\z.txt"), Row("top.txt") });

            Assert.Equal(2, store.DeleteSubtree("t", @"\архів\"));
            var left = store.Load("t").Keys.OrderBy(k => k).ToArray();
            Assert.Equal(new[] { @"Архівний\z.txt", "top.txt" }, left);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
