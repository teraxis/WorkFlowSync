using WorkFlowSync.Core;
using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.Execution;
using WorkFlowSync.Core.Logging;
using WorkFlowSync.Core.Model;
using WorkFlowSync.Core.Planning;
using WorkFlowSync.Core.Scanning;
using Xunit;

namespace WorkFlowSync.Tests;

public sealed class GoogleDriveTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = Now.AddDays(-5);

    [Theory]
    [InlineData("document.gdoc", true)]
    [InlineData("sheet.gsheet", true)]
    [InlineData("slides.gslides", true)]
    [InlineData("form.gform", true)]
    [InlineData("drawing.gdraw", true)]
    [InlineData("table.gtable", true)]
    [InlineData("jam.jam", true)]
    [InlineData("map.gmap", true)]
    [InlineData("site.gsite", true)]
    [InlineData("shortcut.gshortcut", true)]
    [InlineData("recording.gvid", true)]
    [InlineData("script.gscript", true)]
    [InlineData("UPPER.GDOC", true)]
    [InlineData(@"subfolder\nested\file.GSHEET", true)]
    [InlineData("report.docx", false)]
    [InlineData("data.xlsx", false)]
    [InlineData("presentation.pptx", false)]
    [InlineData("document.pdf", false)]
    [InlineData("plain.txt", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGoogleDoc_CorrectlyIdentifiesExtensions(string? path, bool expected)
    {
        Assert.Equal(expected, GoogleDrive.IsGoogleDoc(path));
    }

    [Theory]
    [InlineData("document.gdoc", true)]
    [InlineData("sheet.gsheet", true)]
    [InlineData("slides.gslides", true)]
    [InlineData("drawing.gdraw", true)]
    [InlineData("form.gform", false)]
    [InlineData("site.gsite", false)]
    [InlineData("shortcut.gshortcut", false)]
    [InlineData("recording.gvid", false)]
    [InlineData("script.gscript", false)]
    [InlineData("file.txt", false)]
    public void CanExport_IdentifiesExportableDocuments(string path, bool expected)
    {
        Assert.Equal(expected, GoogleDrive.CanExport(path));
    }

    [Fact]
    public void GetTargetRelativePath_TranslatesExtensionsCorrectly()
    {
        // Office mode (default)
        Assert.Equal(@"sub\doc.docx", GoogleDrive.GetTargetRelativePath(@"sub\doc.gdoc", GoogleDocExportMode.Office));
        Assert.Equal(@"data.xlsx", GoogleDrive.GetTargetRelativePath(@"data.gsheet", GoogleDocExportMode.Office));
        Assert.Equal(@"slides.pptx", GoogleDrive.GetTargetRelativePath(@"slides.gslides", GoogleDocExportMode.Office));
        Assert.Equal(@"diagram.png", GoogleDrive.GetTargetRelativePath(@"diagram.gdraw", GoogleDocExportMode.Office));

        // Pdf mode
        Assert.Equal(@"sub\doc.pdf", GoogleDrive.GetTargetRelativePath(@"sub\doc.gdoc", GoogleDocExportMode.Pdf));
        Assert.Equal(@"data.pdf", GoogleDrive.GetTargetRelativePath(@"data.gsheet", GoogleDocExportMode.Pdf));

        // Skip mode
        Assert.Equal(@"sub\doc.gdoc", GoogleDrive.GetTargetRelativePath(@"sub\doc.gdoc", GoogleDocExportMode.Skip));

        // Non-exportable types remain unchanged
        Assert.Equal(@"form.gform", GoogleDrive.GetTargetRelativePath(@"form.gform", GoogleDocExportMode.Office));
    }

    [Fact]
    public void GetExportMimeType_ReturnsExpectedMimeTypes()
    {
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            GoogleDrive.GetExportMimeType("doc.gdoc", GoogleDocExportMode.Office));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            GoogleDrive.GetExportMimeType("sheet.gsheet", GoogleDocExportMode.Office));
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation",
            GoogleDrive.GetExportMimeType("presentation.gslides", GoogleDocExportMode.Office));
        Assert.Equal("image/png",
            GoogleDrive.GetExportMimeType("drawing.gdraw", GoogleDocExportMode.Office));
        Assert.Equal("application/pdf",
            GoogleDrive.GetExportMimeType("doc.gdoc", GoogleDocExportMode.Pdf));
    }

    [Fact]
    public void MapGoogleDocsForExport_ProjectsEntriesAndCreatesTargetMapping()
    {
        var src = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"docs\report.gdoc"] = new ScanEntry { RelativePath = @"docs\report.gdoc", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            [@"data\calc.gsheet"] = new ScanEntry { RelativePath = @"data\calc.gsheet", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            [@"survey.gform"] = new ScanEntry { RelativePath = @"survey.gform", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            [@"readme.txt"] = new ScanEntry { RelativePath = @"readme.txt", Kind = EntryKind.File, Size = 50, MtimeUtc = T1, CtimeUtc = T1 }
        };

        var mapped = GoogleDrive.MapGoogleDocsForExport(src, GoogleDocExportMode.Office, out var map);

        Assert.True(mapped.ContainsKey(@"docs\report.docx"));
        Assert.True(mapped.ContainsKey(@"data\calc.xlsx"));
        Assert.True(mapped.ContainsKey(@"survey.gform")); // non-exportable remains as is
        Assert.True(mapped.ContainsKey(@"readme.txt"));

        Assert.Equal(@"docs\report.gdoc", map[@"docs\report.docx"]);
        Assert.Equal(@"data\calc.gsheet", map[@"data\calc.xlsx"]);
        Assert.False(map.ContainsKey(@"survey.gform"));
    }

    [Fact]
    public void IsGoogleDrivePath_IdentifiesGoogleDrivePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(GoogleDrive.IsGoogleDrivePath(@"C:\Users\test\Google Drive\My Folder"));
            Assert.True(GoogleDrive.IsGoogleDrivePath(@"D:\Backup\Google Drive"));
            Assert.False(GoogleDrive.IsGoogleDrivePath(@"C:\Users\test\Documents"));
        }
    }

    [Fact]
    public void SyncPlanner_SkipsGoogleDoc_WhenModeIsSkip()
    {
        var src = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc.gdoc"] = new ScanEntry { RelativePath = "doc.gdoc", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            ["normal.txt"] = new ScanEntry { RelativePath = "normal.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var dst = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc.gdoc"] = new StateEntry
            {
                PairName = "p",
                RelativePath = "doc.gdoc",
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = T1,
                LastSeenUtc = T1
            }
        };

        var planner = new SyncPlanner("p", Now, backfillFirstSeen: false, googleDocExport: GoogleDocExportMode.Skip);
        var plan = planner.Plan(src, dst, state);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("normal.txt", action.RelativePath);
        Assert.Equal(SyncActionKind.CopyFile, action.Kind);

        Assert.Contains(plan.Warnings, w => w.Contains("doc.gdoc") && w.Contains("native Google document"));
        Assert.Contains("doc.gdoc", plan.Forget);
    }

    [Fact]
    public void SyncPlanner_SkipsGoogleVid_EvenInOfficeMode()
    {
        var src = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [@"meeting\Recording.gvid"] = new ScanEntry { RelativePath = @"meeting\Recording.gvid", Kind = EntryKind.File, Size = 231, MtimeUtc = T1, CtimeUtc = T1 },
            ["normal.txt"] = new ScanEntry { RelativePath = "normal.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var dst = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        var planner = new SyncPlanner("p", Now, backfillFirstSeen: false, googleDocExport: GoogleDocExportMode.Office);
        var plan = planner.Plan(src, dst, state);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("normal.txt", action.RelativePath);
        Assert.Equal(SyncActionKind.CopyFile, action.Kind);

        Assert.Contains(plan.Warnings, w => w.Contains(@"meeting\Recording.gvid") && w.Contains("native Google document"));
        Assert.DoesNotContain(plan.Actions, a => a.RelativePath.Contains("Recording.gvid"));
    }

    [Fact]
    public void SyncPlanner_ExportsGoogleDoc_WhenModeIsOffice()
    {
        var src = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["doc.gdoc"] = new ScanEntry { RelativePath = "doc.gdoc", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            ["normal.txt"] = new ScanEntry { RelativePath = "normal.txt", Kind = EntryKind.File, Size = 100, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var dst = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        var planner = new SyncPlanner("p", Now, backfillFirstSeen: false, googleDocExport: GoogleDocExportMode.Office);
        var plan = planner.Plan(src, dst, state);

        Assert.Equal(2, plan.Actions.Count);
        var docAction = Assert.Single(plan.Actions, a => a.RelativePath == "doc.docx");
        Assert.Equal(SyncActionKind.CopyFile, docAction.Kind);
        Assert.Equal("doc.gdoc", docAction.SourceRelativePath);
        Assert.Equal(SyncSide.Target, docAction.Side);
    }

    [Fact]
    public void TwoWayPlanner_SkipsGoogleDoc_WhenModeIsSkip()
    {
        var a = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.gsheet"] = new ScanEntry { RelativePath = "sheet.gsheet", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            ["data.csv"] = new ScanEntry { RelativePath = "data.csv", Kind = EntryKind.File, Size = 200, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var b = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.gsheet"] = new StateEntry
            {
                PairName = "p",
                RelativePath = "sheet.gsheet",
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = T1,
                LastSeenUtc = T1
            }
        };

        var planner = new TwoWayPlanner("p", Now, googleDocExport: GoogleDocExportMode.Skip);
        var plan = planner.Plan(a, b, state);

        var action = Assert.Single(plan.Actions);
        Assert.Equal("data.csv", action.RelativePath);

        Assert.Contains(plan.Warnings, w => w.Contains("sheet.gsheet") && w.Contains("native Google document"));
        Assert.Contains("sheet.gsheet", plan.Forget);
    }

    [Fact]
    public void TwoWayPlanner_ExportsGoogleDoc_ToDestination_WhenModeIsOffice()
    {
        var a = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.gsheet"] = new ScanEntry { RelativePath = "sheet.gsheet", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 },
            ["data.csv"] = new ScanEntry { RelativePath = "data.csv", Kind = EntryKind.File, Size = 200, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var b = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);

        var planner = new TwoWayPlanner("p", Now, googleDocExport: GoogleDocExportMode.Office);
        var plan = planner.Plan(a, b, state);

        Assert.Equal(2, plan.Actions.Count);
        var sheetAction = Assert.Single(plan.Actions, x => x.RelativePath == "sheet.xlsx");
        Assert.Equal(SyncActionKind.CopyFile, sheetAction.Kind);
        Assert.Equal("sheet.gsheet", sheetAction.SourceRelativePath);
        Assert.Equal(SyncSide.Target, sheetAction.Side);
    }

    [Fact]
    public void TwoWayPlanner_SuppressesReverseSync_ToGoogleDrive()
    {
        var a = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.gsheet"] = new ScanEntry { RelativePath = "sheet.gsheet", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var b = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // Destination file was modified locally with newer timestamp
            ["sheet.xlsx"] = new ScanEntry { RelativePath = "sheet.xlsx", Kind = EntryKind.File, Size = 1500, MtimeUtc = Now, CtimeUtc = T1 }
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.xlsx"] = new StateEntry
            {
                PairName = "p",
                RelativePath = "sheet.xlsx",
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = T1,
                LastSeenUtc = T1,
                SourceSize = 0,
                SourceMtimeUtc = T1,
                CopiedSize = 1200,
                CopiedMtimeUtc = T1
            }
        };

        var planner = new TwoWayPlanner("p", Now, googleDocExport: GoogleDocExportMode.Office);
        var plan = planner.Plan(a, b, state);

        // Reverse sync must be suppressed! No actions should write to Side A (Source)
        Assert.Empty(plan.Actions);
        Assert.Contains(plan.Warnings, w => w.Contains("sheet.xlsx") && w.Contains("suppressed"));
    }

    [Fact]
    public void TwoWayPlanner_UploadsModifiedConvertedDoc_WhenUploadConvertedDocsIsTrue()
    {
        var a = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.gsheet"] = new ScanEntry { RelativePath = "sheet.gsheet", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var b = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            // Destination file was modified locally with newer timestamp
            ["sheet.xlsx"] = new ScanEntry { RelativePath = "sheet.xlsx", Kind = EntryKind.File, Size = 1500, MtimeUtc = Now, CtimeUtc = T1 }
        };
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.xlsx"] = new StateEntry
            {
                PairName = "p",
                RelativePath = "sheet.xlsx",
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = T1,
                LastSeenUtc = T1,
                SourceSize = 0,
                SourceMtimeUtc = T1,
                CopiedSize = 1200,
                CopiedMtimeUtc = T1
            }
        };

        var planner = new TwoWayPlanner("p", Now, googleDocExport: GoogleDocExportMode.Office, uploadConvertedDocs: true);
        var plan = planner.Plan(a, b, state);

        // Reverse sync must be allowed! An action to update Side A (Source) should be planned
        var action = Assert.Single(plan.Actions);
        Assert.Equal(SyncSide.Source, action.Side);
        Assert.Equal(SyncActionKind.UpdateFile, action.Kind);
        Assert.Equal("sheet.xlsx", action.RelativePath);
        Assert.Equal(1500, action.Proposed.CopiedSize);
        Assert.Contains(plan.Warnings, w => w.Contains("sheet.xlsx") && w.Contains("uploading"));
    }

    [Fact]
    public void TwoWayPlanner_SuppressesDeletion_OnGoogleDrive()
    {
        var a = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.gsheet"] = new ScanEntry { RelativePath = "sheet.gsheet", Kind = EntryKind.File, Size = 0, MtimeUtc = T1, CtimeUtc = T1 }
        };
        var b = new Dictionary<string, ScanEntry>(StringComparer.OrdinalIgnoreCase); // Deleted on destination
        var state = new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["sheet.xlsx"] = new StateEntry
            {
                PairName = "p",
                RelativePath = "sheet.xlsx",
                Kind = EntryKind.File,
                Status = EntryStatus.Active,
                FirstSeenUtc = T1,
                LastSeenUtc = T1,
                SourceSize = 0,
                SourceMtimeUtc = T1,
                CopiedSize = 1200,
                CopiedMtimeUtc = T1
            }
        };

        var planner = new TwoWayPlanner("p", Now, googleDocExport: GoogleDocExportMode.Office);
        var plan = planner.Plan(a, b, state);

        // Deletion on Google Drive must be suppressed
        Assert.DoesNotContain(plan.Actions, action => action.Side == SyncSide.Source && action.Kind == SyncActionKind.DeleteFile);
        Assert.Contains(plan.Warnings, w => w.Contains("sheet.xlsx") && w.Contains("suppressed"));
    }

    [Fact]
    public void SyncExecutor_ExportsGoogleDoc_UsingCustomExporter()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wfs-gdoc-export-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(tmp, "src");
        var dst = Path.Combine(tmp, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        try
        {
            File.WriteAllText(Path.Combine(src, "test.gdoc"), "stub pointer");

            var testExporter = new MockGoogleDocExporter(targetPath =>
            {
                File.WriteAllText(targetPath, "exported docx content");
                return new FileInfo(targetPath).Length;
            });

            var executor = new SyncExecutor(
                sourceRoot: src,
                targetRoot: dst,
                log: new MemorySyncLog(),
                dryRun: false,
                googleDocExport: GoogleDocExportMode.Office,
                googleDriveExporter: testExporter);

            var action = new SyncAction(
                SyncActionKind.CopyFile,
                "test.docx",
                new StateEntry
                {
                    PairName = "p",
                    RelativePath = "test.docx",
                    Kind = EntryKind.File,
                    Status = EntryStatus.Active,
                    FirstSeenUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow
                },
                SyncSide.Target,
                SourceRelativePath: "test.gdoc");

            var plan = new SyncPlan();
            plan.Actions.Add(action);

            var result = executor.Execute(plan, pairTag: "[p]");

            Assert.Equal(1, result.FilesCopied);
            Assert.Equal(0, result.Errors);
            Assert.True(File.Exists(Path.Combine(dst, "test.docx")));
            Assert.Equal("exported docx content", File.ReadAllText(Path.Combine(dst, "test.docx")));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SyncExecutor_HandlesMissingAuthToken_Gracefully()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wfs-gdoc-auth-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(tmp, "src");
        var dst = Path.Combine(tmp, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);

        try
        {
            File.WriteAllText(Path.Combine(src, "test.gdoc"), "stub pointer");

            // Real exporter without access token
            var exporter = new GoogleDriveExporter(GoogleDocExportMode.Office, auth: null);

            var executor = new SyncExecutor(
                sourceRoot: src,
                targetRoot: dst,
                log: new MemorySyncLog(),
                dryRun: false,
                googleDocExport: GoogleDocExportMode.Office,
                googleDriveExporter: exporter);

            var action = new SyncAction(
                SyncActionKind.CopyFile,
                "test.docx",
                new StateEntry
                {
                    PairName = "p",
                    RelativePath = "test.docx",
                    Kind = EntryKind.File,
                    Status = EntryStatus.Active,
                    FirstSeenUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow
                },
                SyncSide.Target,
                SourceRelativePath: "test.gdoc");

            var plan = new SyncPlan();
            plan.Actions.Add(action);

            var result = executor.Execute(plan, pairTag: "[p]");

            // Must skip without crashing or throwing
            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Errors);
            Assert.False(File.Exists(Path.Combine(dst, "test.docx")));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void TwoWay_SyncsRegularFiles_AndIgnoresGoogleDocs_WhenModeIsSkip()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wfs-gdoc-e2e-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(tmp, "a");
        var b = Path.Combine(tmp, "b");
        var cfgPath = Path.Combine(tmp, "config.json");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        try
        {
            var config = new SyncConfig
            {
                Pairs = { new FolderPair { Name = "gd-test", Source = a, Target = b, Mode = SyncMode.TwoWay, GoogleDocExport = GoogleDocExportMode.Skip } },
                ScanParallelism = 1
            };
            ConfigFile.Save(config, cfgPath);

            File.WriteAllText(Path.Combine(a, "doc.gdoc"), "pointer");
            File.WriteAllText(Path.Combine(a, "real.txt"), "hello world");
            File.WriteAllText(Path.Combine(b, "sheet.gsheet"), "pointer2");
            File.WriteAllText(Path.Combine(b, "data.csv"), "1,2,3");

            var runner = new SyncRunner(config, cfgPath, new MemorySyncLog());
            var res = runner.Run(dryRun: false);

            Assert.Equal(0, res.Errors);
            Assert.True(File.Exists(Path.Combine(b, "real.txt")));
            Assert.True(File.Exists(Path.Combine(a, "data.csv")));
            Assert.False(File.Exists(Path.Combine(b, "doc.gdoc")));
            Assert.False(File.Exists(Path.Combine(a, "sheet.gsheet")));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class MockGoogleDocExporter : IGoogleDriveExporter
    {
        private readonly Func<string, long> _writeTarget;

        public MockGoogleDocExporter(Func<string, long> writeTarget)
        {
            _writeTarget = writeTarget;
        }

        public ExportResult Export(string sourcePath, string targetPath, DateTimeOffset? mtime, CancellationToken ct = default)
        {
            var len = _writeTarget(targetPath);
            return new ExportResult(true, len, mtime ?? DateTimeOffset.UtcNow);
        }
    }
}
