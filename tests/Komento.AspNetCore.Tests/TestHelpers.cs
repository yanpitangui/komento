namespace Komento.AspNetCore.Tests;

internal static class TestHelpers
{
    internal static string ResolveContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir?.Parent != null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return Path.GetFullPath(Path.Combine(dir.FullName, "tests/Komento.AspNetCore.Tests"));
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
