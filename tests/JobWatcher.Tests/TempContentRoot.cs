using Microsoft.Extensions.Hosting;

namespace JobWatcher.Tests;

/// <summary>A host builder whose content root is a fresh folder, as the application's own folder is for the real one.</summary>
internal static class TempContentRoot
{
    /// <summary>
    /// Runs <paramref name="test"/> with a host builder whose content root holds <c>appsettings.Local.json</c> with the
    /// given text (no such file when it is null), and removes the folder afterwards.
    /// </summary>
    public static void WithLocalFile(string? localJson, Action<HostApplicationBuilder> test)
    {
        var folder = Directory.CreateTempSubdirectory("jobwatcher-local-");
        try
        {
            if (localJson is not null)
            {
                File.WriteAllText(Path.Combine(folder.FullName, "appsettings.Local.json"), localJson);
            }

            test(Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = folder.FullName, Args = [] }));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
