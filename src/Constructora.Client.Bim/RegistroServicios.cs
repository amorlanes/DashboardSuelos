using Constructora.Application.Bim;
using Constructora.Client.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Constructora.Client.Bim;

public static class RegistroServiciosBim
{
    public static IServiceCollection AddConstructoraBim(this IServiceCollection services, string sqlitePath, string archivoDir)
    {
        var path = Path.GetFullPath(sqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnectionStringBuilder
        { DataSource = path, ForeignKeys = true, DefaultTimeout = 10 }.ToString();
        services.AddDbContextFactory<BimLocalDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IBimService>(provider => new BimService(
            provider.GetRequiredService<IDbContextFactory<BimLocalDbContext>>(),
            provider.GetRequiredService<IDbContextFactory<SqliteLocalDbContext>>(),
            provider.GetRequiredService<IImportadorIfc>(),
            provider.GetRequiredService<ILogger<BimService>>(), archivoDir));
        return services;
    }
}
