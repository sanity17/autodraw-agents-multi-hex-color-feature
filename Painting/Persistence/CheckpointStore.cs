namespace Autodraw.Painting.Persistence;

public class Checkpoint
{
    public string SessionId = string.Empty;
    public int StrokeIndex;
    public Recipe Recipe = new();
}

public static class CheckpointStore
{
    public static void Save(string sessionId, int strokeIndex, Recipe recipe) { }
    public static Checkpoint? Load(string sessionId) => null;
    public static void Clear(string sessionId) { }
}
