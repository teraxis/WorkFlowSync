using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.State;

namespace WorkFlowSync.Tests;

public class ForgetTests
{
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
