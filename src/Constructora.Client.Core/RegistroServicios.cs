using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Constructora.Client.Core;

public static class RegistroServicios
{
    public static IServiceCollection AddConstructoraClient(this IServiceCollection services, Uri apiBaseUri, string sqlitePath)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (!apiBaseUri.IsAbsoluteUri || apiBaseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("La API necesita una URL absoluta HTTP o HTTPS.", nameof(apiBaseUri));
        var path = Path.GetFullPath(sqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnectionStringBuilder
        { DataSource = path, ForeignKeys = true, DefaultTimeout = 10 }.ToString();
        services.AddDbContextFactory<SqliteLocalDbContext>(options => options.UseSqlite(connection));
        services.AddHttpClient("Constructora", client =>
        {
            client.BaseAddress = new Uri(apiBaseUri.AbsoluteUri.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<ISincronizadorService>(provider => new SincronizadorService(
            provider.GetRequiredService<IDbContextFactory<SqliteLocalDbContext>>(),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Constructora"),
            provider.GetRequiredService<ILogger<SincronizadorService>>()));
        return services;
    }
}
