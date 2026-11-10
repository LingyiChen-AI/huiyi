using System.Text.Json;

namespace HuiYi;

public record SessionGroup(string Id, string Name);

public sealed partial class SessionStore
{
    private sealed record OrganizationEntry(string? GroupId = null, bool Archived = false);
    private sealed class Organization
    {
        public required List<SessionGroup> Groups { get; init; }
        public required Dictionary<string, OrganizationEntry> Sessions { get; init; }
    }

    private Organization organization = new() { Groups = [], Sessions = [] };
    private Exception? organizationLoadError;
    private string OrganizationPath => Path.Combine(root, "organization.json");

    public IReadOnlyList<SessionGroup> Groups
    {
        get { lock (gate) return organization.Groups.ToArray(); }
    }

    public string? GroupOf(string sessionId)
    {
        lock (gate)
        {
            if (!sessions.Any(s => s.Id == sessionId)) return null;
            return organization.Sessions.GetValueOrDefault(sessionId)?.GroupId;
        }
    }

    public bool IsArchived(string sessionId)
    {
        lock (gate)
        {
            if (!sessions.Any(s => s.Id == sessionId)) return false;
            return organization.Sessions.GetValueOrDefault(sessionId)?.Archived ?? false;
        }
    }

    public SessionGroup CreateGroup(string name, string? moveMeetingId = null)
    {
        SessionGroup? group = null;
        ChangeOrganization(next =>
        {
            if (moveMeetingId != null) RequireSession(moveMeetingId);
            group = new(Guid.NewGuid().ToString("N"), ValidateGroupName(next, name));
            next.Groups.Add(group);
            if (moveMeetingId != null)
            {
                var entry = next.Sessions.GetValueOrDefault(moveMeetingId) ?? new();
                next.Sessions[moveMeetingId] = entry with { GroupId = group.Id };
            }
        });
        return group!;
    }

    public void RenameGroup(string groupId, string name) => ChangeOrganization(next =>
    {
        var group = RequireGroup(next, groupId);
        next.Groups[next.Groups.IndexOf(group)] = group with { Name = ValidateGroupName(next, name, groupId) };
    });

    public bool GroupHasMeetings(string groupId)
    {
        lock (gate)
            return organization.Groups.Any(g => g.Id == groupId) &&
                organization.Sessions.Values.Any(entry => entry.GroupId == groupId);
    }

    public void DeleteGroup(string groupId) => ChangeOrganization(next =>
    {
        var group = RequireGroup(next, groupId);
        if (next.Sessions.Values.Any(entry => entry.GroupId == groupId))
            throw new InvalidOperationException("A group containing meetings cannot be deleted.");
        next.Groups.Remove(group);
    });
    public void MoveToGroup(string sessionId, string? groupId) => ChangeOrganization(next =>
    {
        RequireSession(sessionId);
        groupId = string.IsNullOrEmpty(groupId) ? null : groupId;
        if (groupId != null) RequireGroup(next, groupId);
        var entry = next.Sessions.GetValueOrDefault(sessionId) ?? new();
        next.Sessions[sessionId] = entry with { GroupId = groupId };
    });

    public void SetArchived(string sessionId, bool archived) => ChangeOrganization(next =>
    {
        RequireSession(sessionId);
        if (archived && LiveId == sessionId)
            throw new InvalidOperationException("The live meeting cannot be archived.");
        var entry = next.Sessions.GetValueOrDefault(sessionId) ?? new();
        next.Sessions[sessionId] = entry with { Archived = archived };
    });

    private void RequireSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !sessions.Any(s => s.Id == sessionId))
            throw new ArgumentException("Unknown meeting.", nameof(sessionId));
    }

    private static SessionGroup RequireGroup(Organization state, string groupId) =>
        state.Groups.FirstOrDefault(g => g.Id == groupId)
        ?? throw new ArgumentException("Unknown group.", nameof(groupId));

    private static string ValidateGroupName(Organization state, string name, string? exceptId = null)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || state.Groups.Any(g => g.Id != exceptId &&
            string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Group names must be nonempty and unique.", nameof(name));
        return trimmed;
    }

    private void LoadOrganization()
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(OrganizationPath)) return;
                var loaded = JsonSerializer.Deserialize<Organization>(File.ReadAllText(OrganizationPath), Json.Options)
                    ?? throw new JsonException("Missing organization.");
                if (loaded.Groups == null || loaded.Sessions == null) throw new JsonException("Missing collections.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in loaded.Groups)
                    if (group == null || string.IsNullOrWhiteSpace(group.Id) || !ids.Add(group.Id) ||
                        string.IsNullOrWhiteSpace(group.Name) || group.Name != group.Name.Trim() || !names.Add(group.Name))
                        throw new JsonException("Invalid group.");
                foreach (var entry in loaded.Sessions.Values)
                    if (entry == null || (entry.GroupId != null && !ids.Contains(entry.GroupId)))
                        throw new JsonException("Invalid meeting organization.");
                // Unreadable session files still own their metadata while their directory exists.
                var folders = Directory.EnumerateDirectories(root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var id in loaded.Sessions.Keys.ToArray())
                    if (!folders.Contains(id)) loaded.Sessions.Remove(id);
                organization = loaded;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                organizationLoadError = error;
                Log.Debug($"store: organization unavailable {error.GetType().Name}");
            }
        }
    }

    private void ChangeOrganization(Action<Organization> change)
    {
        lock (gate)
        {
            var next = CopyOrganization();
            change(next);
            SaveOrganization(next);
        }
        Changed?.Invoke();
    }

    private Organization CopyOrganization() => new()
    {
        Groups = organization.Groups.ToList(),
        Sessions = new(organization.Sessions),
    };

    // Caller holds gate through copying, serialization, replacement and publication.
    private void SaveOrganization(Organization next)
    {
        if (organizationLoadError != null)
            throw new InvalidOperationException("Organization could not be loaded. Preserve and repair organization.json before reopening the store.", organizationLoadError);
        var folders = Directory.EnumerateDirectories(root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in next.Sessions.ToArray())
            if (!folders.Contains(id) || (entry.GroupId == null && !entry.Archived)) next.Sessions.Remove(id);
        var temporary = OrganizationPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Json.Options));
            if (File.Exists(OrganizationPath)) File.Replace(temporary, OrganizationPath, null);
            else File.Move(temporary, OrganizationPath);
            organization = next;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void RemoveOrganization(string sessionId)
    {
        if (!organization.Sessions.ContainsKey(sessionId)) return;
        var next = CopyOrganization();
        next.Sessions.Remove(sessionId);
        SaveOrganization(next);
    }
}
