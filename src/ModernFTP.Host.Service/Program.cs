using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModernFTP.Host.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceSettings.ServiceName);
        builder.Services.AddSingleton(ServiceSettings.FromArgs(args));
        builder.Services.AddHostedService<FtpWorker>();

        // Lifecycle messages and errors go to the Windows Event Log; every FTP event goes to the text log.
        builder.Logging.ClearProviders();
        builder.Logging.AddEventLog(settings => settings.SourceName = ServiceSettings.ServiceName);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return Environment.ExitCode;
    }
}
