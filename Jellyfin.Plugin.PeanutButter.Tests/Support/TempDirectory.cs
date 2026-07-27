namespace Jellyfin.Plugin.PeanutButter.Tests.Support;

/// <summary>
/// A uniquely named temporary directory that is removed (best effort) on dispose.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "peanutbutter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leaked temp directories must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
