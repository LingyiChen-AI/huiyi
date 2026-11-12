using System.Text.Json;

namespace HuiYi;

public sealed partial class SessionStore
{
    public string Import(Session session)
    {
        if (session.EndedAt == null || session.EndedAt < session.StartedAt || session.Lines.Count == 0)
            throw new ArgumentException("A completed transcript is required", nameof(session));
        // Copy before publishing so the decoder cannot keep changing the saved record.
        var copy = JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(session, Json.Options), Json.Options)!;
        copy.Id = Guid.NewGuid().ToString("N");
        var folder = Folder(copy.Id);
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "session.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(copy, Json.Options));
            File.Move(file + ".tmp", file);
        }
        catch
        {
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
        lock (gate)
        {
            sessions.Add(copy);
            sessions.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        }
        Changed?.Invoke();
        return copy.Id;
    }
}
