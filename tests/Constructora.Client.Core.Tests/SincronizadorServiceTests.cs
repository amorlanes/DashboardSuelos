using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructora.Application;
using Constructora.Client.Core;
using Constructora.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Constructora.Client.Core.Tests;

public sealed class SincronizadorServiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GuardadoSinConexionPersisteEnSqliteYSePuedeReabrir()
    {
        await using var ambiente = await Ambiente.CrearAsync((_, _) => throw new HttpRequestException("Offline"));
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        var resumen = await ambiente.Servicio.SincronizarPendientesAsync();
        Assert.True(resumen.SinConexion);
        using var reabierto = ambiente.NuevoServicio();
        Assert.Equal(2m, (await reabierto.ObtenerCertificacionAsync(ambiente.Certificacion.Id))!.Lineas.Single().CantidadPeriodo);
        await using var db = ambiente.Factory.CreateDbContext();
        Assert.True((await db.Certificaciones.SingleAsync()).EsPendienteSincronizacion);
        Assert.Single(await db.OperacionesPendientes.ToListAsync());
    }

    [Fact]
    public async Task RevisionesSeEnvianEnOrdenYEncadenanLaVersionDelServidor()
    {
        var recibidas = new List<SincronizarCertificacionRequest>();
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            var solicitud = await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token);
            recibidas.Add(solicitud!);
            return Respuesta(solicitud!.Certificacion with { RowVersion = (uint)(10 + recibidas.Count) });
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        await ambiente.Servicio.GuardarLocalAsync(ConCantidad(ambiente.Certificacion, 9m));
        var resumen = await ambiente.Servicio.SincronizarPendientesAsync();
        Assert.Equal(2, resumen.Enviadas);
        Assert.Equal(new uint[] { 0, 11 }, recibidas.Select(r => r.Certificacion.RowVersion));
        Assert.Equal(new decimal[] { 2, 9 }, recibidas.Select(r => r.Certificacion.Lineas.Single().CantidadPeriodo));
        Assert.NotEqual(recibidas[0].OperacionId, recibidas[1].OperacionId);
        var local = await ambiente.Servicio.ObtenerCertificacionAsync(ambiente.Certificacion.Id);
        Assert.Equal(12u, local!.RowVersion);
        await using var db = ambiente.Factory.CreateDbContext();
        Assert.Empty(await db.OperacionesPendientes.ToListAsync());
        Assert.False((await db.Certificaciones.SingleAsync()).EsPendienteSincronizacion);
    }

    [Fact]
    public async Task RespuestaPerdidaReintentaMismaOperacionYPayloadTrasReabrirElServicio()
    {
        var bodies = new List<string>();
        var recibos = new Dictionary<Guid, CertificacionDto>();
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            var body = await request.Content!.ReadAsStringAsync(token);
            bodies.Add(body);
            var solicitud = JsonSerializer.Deserialize<SincronizarCertificacionRequest>(body, Json)!;
            if (!recibos.TryGetValue(solicitud.OperacionId, out var dto))
            {
                dto = solicitud.Certificacion with { RowVersion = 77 };
                recibos.Add(solicitud.OperacionId, dto);
                throw new HttpRequestException("El servidor guardó, pero se perdió la respuesta.");
            }
            return Respuesta(dto);
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        Assert.True((await ambiente.Servicio.SincronizarPendientesAsync()).SinConexion);
        using var reabierto = ambiente.NuevoServicio();
        Assert.Equal(1, (await reabierto.SincronizarPendientesAsync()).Enviadas);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Single(recibos);
        Assert.Equal(77u, (await reabierto.ObtenerCertificacionAsync(ambiente.Certificacion.Id))!.RowVersion);
    }

    [Fact]
    public async Task ConflictoConservaEdicionYNoSobrescribeHastaResolucionExplicita()
    {
        var solicitudes = new List<SincronizarCertificacionRequest>();
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            var solicitud = (await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token))!;
            solicitudes.Add(solicitud);
            return solicitudes.Count == 1
                ? Respuesta(new ConflictoSincronizacion("Otro usuario cambió la certificación.",
                    ConCantidad(solicitud.Certificacion, 6m) with { RowVersion = 9 }), HttpStatusCode.Conflict)
                : Respuesta(solicitud.Certificacion with { RowVersion = 10 });
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion with { RowVersion = 4 });
        Assert.Equal(1, (await ambiente.Servicio.SincronizarPendientesAsync()).Conflictos);
        await ambiente.Servicio.SincronizarPendientesAsync();
        Assert.Single(solicitudes);
        var conflicto = Assert.Single(await ambiente.Servicio.ObtenerConflictosAsync());
        Assert.Equal(2m, conflicto.VersionLocal.Lineas.Single().CantidadPeriodo);
        Assert.Equal(4u, conflicto.VersionLocal.RowVersion);
        Assert.Equal(9u, conflicto.VersionServidor!.RowVersion);
        await ambiente.Servicio.ResolverConflictoAsync(ambiente.Certificacion.Id, ConCantidad(conflicto.VersionLocal, 8m));
        Assert.Equal(1, (await ambiente.Servicio.SincronizarPendientesAsync()).Enviadas);
        Assert.Equal(9u, solicitudes[1].Certificacion.RowVersion);
        Assert.NotEqual(solicitudes[0].OperacionId, solicitudes[1].OperacionId);
        Assert.Empty(await ambiente.Servicio.ObtenerConflictosAsync());
    }

    [Fact]
    public async Task EdicionMientrasHayUnaPeticionEnVueloNoSePierdeNiBloqueaElGuardado()
    {
        var iniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var solicitudes = new List<SincronizarCertificacionRequest>();
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            var solicitud = (await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token))!;
            solicitudes.Add(solicitud);
            if (solicitudes.Count == 1)
            {
                iniciada.SetResult();
                await liberar.Task.WaitAsync(token);
            }
            return Respuesta(solicitud.Certificacion with { RowVersion = (uint)(100 + solicitudes.Count) });
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        var sincronizacion = ambiente.Servicio.SincronizarPendientesAsync();
        await iniciada.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await ambiente.Servicio.GuardarLocalAsync(ConCantidad(ambiente.Certificacion, 15m)).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { liberar.SetResult(); }
        Assert.Equal(2, (await sincronizacion).Enviadas);
        Assert.Equal(101u, solicitudes[1].Certificacion.RowVersion);
        var local = await ambiente.Servicio.ObtenerCertificacionAsync(ambiente.Certificacion.Id);
        Assert.Equal(15m, local!.Lineas.Single().CantidadPeriodo);
        Assert.Equal(102u, local.RowVersion);
    }

    [Fact]
    public async Task AceptarVersionServidorFacturadaLimpiaLaColaSinEnviarOtroPost()
    {
        var posts = 0;
        CertificacionDto? remoto = null;
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            posts++;
            var solicitud = (await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token))!;
            remoto = ConCantidad(solicitud.Certificacion, 6m) with { RowVersion = 9, Estado = EstadoCertificacion.Facturada };
            return Respuesta(new ConflictoSincronizacion("La certificación está facturada.", remoto), HttpStatusCode.Conflict);
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion with { RowVersion = 4 });
        await ambiente.Servicio.GuardarLocalAsync(ConCantidad(ambiente.Certificacion, 8m) with { RowVersion = 4 });
        Assert.Equal(1, (await ambiente.Servicio.SincronizarPendientesAsync()).Conflictos);
        await ambiente.Servicio.AceptarVersionServidorAsync(ambiente.Certificacion.Id);
        Assert.Equal(0, (await ambiente.Servicio.SincronizarPendientesAsync()).Enviadas);
        Assert.Equal(1, posts);
        var aceptada = (await ambiente.Servicio.ObtenerCertificacionAsync(ambiente.Certificacion.Id))!;
        Assert.Equal(EstadoCertificacion.Facturada, aceptada.Estado);
        Assert.Equal(remoto!.RowVersion, aceptada.RowVersion);
        Assert.Equal(remoto.Lineas.Single(), Assert.Single(aceptada.Lineas));
        Assert.Empty(await ambiente.Servicio.ObtenerConflictosAsync());
        await using var db = ambiente.Factory.CreateDbContext();
        var local = await db.Certificaciones.SingleAsync();
        Assert.False(local.EsPendienteSincronizacion);
        Assert.False(local.TieneConflicto);
        Assert.Null(local.UltimoError);
        Assert.Null(local.VersionServidorJson);
        Assert.Equal(3, local.RevisionLocal);
        Assert.Empty(await db.OperacionesPendientes.ToListAsync());
    }

    [Fact]
    public async Task AceptarServidorSinSnapshotRemotoNoDescartaLaEdicionLocal()
    {
        await using var ambiente = await Ambiente.CrearAsync((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RespuestaSalud() : Respuesta(new ConflictoSincronizacion("No existe versión remota.", null), HttpStatusCode.Conflict)));
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        await ambiente.Servicio.SincronizarPendientesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ambiente.Servicio.AceptarVersionServidorAsync(ambiente.Certificacion.Id));
        Assert.Single(await ambiente.Servicio.ObtenerConflictosAsync());
        await using var db = ambiente.Factory.CreateDbContext();
        Assert.True((await db.Certificaciones.SingleAsync()).EsPendienteSincronizacion);
        Assert.Single(await db.OperacionesPendientes.ToListAsync());
    }

    [Fact]
    public async Task ErrorDeDatosSePropagaYConservaLaOperacionPendiente()
    {
        await using var ambiente = await Ambiente.CrearAsync((request, _) => Task.FromResult(
            request.Method == HttpMethod.Get ? RespuestaSalud() : Respuesta(new { error = "Estado inválido" }, HttpStatusCode.BadRequest)));
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        var exception = await Assert.ThrowsAsync<SincronizacionDatosException>(() => ambiente.Servicio.SincronizarPendientesAsync());
        Assert.Equal(400, exception.StatusCode);
        await using var db = ambiente.Factory.CreateDbContext();
        Assert.True((await db.Certificaciones.SingleAsync()).EsPendienteSincronizacion);
        Assert.Single(await db.OperacionesPendientes.ToListAsync());
    }

    [Fact]
    public async Task ActualizacionRemotaNoSobrescribeCertificacionesPendientes()
    {
        CertificacionDto? remoto = null;
        await using var ambiente = await Ambiente.CrearAsync((_, _) => Task.FromResult(Respuesta(new[] { remoto! })));
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        remoto = ConCantidad(ambiente.Certificacion, 99m) with { RowVersion = 20 };
        await ambiente.Servicio.ActualizarCertificacionesAsync(ambiente.Certificacion.ProyectoId);
        var local = await ambiente.Servicio.ObtenerCertificacionAsync(ambiente.Certificacion.Id);
        Assert.Equal(2m, local!.Lineas.Single().CantidadPeriodo);
        Assert.Equal(0u, local.RowVersion);
    }

    [Fact]
    public async Task EditorObsoletoTrasDescargaRemotaConservaAmbasVersionesSinSubirSobreTokenNuevo()
    {
        CertificacionDto? remoto = null;
        var posts = 0;
        await using var ambiente = await Ambiente.CrearAsync((request, _) =>
        {
            if (request.Method == HttpMethod.Post) posts++;
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/health" ? RespuestaSalud() : Respuesta(new[] { remoto! }));
        });
        remoto = ambiente.Certificacion with { RowVersion = 1 };
        await ambiente.Servicio.ActualizarCertificacionesAsync(remoto.ProyectoId);
        var editorAbierto = (await ambiente.Servicio.ObtenerCertificacionAsync(remoto.Id))!;
        remoto = ConCantidad(remoto, 8m) with { RowVersion = 2 };
        await ambiente.Servicio.ActualizarCertificacionesAsync(remoto.ProyectoId);
        await ambiente.Servicio.GuardarLocalAsync(ConCantidad(editorAbierto, 5m));
        await ambiente.Servicio.SincronizarPendientesAsync();
        var conflicto = Assert.Single(await ambiente.Servicio.ObtenerConflictosAsync());
        Assert.Equal(1u, conflicto.VersionLocal.RowVersion);
        Assert.Equal(5m, conflicto.VersionLocal.Lineas.Single().CantidadPeriodo);
        Assert.Equal(2u, conflicto.VersionServidor!.RowVersion);
        Assert.Equal(8m, conflicto.VersionServidor.Lineas.Single().CantidadPeriodo);
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task RechazoConfirmadoNoBloqueaOtrasCertificacionesYUnNuevoGuardadoPermiteCorregir()
    {
        var enviadas = new List<SincronizarCertificacionRequest>();
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            var dto = (await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token))!;
            enviadas.Add(dto);
            return enviadas.Count == 1
                ? Respuesta(new { error = "Datos rechazados" }, HttpStatusCode.BadRequest)
                : Respuesta(dto.Certificacion with { RowVersion = (uint)(10 + enviadas.Count) });
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        var segunda = ambiente.Certificacion with { Id = Guid.NewGuid(), NumeroCertificacion = 2,
            Lineas = ambiente.Certificacion.Lineas.Select(l => l with { Id = Guid.NewGuid() }).ToList() };
        await ambiente.Servicio.GuardarLocalAsync(segunda);
        await Assert.ThrowsAsync<SincronizacionDatosException>(() => ambiente.Servicio.SincronizarPendientesAsync());
        Assert.Equal(1, (await ambiente.Servicio.SincronizarPendientesAsync()).Enviadas);
        Assert.Equal(segunda.Id, enviadas[1].Certificacion.Id);
        await ambiente.Servicio.GuardarLocalAsync(ConCantidad(ambiente.Certificacion, 7m));
        Assert.Equal(1, (await ambiente.Servicio.SincronizarPendientesAsync()).Enviadas);
        Assert.Equal(7m, enviadas[2].Certificacion.Lineas.Single().CantidadPeriodo);
        Assert.NotEqual(enviadas[0].OperacionId, enviadas[2].OperacionId);
        await using var db = ambiente.Factory.CreateDbContext();
        Assert.Empty(await db.OperacionesPendientes.ToListAsync());
    }

    [Fact]
    public async Task VersionAnteriorDeLaPropiaSesionSigueSiendoValidaTrasUnAckPropio()
    {
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get) return RespuestaSalud();
            var dto = (await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token))!;
            return Respuesta(dto.Certificacion with { RowVersion = dto.Certificacion.RowVersion + 1 });
        });
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        await ambiente.Servicio.SincronizarPendientesAsync();
        await ambiente.Servicio.GuardarLocalAsync(ConCantidad(ambiente.Certificacion, 7m));
        Assert.Equal(1, (await ambiente.Servicio.SincronizarPendientesAsync()).Enviadas);
        Assert.Empty(await ambiente.Servicio.ObtenerConflictosAsync());
        Assert.Equal(2u, (await ambiente.Servicio.ObtenerCertificacionAsync(ambiente.Certificacion.Id))!.RowVersion);
    }

    [Fact]
    public async Task CantidadesFueraDePrecisionNoSeGuardanNiSeEncolan()
    {
        await using var ambiente = await Ambiente.CrearAsync((_, _) => throw new HttpRequestException("Offline"));
        await Assert.ThrowsAsync<ArgumentException>(() => ambiente.Servicio.GuardarLocalAsync(ConCantidad(ambiente.Certificacion, 2.00001m)));
        await using var db = ambiente.Factory.CreateDbContext();
        Assert.Empty(await db.Certificaciones.ToListAsync());
        Assert.Empty(await db.OperacionesPendientes.ToListAsync());
    }

    [Fact]
    public async Task DescargaEnVueloSeSerializaConElEnvioParaNoRevertirLaVersionConfirmada()
    {
        var descargaIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminarDescarga = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CertificacionDto? remoto = null;
        var posts = 0;
        await using var ambiente = await Ambiente.CrearAsync(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/health") return RespuestaSalud();
            if (request.Method == HttpMethod.Get)
            {
                descargaIniciada.SetResult();
                await terminarDescarga.Task.WaitAsync(token);
                return Respuesta(new[] { remoto! });
            }
            posts++;
            var dto = (await request.Content!.ReadFromJsonAsync<SincronizarCertificacionRequest>(Json, token))!;
            return Respuesta(dto.Certificacion with { RowVersion = 50 });
        });
        remoto = ConCantidad(ambiente.Certificacion, 99m) with { RowVersion = 1 };
        var descarga = ambiente.Servicio.ActualizarCertificacionesAsync(remoto.ProyectoId);
        await descargaIniciada.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        var envio = ambiente.Servicio.SincronizarPendientesAsync();
        Assert.False(envio.IsCompleted);
        Assert.Equal(0, posts);
        terminarDescarga.SetResult();
        await descarga;
        await envio;
        var local = await ambiente.Servicio.ObtenerCertificacionAsync(ambiente.Certificacion.Id);
        Assert.Equal(50u, local!.RowVersion);
        Assert.Equal(2m, local.Lineas.Single().CantidadPeriodo);
    }

    [Fact]
    public async Task EditarCertificacionExistentePermiteAnadirQuitarYReemplazarLineasConGuidExplicito()
    {
        await using var ambiente = await Ambiente.CrearAsync((_, _) => throw new HttpRequestException("Offline"));
        var nuevaPartidaId = Guid.NewGuid();
        await using (var db = ambiente.Factory.CreateDbContext())
        {
            db.Partidas.Add(new PartidaLocal { Id = nuevaPartidaId, ProyectoId = ambiente.Certificacion.ProyectoId,
                Codigo = "P-02", Descripcion = "Acero", Unidad = "kg", CantidadPrevista = 100m, PrecioUnitario = 2m });
            await db.SaveChangesAsync();
        }
        await ambiente.Servicio.GuardarLocalAsync(ambiente.Certificacion);
        var lineaNueva = new LineaCertificacionDto(Guid.NewGuid(), nuevaPartidaId, 0, 5, 5);
        var ampliada = ambiente.Certificacion with { Lineas = [.. ambiente.Certificacion.Lineas, lineaNueva] };
        await ambiente.Servicio.GuardarLocalAsync(ampliada);
        Assert.Equal(2, (await ambiente.Servicio.ObtenerCertificacionAsync(ampliada.Id))!.Lineas.Count);
        var reemplazo = lineaNueva with { Id = Guid.NewGuid(), CantidadPeriodo = 8 };
        await ambiente.Servicio.GuardarLocalAsync(ampliada with { Lineas = [reemplazo] });
        var final = (await ambiente.Servicio.ObtenerCertificacionAsync(ampliada.Id))!;
        Assert.Equal(reemplazo, Assert.Single(final.Lineas));
        await using var comprobacion = ambiente.Factory.CreateDbContext();
        Assert.Single(await comprobacion.Set<LineaCertificacionLocal>().ToListAsync());
        Assert.Equal(3, await comprobacion.OperacionesPendientes.CountAsync());
    }

    private static CertificacionDto ConCantidad(CertificacionDto dto, decimal cantidad) => dto with
    { Lineas = dto.Lineas.Select(l => l with { CantidadPeriodo = cantidad }).ToList() };

    private static HttpResponseMessage Respuesta<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: Json) };
    private static HttpResponseMessage RespuestaSalud() => new(HttpStatusCode.OK) { Content = new StringContent("Healthy") };

    private sealed class Ambiente : IAsyncDisposable
    {
        private readonly string _path;
        private readonly HttpClient _client;
        public FactorySqlite Factory { get; }
        public SincronizadorService Servicio { get; }
        public CertificacionDto Certificacion { get; }

        private Ambiente(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _path = Path.Combine(Path.GetTempPath(), $"constructora-tests-{Guid.NewGuid():N}.db");
            Factory = new FactorySqlite(new DbContextOptionsBuilder<SqliteLocalDbContext>()
                .UseSqlite($"Data Source={_path};Foreign Keys=True;Pooling=False").Options);
            _client = new HttpClient(new Handler(responder)) { BaseAddress = new Uri("http://localhost/") };
            Servicio = NuevoServicio();
            Certificacion = new CertificacionDto(Guid.NewGuid(), Guid.NewGuid(), 1, new DateOnly(2026, 10, 1),
                EstadoCertificacion.Borrador, 0, [new(Guid.NewGuid(), Guid.NewGuid(), 0m, 2m, 10m)]);
        }

        public SincronizadorService NuevoServicio() => new(Factory, _client, NullLogger<SincronizadorService>.Instance);

        public static async Task<Ambiente> CrearAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            var a = new Ambiente(responder);
            await a.Servicio.InicializarAsync();
            await using var db = a.Factory.CreateDbContext();
            db.Proyectos.Add(new ProyectoLocal { Id = a.Certificacion.ProyectoId, Nombre = "Obra", Codigo = "OB-01" });
            db.Partidas.Add(new PartidaLocal { Id = a.Certificacion.Lineas[0].PartidaId, ProyectoId = a.Certificacion.ProyectoId,
                Codigo = "P-01", Descripcion = "Hormigón", Unidad = "m3", CantidadPrevista = 20m, PrecioUnitario = 100m });
            await db.SaveChangesAsync();
            return a;
        }

        public ValueTask DisposeAsync()
        {
            Servicio.Dispose(); _client.Dispose();
            foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FactorySqlite(DbContextOptions<SqliteLocalDbContext> options) : IDbContextFactory<SqliteLocalDbContext>
    {
        public SqliteLocalDbContext CreateDbContext() => new(options);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
