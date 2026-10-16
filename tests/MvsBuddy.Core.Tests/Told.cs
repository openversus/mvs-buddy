namespace MvsBuddy.Tests;

/// <summary>Progress lines as reported, in order, on the reporting thread (Progress&lt;T&gt; would post them elsewhere).</summary>
internal sealed class Told : IProgress<string>
{
    private readonly List<string> lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
            {
                return lines.ToList();
            }
        }
    }

    public void Report(string value)
    {
        lock (lines)
        {
            lines.Add(value);
        }
    }
}
