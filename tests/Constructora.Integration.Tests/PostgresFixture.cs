using Constructora.Domain;
using Constructora.Infrastructure.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Constructora.Integration.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL API";
}

/// <summary>
/// Uses a separate real PostgreSQL database for each run. The supplied connection's
/// database and data are never used for application requests or schema creation.
/// PostgreSQL must be available and the configured role must be allowed to create databases.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string databaseName = $"constructora_test_{Guid.NewGuid():N}";
    private readonly string adminConnectionString;
    private bool databaseCreated;

    public PostgresFixture()
    {
        var supplied = Environment.GetEnvironmentVariable("CONSTRUCTORA_TEST_POSTGRES")
            ?? "Host=127.0.0.1;Port=54329;Database=constructora;Username=constructora";
        var settings = new NpgsqlConnectionStringBuilder(supplied);
        // Administration uses the standard maintenance database, never the application's database.
        settings.Database = "postgres";
        settings.Pooling = false;
        adminConnectionString = settings.ConnectionString;
        settings.Database = databaseName;
        ConnectionString = settings.ConnectionString;
    }

    public string ConnectionString { get; }
    public TestFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid ProyectoId { get; } = Guid.NewGuid();
    public Guid PartidaId { get; } = Guid.NewGuid();
    public Guid SegundaPartidaId { get; } = Guid.NewGuid();
    public Guid PartidaAjenaId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
            databaseCreated = true;
        }

        try
        {
            Factory = new TestFactory(ConnectionString);
            Client = Factory.CreateClient();
            await WithDb(async db =>
            {
                await db.Database.EnsureCreatedAsync();
                var proyectoAjenoId = Guid.NewGuid();
                db.Proyectos.AddRange(
                    new Proyecto { Id = ProyectoId, Codigo = "OBRA-001", Nombre = "Obra de integración", FechaInicio = new DateOnly(2026, 1, 1) },
                    new Proyecto { Id = proyectoAjenoId, Codigo = "OBRA-002", Nombre = "Otra obra", FechaInicio = new DateOnly(2026, 1, 1) });
                db.Partidas.AddRange(
                    Partida(PartidaId, ProyectoId, "P-001"),
                    Partida(SegundaPartidaId, ProyectoId, "P-002"),
                    Partida(PartidaAjenaId, proyectoAjenoId, "P-001"));
                await db.SaveChangesAsync();
            });
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private static Partida Partida(Guid id, Guid proyectoId, string codigo) => new()
    {
        Id = id, ProyectoId = proyectoId, Codigo = codigo, Descripcion = "Unidad de obra",
        Unidad = "m2", PrecioUnitario = 12.5m, CantidadPrevista = 100m
    };

    public async Task WithDb(Func<ServidorDbContext, Task> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ServidorDbContext>());
    }

    public async Task<T> WithDb<T>(Func<ServidorDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServidorDbContext>());
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();
        if (!databaseCreated)
            return;

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        databaseCreated = false;
    }
}

public sealed class TestFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Servidor"] = connectionString,
                ["Database:Initialize"] = "false",
                ["Logging:LogLevel:Default"] = "Warning"
            }));
    }
}
