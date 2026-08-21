namespace MusicEngine.Tests;

/// <summary>One temp directory per test, deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Value { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "musicengine-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Value);

    public void Dispose()
    {
        try { Directory.Delete(Value, true); } catch { }
    }
}
