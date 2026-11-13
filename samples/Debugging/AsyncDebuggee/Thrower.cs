namespace AsyncDebuggee;

/// <summary>An exception thrown after an await (on the thread it resumed on), caught by the caller.</summary>
public static class Thrower
{
    public static async Task FailLaterAsync(string reason)
    {
        await Task.Delay(10);
        var message = "failed " + reason;
        throw new InvalidOperationException(message); // [throw]
    }
}
