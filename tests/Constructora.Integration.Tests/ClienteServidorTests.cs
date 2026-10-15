using System.Net.Http.Json;
using Constructora.Application;
using Constructora.Client.Core;
using Constructora.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Constructora.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ClienteServidorTests(PostgresFixture fixture)
{
    private static int nextNumber = 100000;

    [Fact]
    public async Task Catalogo_y_outbox_sqlite_se_sincronizan_con_la_api_y_postgres_reales()
    {
        await using var client = await LocalClient.Create(fixture);
        Assert.Contains(await client.Sync.ObtenerProyectosAsync(), p => p.Id == fixture.ProyectoId);
        Assert.Contains(await client.Sync.ObtenerPartidasAsync(fixture.ProyectoId), p => p.Id == fixture.PartidaId);

        var draft = NewDraft();
        await client.Sync.GuardarLocalAsync(draft);
        var pending = await client.WithDb(db => db.OperacionesPendientes.AsNoTracking().SingleAsync());
        Assert.NotEqual(Guid.Empty, pending.OperacionId);
        Assert.False(pending.Preparada);
        Assert.Equal(0u, (await client.Sync.ObtenerCertificacionAsync(draft.Id))!.RowVersion);
        Assert.False(await fixture.WithDb(db => db.Certificaciones.AnyAsync(c => c.Id == draft.Id)));

        var result = await client.Sync.SincronizarPendientesAsync();
        Assert.Equal(new ResumenSincronizacion(1, 0, false), result);
        var remote = await Get(draft.Id);
        var local = await client.Sync.ObtenerCertificacionAsync(draft.Id);
        Assert.NotNull(local);
        Assert.NotEqual(0u, remote.RowVersion);
        Assert.Equal(remote.RowVersion, local.RowVersion);
        Assert.Equal(draft.Lineas, remote.Lineas);
        Assert.Equal(remote.Lineas, local.Lineas);
        Assert.False(await client.WithDb(db => db.OperacionesPendientes.AnyAsync()));
        Assert.False(await client.WithDb(db => db.Certificaciones.AnyAsync(c => c.EsPendienteSincronizacion || c.TieneConflicto)));
        Assert.True(await fixture.WithDb(db => db.OperacionesSincronizacion.AnyAsync(o =>
            o.OperacionId == pending.OperacionId && o.CertificacionId == draft.Id)));
    }

    [Fact]
    public async Task Dos_sqlite_independientes_conservan_el_conflicto_hasta_una_resolucion_explicita()
    {
        await using var first = await LocalClient.Create(fixture);
        await using var second = await LocalClient.Create(fixture);
        var draft = NewDraft();
        await first.Sync.GuardarLocalAsync(draft);
        Assert.Equal(new ResumenSincronizacion(1, 0, false), await first.Sync.SincronizarPendientesAsync());
        Assert.True(await second.Sync.ActualizarCertificacionesAsync(fixture.ProyectoId));

        var firstVersion = await first.Sync.ObtenerCertificacionAsync(draft.Id);
        var secondVersion = await second.Sync.ObtenerCertificacionAsync(draft.Id);
        Assert.NotNull(firstVersion);
        Assert.NotNull(secondVersion);
        Assert.Equal(firstVersion.RowVersion, secondVersion.RowVersion);
        await first.Sync.GuardarLocalAsync(WithQuantity(firstVersion, 20m));
        await second.Sync.GuardarLocalAsync(WithQuantity(secondVersion, 30m));
        var rejectedOperation = await second.WithDb(db => db.OperacionesPendientes.AsNoTracking()
            .SingleAsync(o => o.CertificacionId == draft.Id));

        Assert.Equal(new ResumenSincronizacion(1, 0, false), await first.Sync.SincronizarPendientesAsync());
        Assert.Equal(new ResumenSincronizacion(0, 1, false), await second.Sync.SincronizarPendientesAsync());
        var conflict = Assert.Single(await second.Sync.ObtenerConflictosAsync());
        Assert.NotNull(conflict.VersionServidor);
        Assert.Equal(30m, Assert.Single(conflict.VersionLocal.Lineas).CantidadPeriodo);
        Assert.Equal(20m, Assert.Single(conflict.VersionServidor.Lineas).CantidadPeriodo);
        Assert.NotEqual(conflict.VersionLocal.RowVersion, conflict.VersionServidor.RowVersion);
        Assert.Equal(20m, Assert.Single((await Get(draft.Id)).Lineas).CantidadPeriodo);

        // Refreshing may not replace the unsent editor contents with the winning server copy.
        Assert.True(await second.Sync.ActualizarCertificacionesAsync(fixture.ProyectoId));
        var preserved = await second.Sync.ObtenerCertificacionAsync(draft.Id);
        Assert.NotNull(preserved);
        Assert.Equal(30m, Assert.Single(preserved.Lineas).CantidadPeriodo);
        Assert.True(await second.WithDb(db => db.Certificaciones.AnyAsync(c =>
            c.Id == draft.Id && c.EsPendienteSincronizacion && c.TieneConflicto)));

        // The user compares 20 on the server with 30 locally and explicitly chooses 35.
        await second.Sync.ResolverConflictoAsync(draft.Id, WithQuantity(conflict.VersionLocal, 35m));
        Assert.Empty(await second.Sync.ObtenerConflictosAsync());
        var resolutionOperation = await second.WithDb(db => db.OperacionesPendientes.AsNoTracking()
            .SingleAsync(o => o.CertificacionId == draft.Id));
        Assert.NotEqual(rejectedOperation.OperacionId, resolutionOperation.OperacionId);
        Assert.Equal(new ResumenSincronizacion(1, 0, false), await second.Sync.SincronizarPendientesAsync());

        var resolved = await Get(draft.Id);
        Assert.Equal(35m, Assert.Single(resolved.Lineas).CantidadPeriodo);
        Assert.NotEqual(conflict.VersionServidor.RowVersion, resolved.RowVersion);
        Assert.False(await second.WithDb(db => db.OperacionesPendientes.AnyAsync(o => o.CertificacionId == draft.Id)));
        Assert.False(await second.WithDb(db => db.Certificaciones.AnyAsync(c =>
            c.Id == draft.Id && (c.EsPendienteSincronizacion || c.TieneConflicto))));
        Assert.True(await first.Sync.ActualizarCertificacionesAsync(fixture.ProyectoId));
        var refreshed = await first.Sync.ObtenerCertificacionAsync(draft.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(resolved.RowVersion, refreshed.RowVersion);
        Assert.Equal(resolved.Lineas, refreshed.Lineas);
    }

    private CertificacionDto NewDraft() => new(Guid.NewGuid(), fixture.ProyectoId,
        Interlocked.Increment(ref nextNumber), new DateOnly(2026, 3, 1), EstadoCertificacion.Borrador, 0,
        [new LineaCertificacionDto(Guid.NewGuid(), fixture.PartidaId, 0m, 10m, 10m)]);

    private static CertificacionDto WithQuantity(CertificacionDto dto, decimal quantity) => dto with
    {
        Lineas = [dto.Lineas.Single() with { CantidadPeriodo = quantity, PorcentajeAvance = quantity }]
    };

    private async Task<CertificacionDto> Get(Guid id)
    {
        var dto = await fixture.Client.GetFromJsonAsync<CertificacionDto>($"/api/certificaciones/{id}");
        Assert.NotNull(dto);
        return dto;
    }

    private sealed class LocalClient : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"constructora_client_test_{Guid.NewGuid():N}");
        private readonly ServiceProvider provider;

        private LocalClient(PostgresFixture fixture)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddConstructoraClient(new Uri("http://localhost/"), Path.Combine(directory, "local.db"));
            services.AddHttpClient("Constructora")
                .ConfigurePrimaryHttpMessageHandler(() => fixture.Factory.Server.CreateHandler());
            provider = services.BuildServiceProvider();
            Sync = provider.GetRequiredService<ISincronizadorService>();
        }

        public ISincronizadorService Sync { get; }

        public static async Task<LocalClient> Create(PostgresFixture fixture)
        {
            var result = new LocalClient(fixture);
            try
            {
                await result.Sync.InicializarAsync();
                Assert.True(await result.Sync.CargarCatalogoAsync());
                return result;
            }
            catch
            {
                await result.DisposeAsync();
                throw;
            }
        }

        public async Task<T> WithDb<T>(Func<SqliteLocalDbContext, Task<T>> action)
        {
            await using var db = await provider.GetRequiredService<IDbContextFactory<SqliteLocalDbContext>>()
                .CreateDbContextAsync();
            return await action(db);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            // Release idle SQLite handles so temporary files can also be removed on Windows.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
