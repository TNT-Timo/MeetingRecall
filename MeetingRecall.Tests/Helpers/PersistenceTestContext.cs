using Microsoft.Extensions.DependencyInjection;
using MeetingRecall.Infrastructure.DependencyInjection;
using MeetingRecall.Infrastructure.Persistence;

namespace MeetingRecall.Tests.Helpers;

internal sealed class PersistenceTestContext : IDisposable
{
    public PersistenceTestContext(
        Action<IServiceCollection>? configureServices = null)
    {
        RootDirectory = Path.Combine(
            Path.GetTempPath(),
            "MeetingRecall.Tests",
            Guid.NewGuid().ToString("N"));

        var services = new ServiceCollection();
        services.AddMeetingRecallInfrastructure(RootDirectory);
        configureServices?.Invoke(services);
        Services = services.BuildServiceProvider(validateScopes: true);
        Services.GetRequiredService<IDatabaseInitializer>().Initialize();
    }

    public string RootDirectory { get; }

    public ServiceProvider Services { get; }

    public T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();

        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
    }
}
