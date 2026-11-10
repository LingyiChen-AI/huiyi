namespace HuiYi.Tests;

public sealed class SessionOrganizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HuiYi-organization-" + Guid.NewGuid().ToString("N"));
    private SessionStore Open() => new(root);
    private static string Meeting(SessionStore store)
    {
        var session = store.Begin();
        session.Lines.Add(new() { Src = "meeting" });
        store.End();
        return session.Id;
    }

    [Fact]
    public void FolderCaseDifferencesPreserveOrganizationOnWindows()
    {
        var folder = Path.Combine(root, "meeting-a");
        Directory.CreateDirectory(folder);
        var session = new Session { Id = "MEETING-A", Title = "Imported meeting", StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow, Lines = [new() { Src = "Imported words" }] };
        File.WriteAllText(Path.Combine(folder, "session.json"), System.Text.Json.JsonSerializer.Serialize(session, Json.Options));
        var store = Open();
        var group = store.CreateGroup("Imported");
        store.MoveToGroup(session.Id, group.Id);
        store.SetArchived(session.Id, true);
        var loaded = Open();
        Assert.Equal(group.Id, loaded.GroupOf(session.Id));
        Assert.True(loaded.IsArchived(session.Id));
    }

    [Fact]
    public void CreatingGroupCanMoveMeetingAtomicallyAndPreserveArchive()
    {
        var store = Open();
        var id = Meeting(store);
        store.SetArchived(id, true);
        var group = store.CreateGroup("New project", id);
        Assert.Equal(group.Id, Open().GroupOf(id));
        Assert.True(Open().IsArchived(id));
        Assert.Throws<ArgumentException>(() => store.CreateGroup("Invalid target", "missing"));
        Assert.Single(Open().Groups);
    }

    [Fact]
    public void GroupsAndArchiveRoundTripWithoutChangingSessionProtocol()
    {
        var store = Open();
        var id = Meeting(store);
        var original = File.ReadAllText(Path.Combine(root, id, "session.json"));
        var changed = 0;
        store.Changed += () => changed++;
        var group = store.CreateGroup("  Work  ");
        Assert.Equal("Work", group.Name);
        store.RenameGroup(group.Id, " Projects ");
        store.MoveToGroup(id, group.Id);
        store.SetArchived(id, true);
        Assert.Equal(4, changed);
        var loaded = Open();
        Assert.Equal("Projects", Assert.Single(loaded.Groups).Name);
        Assert.Equal(group.Id, loaded.GroupOf(id));
        Assert.True(loaded.IsArchived(id));
        loaded.SetArchived(id, false);
        Assert.Equal(group.Id, Open().GroupOf(id));
        Assert.False(Open().IsArchived(id));
        loaded.SetArchived(id, true);
        loaded.MoveToGroup(id, null);
        loaded.DeleteGroup(group.Id);
        Assert.Null(Open().GroupOf(id));
        Assert.True(Open().IsArchived(id));
        Assert.Empty(Open().Groups);
        Assert.Equal(original, File.ReadAllText(Path.Combine(root, id, "session.json")));
    }

    [Fact]
    public void OldSessionsDefaultToUnorganizedAndEmptyGroupNormalizes()
    {
        var store = Open();
        var id = Meeting(store);
        store = Open();
        Assert.Empty(store.Groups);
        Assert.Null(store.GroupOf(id));
        Assert.False(store.IsArchived(id));
        var group = store.CreateGroup("Work");
        store.MoveToGroup(id, group.Id);
        store.MoveToGroup(id, "");
        Assert.Null(Open().GroupOf(id));
        store.MoveToGroup(id, null);
    }

    [Fact]
    public void InvalidIdsNamesAndLiveArchiveAreRejected()
    {
        var store = Open();
        var id = store.Begin().Id;
        var group = store.CreateGroup("Work");
        var other = store.CreateGroup("Other");
        Assert.Throws<InvalidOperationException>(() => store.SetArchived(id, true));
        Assert.Throws<ArgumentException>(() => store.CreateGroup(" work "));
        Assert.Throws<ArgumentException>(() => store.CreateGroup("  "));
        Assert.Throws<ArgumentException>(() => store.RenameGroup(other.Id, "WORK"));
        Assert.Throws<ArgumentException>(() => store.RenameGroup(group.Id, " "));
        Assert.Throws<ArgumentException>(() => store.RenameGroup("missing", "Name"));
        Assert.Throws<ArgumentException>(() => store.DeleteGroup("missing"));
        Assert.Throws<ArgumentException>(() => store.MoveToGroup(id, "missing"));
        Assert.Throws<ArgumentException>(() => store.MoveToGroup("missing", group.Id));
        Assert.Throws<ArgumentException>(() => store.SetArchived("missing", true));
        Assert.Null(store.GroupOf("missing"));
        Assert.False(store.IsArchived("missing"));
        Assert.Throws<ArgumentException>(() => store.Delete("../outside"));
        Assert.False(store.IsArchived(id));
    }

    [Fact]
    public void FailedSaveLeavesMemoryDiskAndEventsUntouched()
    {
        var store = Open();
        var id = Meeting(store);
        var group = store.CreateGroup("Work");
        var path = Path.Combine(root, "organization.json");
        var original = File.ReadAllText(path);
        var changed = 0;
        store.Changed += () => changed++;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.MoveToGroup(id, group.Id));
            Assert.ThrowsAny<IOException>(() => store.CreateGroup("Other"));
            Assert.ThrowsAny<IOException>(() => store.RenameGroup(group.Id, "Renamed"));
            Assert.ThrowsAny<IOException>(() => store.DeleteGroup(group.Id));
            Assert.ThrowsAny<IOException>(() => store.SetArchived(id, true));
            Assert.False(store.IsArchived(id));
            Assert.Equal("Work", Assert.Single(store.Groups).Name);
            Assert.Null(store.GroupOf(id));
            Assert.Single(store.Groups);
        }
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(0, changed);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"groups\":[],\"sessions\":{\"missing\":{\"groupId\":\"orphan\"}}}")]
    public void CorruptOrganizationIsPreservedAndWritesAreBlocked(string contents)
    {
        var store = Open();
        var id = Meeting(store);
        var path = Path.Combine(root, "organization.json");
        File.WriteAllText(path, contents);
        store = Open();
        Assert.NotNull(store.Get(id));
        Assert.False(store.IsArchived(id));
        Assert.Throws<InvalidOperationException>(() => store.CreateGroup("Work"));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void ConcurrentWritesPersistEveryChange()
    {
        var store = Open();
        var ids = Enumerable.Range(0, 24).Select(_ => Meeting(store)).ToArray();
        var group = store.CreateGroup("Work");
        Parallel.ForEach(ids, id => { store.MoveToGroup(id, group.Id); store.SetArchived(id, true); });
        var loaded = Open();
        foreach (var id in ids)
        {
            Assert.Equal(group.Id, loaded.GroupOf(id));
            Assert.True(loaded.IsArchived(id));
        }
    }

    [Fact]
    public void DeleteCleansOnlyTheDeletedMeetingsMetadata()
    {
        var store = Open();
        var first = Meeting(store);
        var second = Meeting(store);
        var group = store.CreateGroup("Work");
        foreach (var id in new[] { first, second })
        {
            store.MoveToGroup(id, group.Id);
            store.SetArchived(id, true);
        }
        store.Delete(first);
        var loaded = Open();
        Assert.Null(loaded.Get(first));
        Assert.Equal(group.Id, loaded.GroupOf(second));
        Assert.True(loaded.IsArchived(second));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "organization.json")));
        Assert.False(json.RootElement.GetProperty("sessions").TryGetProperty(first, out _));
    }

    [Fact]
    public void DeleteSaveFailureRecoversStaleMetadataOnReload()
    {
        var store = Open();
        var id = Meeting(store);
        var group = store.CreateGroup("Work");
        store.MoveToGroup(id, group.Id);
        store.SetArchived(id, true);
        var path = Path.Combine(root, "organization.json");
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => store.Delete(id));
        Assert.Null(store.Get(id));
        Assert.Null(store.GroupOf(id));
        Assert.False(store.IsArchived(id));
        var loaded = Open();
        Assert.Null(loaded.Get(id));
        Assert.Single(loaded.Groups);
        loaded.RenameGroup(group.Id, "Renamed");
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Empty(json.RootElement.GetProperty("sessions").EnumerateObject());
    }

    [Fact]
    public void SearchCanIncludeOrExcludeArchivedMeetings()
    {
        var store = Open();
        var active = Meeting(store);
        var archived = Meeting(store);
        store.SetArchived(archived, true);
        var matches = store.All.Where(s => SessionSearch.Matches(s, "meeting")).ToArray();
        Assert.Equal(2, matches.Length);
        Assert.Equal(active, Assert.Single(matches.Where(s => !store.IsArchived(s.Id))).Id);
    }

    [Fact]
    public void EndingAnEmptyMeetingCleansItsGroupMetadata()
    {
        var store = Open();
        var id = store.Begin().Id;
        var group = store.CreateGroup("Work");
        store.MoveToGroup(id, group.Id);
        store.End();
        Assert.Null(store.Get(id));
        Assert.Null(store.GroupOf(id));
        Assert.False(store.IsArchived(id));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "organization.json")));
        Assert.False(json.RootElement.GetProperty("sessions").TryGetProperty(id, out _));
    }
    [Fact]
    public void LiveMeetingCannotBeDeletedAndRemainsUnchanged()
    {
        var store = Open();
        var session = store.Begin();
        var group = store.CreateGroup("Work");
        store.MoveToGroup(session.Id, group.Id);
        store.SaveNow(session.Id);
        var path = Path.Combine(root, "organization.json");
        var original = File.ReadAllText(path);
        var changed = 0;
        store.Changed += () => changed++;
        Assert.Throws<InvalidOperationException>(() => store.Delete(session.Id));
        Assert.Same(session, store.Get(session.Id));
        Assert.Equal(session.Id, store.LiveId);
        Assert.Equal(group.Id, store.GroupOf(session.Id));
        Assert.True(File.Exists(Path.Combine(root, session.Id, "session.json")));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task SnapshotReadsRemainSafeAfterConcurrentEmptyMeetingEnd()
    {
        var store = Open();
        var id = store.Begin().Id;
        var group = store.CreateGroup("Work");
        store.MoveToGroup(id, group.Id);
        var snapshot = store.All;
        await Task.Run(() => store.End());
        var stale = Assert.Single(snapshot);
        Assert.Null(store.GroupOf(stale.Id));
        Assert.False(store.IsArchived(stale.Id));
        Assert.Null(store.GroupOf(""));
        Assert.False(store.IsArchived(""));
        Assert.Throws<ArgumentException>(() => store.MoveToGroup(stale.Id, group.Id));
        Assert.Throws<ArgumentException>(() => store.SetArchived(stale.Id, false));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableMeetingPreservesOrganizationThroughOtherGroupChanges(bool locked)
    {
        var store = Open();
        var id = Meeting(store);
        var group = store.CreateGroup("Work");
        store.MoveToGroup(id, group.Id);
        store.SetArchived(id, true);
        var path = Path.Combine(root, id, "session.json");
        var original = File.ReadAllText(path);
        FileStream? heldFile = null;
        try
        {
            if (locked) heldFile = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            else File.WriteAllText(path, "{broken");
            store = Open();
            Assert.Null(store.Get(id));
            Assert.True(store.GroupHasMeetings(group.Id));
            Assert.Throws<InvalidOperationException>(() => store.DeleteGroup(group.Id));
            var other = store.CreateGroup("Other");
            store.RenameGroup(other.Id, "Renamed");
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "organization.json")));
            var entry = json.RootElement.GetProperty("sessions").GetProperty(id);
            Assert.Equal(group.Id, entry.GetProperty("groupId").GetString());
            Assert.True(entry.GetProperty("archived").GetBoolean());
        }
        finally
        {
            heldFile?.Dispose();
            File.WriteAllText(path, original);
        }
        var restored = Open();
        Assert.NotNull(restored.Get(id));
        Assert.Equal(group.Id, restored.GroupOf(id));
        Assert.True(restored.IsArchived(id));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupsWithMeetingsCannotBeDeletedUntilMovedOut(bool archived)
    {
        var store = Open();
        var id = Meeting(store);
        var group = store.CreateGroup("Work", id);
        store.SetArchived(id, archived);
        var path = Path.Combine(root, "organization.json");
        var original = File.ReadAllText(path);
        var changed = 0;
        store.Changed += () => changed++;
        Assert.True(store.GroupHasMeetings(group.Id));
        Assert.Throws<InvalidOperationException>(() => store.DeleteGroup(group.Id));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Single(store.Groups);
        Assert.Equal(group.Id, store.GroupOf(id));
        Assert.Equal(archived, store.IsArchived(id));
        Assert.Equal(0, changed);
        store.MoveToGroup(id, null);
        Assert.False(store.GroupHasMeetings(group.Id));
        store.DeleteGroup(group.Id);
        Assert.Equal(2, changed);
        Assert.Empty(Open().Groups);
        Assert.NotNull(Open().Get(id));
        Assert.Equal(archived, Open().IsArchived(id));
        Assert.False(store.GroupHasMeetings(group.Id));
        Assert.False(store.GroupHasMeetings("missing"));
        Assert.False(store.GroupHasMeetings(""));
    }

    [Fact]
    public void EmptyGroupCanBeDeletedAndNotifiesAfterPersistence()
    {
        var store = Open();
        var group = store.CreateGroup("Empty");
        Assert.False(store.GroupHasMeetings(group.Id));
        var changed = 0;
        store.Changed += () => { changed++; Assert.Empty(Open().Groups); };
        store.DeleteGroup(group.Id);
        Assert.Empty(store.Groups);
        Assert.Equal(1, changed);
    }
    public void Dispose() => Directory.Delete(root, true);
}
