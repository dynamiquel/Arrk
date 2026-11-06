namespace Arrk.TestSupport;

/// <summary>
/// Locates the repository root from the test output directory, so source-convention tests can read
/// the files they audit without every project re-implementing the walk.
/// </summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) =>
        Path.Combine([Root, .. parts]);

    public static string ReadSource(string relativePath) =>
        File.ReadAllText(Combine(relativePath.Split('/', '\\')));

    static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Arrk.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Arrk.slnx above the test output directory.");
    }
}
