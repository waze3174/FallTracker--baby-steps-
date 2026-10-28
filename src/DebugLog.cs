namespace FallTracker;

// debug lines nobody playing needs. they go nowhere unless a debug tool of mine is installed and sets Sink
public static class DebugLog
{
    public static System.Action<string> Sink;

    internal static void Write(string s) => Sink?.Invoke(s);
}
