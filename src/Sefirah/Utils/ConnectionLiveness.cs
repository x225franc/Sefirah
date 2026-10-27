namespace Sefirah.Utils;

/// <summary>One idle probe at a time, based on monotonic time rather than the wall clock.</summary>
public sealed class ConnectionLiveness
{
    private readonly object gate = new();
    private long lastReceived;
    private long? probeSent;

    public void Received(long now)
    {
        lock (gate) { lastReceived = now; probeSent = null; }
    }

    public LivenessAction Check(long now)
    {
        lock (gate)
        {
            if (probeSent is long sent)
                return now - sent >= 60_000 ? LivenessAction.Disconnect : LivenessAction.None;
            if (now - lastReceived < 60_000) return LivenessAction.None;
            probeSent = now;
            return LivenessAction.Ping;
        }
    }
}

public enum LivenessAction { None, Ping, Disconnect }
