using System.Net;
using System.Net.Http.Json;
using Constructora.Application;
using Constructora.Domain;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Constructora.Integration.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CertificacionesApiTests(PostgresFixture fixture)
{
    private static int nextNumber;

    [Fact]
    public async Task Health_y_catalogos_consultan_postgres()
    {
        using var health = await fixture.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var proyectos = await fixture.Client.GetFromJsonAsync<List<ProyectoDto>>("/api/proyectos");
        Assert.Contains(proyectos!, p => p.Id == fixture.ProyectoId);
        var partidas = await fixture.Client.GetFromJsonAsync<List<PartidaDto>>($"/api/proyectos/{fixture.ProyectoId}/partidas");
        Assert.Contains(partidas!, p => p.Id == fixture.PartidaId);
        Assert.DoesNotContain(partidas!, p => p.Id == fixture.PartidaAjenaId);
    }

    [Fact]
    public async Task Crear_y_cambiar_solo_una_linea_renueva_xmin_del_agregado()
    {
        var created = await SendOk(NewRequest());
        Assert.NotEqual(0u, created.RowVersion);
        var line = created.Lineas.Single() with { CantidadPeriodo = 37m, PorcentajeAvance = 37m };
        var updated = await SendOk(Request(created with { Lineas = [line] }));

        Assert.NotEqual(created.RowVersion, updated.RowVersion);
        Assert.Equal(created.Fecha, updated.Fecha);
        Assert.Equal(created.Estado, updated.Estado);
        Assert.Equal(created.NumeroCertificacion, updated.NumeroCertificacion);
        var stored = await Get(created.Id);
        Assert.Equal(updated.RowVersion, stored.RowVersion);
        Assert.Equal(37m, Assert.Single(stored.Lineas).CantidadPeriodo);
        var databaseVersion = await fixture.WithDb(db => db.Certificaciones.AsNoTracking()
            .Where(c => c.Id == created.Id).Select(c => c.RowVersion).SingleAsync());
        Assert.Equal(updated.RowVersion, databaseVersion);
    }

    [Fact]
    public async Task Editar_un_agregado_existente_puede_insertar_una_linea_con_guid_del_cliente()
    {
        var original = await SendOk(NewRequest());
        var newLine = new LineaCertificacionDto(Guid.NewGuid(), fixture.SegundaPartidaId, 0m, 5m, 5m);
        var updated = await SendOk(Request(original with { Lineas = [.. original.Lineas, newLine] }));

        Assert.NotEqual(original.RowVersion, updated.RowVersion);
        Assert.Contains(updated.Lineas, line => line.Id == newLine.Id && line.PartidaId == newLine.PartidaId);
        var stored = await Get(original.Id);
        Assert.Equal(2, stored.Lineas.Count);
        Assert.Contains(stored.Lineas, line => line.Id == newLine.Id && line.CantidadPeriodo == 5m);

        // Replacing an existing row by a fresh client GUID must delete the old rows as well.
        var replacement = original.Lineas.Single() with { Id = Guid.NewGuid(), CantidadPeriodo = 19m };
        var replaced = await SendOk(Request(updated with { Lineas = [replacement] }));
        Assert.Equal(replacement, Assert.Single(replaced.Lineas));
        Assert.NotEqual(updated.RowVersion, replaced.RowVersion);
        var oldIds = updated.Lineas.Select(line => line.Id).ToArray();
        Assert.False(await fixture.WithDb(db => db.LineasCertificacion.AnyAsync(line => oldIds.Contains(line.Id))));
    }

    [Fact]
    public async Task Repetir_operacion_exacta_devuelve_el_mismo_resultado_sin_segundo_guardado()
    {
        var request = NewRequest();
        var first = await SendOk(request);
        var repeated = await SendOk(request);
        Assert.Equal(first, repeated with { Lineas = first.Lineas });
        Assert.Equal(first.Lineas, repeated.Lineas);
        Assert.Equal(first.RowVersion, (await Get(first.Id)).RowVersion);
        var receiptCount = await fixture.WithDb(db => db.OperacionesSincronizacion.CountAsync(o => o.OperacionId == request.OperacionId));
        Assert.Equal(1, receiptCount);

        var latest = await SendOk(Request(first with { Fecha = first.Fecha.AddDays(1) }));
        var oldReplay = await SendOk(request);
        Assert.Equal(first.RowVersion, oldReplay.RowVersion);
        Assert.Equal(latest.RowVersion, (await Get(first.Id)).RowVersion);
        Assert.Equal(latest.Fecha, (await Get(first.Id)).Fecha);
    }

    [Fact]
    public async Task Reintentos_simultaneos_de_la_misma_operacion_crean_un_solo_resultado()
    {
        var creation = NewRequest();
        await AssertSameOperationConverges(creation);
        var created = await Get(creation.Certificacion.Id);
        await AssertSameOperationConverges(Request(created with { Fecha = created.Fecha.AddDays(1) }));
    }

    [Fact]
    public async Task Reutilizar_operacion_con_otro_payload_devuelve_conflicto()
    {
        var request = NewRequest();
        var saved = await SendOk(request);
        var changed = request with { Certificacion = request.Certificacion with { Fecha = request.Certificacion.Fecha.AddDays(1) } };
        using var response = await fixture.Client.PostAsJsonAsync("/api/certificaciones/sincronizar", changed);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(saved.Fecha, (await Get(saved.Id)).Fecha);
        Assert.Equal(saved.RowVersion, (await Get(saved.Id)).RowVersion);
    }

    [Fact]
    public async Task Version_obsoleta_devuelve_409_y_la_version_del_servidor_sin_sobreescribir()
    {
        var original = await SendOk(NewRequest());
        var winner = await SendOk(Request(original with { Fecha = original.Fecha.AddDays(1) }));
        using var response = await fixture.Client.PostAsJsonAsync("/api/certificaciones/sincronizar",
            Request(original with { Fecha = original.Fecha.AddDays(2) }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var conflict = await response.Content.ReadFromJsonAsync<ConflictoSincronizacion>();
        Assert.NotNull(conflict?.VersionServidor);
        Assert.Equal(winner.RowVersion, conflict.VersionServidor.RowVersion);
        var stored = await Get(original.Id);
        Assert.Equal(winner.Fecha, stored.Fecha);
        Assert.Equal(winner.RowVersion, stored.RowVersion);
    }

    [Fact]
    public async Task Dos_actualizaciones_con_la_misma_version_tienen_un_unico_ganador()
    {
        var original = await SendOk(NewRequest());
        var first = Request(original with { Fecha = original.Fecha.AddDays(1) });
        var second = Request(original with { Fecha = original.Fecha.AddDays(2) });
        var responses = await Task.WhenAll(
            fixture.Client.PostAsJsonAsync("/api/certificaciones/sincronizar", first),
            fixture.Client.PostAsJsonAsync("/api/certificaciones/sincronizar", second));
        try
        {
            var success = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var winningDto = await success.Content.ReadFromJsonAsync<CertificacionDto>();
            Assert.NotNull(winningDto);
            var stored = await Get(original.Id);
            Assert.Equal(winningDto.Fecha, stored.Fecha);
            Assert.Equal(winningDto.RowVersion, stored.RowVersion);
            var receipts = await fixture.WithDb(db => db.OperacionesSincronizacion
                .CountAsync(o => o.OperacionId == first.OperacionId || o.OperacionId == second.OperacionId));
            Assert.Equal(1, receipts);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task Una_partida_de_otro_proyecto_se_rechaza_sin_guardar()
    {
        var request = NewRequest();
        request = request with { Certificacion = request.Certificacion with
        {
            Lineas = [request.Certificacion.Lineas.Single() with { PartidaId = fixture.PartidaAjenaId }]
        } };
        await AssertRejectedWithoutSave(request, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("partida_duplicada")]
    [InlineData("id_linea_duplicado")]
    [InlineData("anterior_negativa")]
    [InlineData("periodo_negativo")]
    [InlineData("porcentaje_negativo")]
    [InlineData("porcentaje_mayor_100")]
    [InlineData("cantidad_demasiado_grande")]
    [InlineData("cantidad_demasiados_decimales")]
    [InlineData("porcentaje_demasiados_decimales")]
    [InlineData("numero_cero")]
    [InlineData("estado_invalido")]
    public async Task Duplicados_y_rangos_invalidos_devuelven_400_sin_datos_parciales(string invalid)
    {
        var request = NewRequest();
        var dto = request.Certificacion;
        var line = dto.Lineas.Single();
        dto = invalid switch
        {
            "partida_duplicada" => dto with { Lineas = [line, line with { Id = Guid.NewGuid() }] },
            "id_linea_duplicado" => dto with { Lineas = [line, line with { PartidaId = fixture.SegundaPartidaId }] },
            "anterior_negativa" => dto with { Lineas = [line with { CantidadAcumuladaAnterior = -1m }] },
            "periodo_negativo" => dto with { Lineas = [line with { CantidadPeriodo = -1m }] },
            "porcentaje_negativo" => dto with { Lineas = [line with { PorcentajeAvance = -1m }] },
            "porcentaje_mayor_100" => dto with { Lineas = [line with { PorcentajeAvance = 100.01m }] },
            "cantidad_demasiado_grande" => dto with { Lineas = [line with { CantidadPeriodo = 100000000000000m }] },
            "cantidad_demasiados_decimales" => dto with { Lineas = [line with { CantidadPeriodo = 0.00001m }] },
            "porcentaje_demasiados_decimales" => dto with { Lineas = [line with { PorcentajeAvance = 0.00001m }] },
            "numero_cero" => dto with { NumeroCertificacion = 0 },
            "estado_invalido" => dto with { Estado = (EstadoCertificacion)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        await AssertRejectedWithoutSave(request with { Certificacion = dto }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Numero_de_certificacion_duplicado_en_la_obra_devuelve_409()
    {
        var saved = await SendOk(NewRequest());
        var request = NewRequest();
        request = request with { Certificacion = request.Certificacion with { NumeroCertificacion = saved.NumeroCertificacion } };
        await AssertRejectedWithoutSave(request, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SignalR_notifica_cuando_los_datos_y_el_recibo_ya_estan_confirmados()
    {
        var request = NewRequest();
        var received = new TaskCompletionSource<(NotificacionCertificacion Notice, uint Version, bool Receipt)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/certificaciones", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => fixture.Factory.Server.CreateHandler();
            }).Build();
        using var subscription = hub.On<NotificacionCertificacion>("CertificacionActualizada", async notice =>
        {
            if (notice.CertificacionId != request.Certificacion.Id)
                return;
            try
            {
                var evidence = await fixture.WithDb(async db =>
                {
                    var version = await db.Certificaciones.AsNoTracking()
                        .Where(c => c.Id == notice.CertificacionId).Select(c => c.RowVersion).SingleAsync();
                    var receipt = await db.OperacionesSincronizacion.AnyAsync(o => o.OperacionId == request.OperacionId);
                    return (notice, version, receipt);
                });
                received.TrySetResult(evidence);
            }
            catch (Exception error)
            {
                received.TrySetException(error);
            }
        });
        await hub.StartAsync();
        var saved = await SendOk(request);
        var evidence = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(fixture.ProyectoId, evidence.Notice.ProyectoId);
        Assert.Equal(saved.Id, evidence.Notice.CertificacionId);
        Assert.Equal(saved.RowVersion, evidence.Version);
        Assert.True(evidence.Receipt);
        await hub.StopAsync();
    }

    private SincronizarCertificacionRequest NewRequest() => Request(new CertificacionDto(
        Guid.NewGuid(), fixture.ProyectoId, Interlocked.Increment(ref nextNumber), new DateOnly(2026, 2, 1),
        EstadoCertificacion.Borrador, 0,
        [new LineaCertificacionDto(Guid.NewGuid(), fixture.PartidaId, 0m, 10m, 10m)]));

    private static SincronizarCertificacionRequest Request(CertificacionDto dto) => new(Guid.NewGuid(), dto);

    private async Task<CertificacionDto> SendOk(SincronizarCertificacionRequest request)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/certificaciones/sincronizar", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Se esperaba 200; recibido {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var result = await response.Content.ReadFromJsonAsync<CertificacionDto>();
        Assert.NotNull(result);
        return result;
    }

    private async Task AssertSameOperationConverges(SincronizarCertificacionRequest request)
    {
        var results = await Task.WhenAll(SendOk(request), SendOk(request));
        Assert.Equal(results[0].RowVersion, results[1].RowVersion);
        Assert.Equal(results[0].Fecha, results[1].Fecha);
        Assert.Equal(results[0].Lineas, results[1].Lineas);
        var receiptCount = await fixture.WithDb(db => db.OperacionesSincronizacion
            .CountAsync(o => o.OperacionId == request.OperacionId));
        Assert.Equal(1, receiptCount);
        Assert.Equal(results[0].RowVersion, (await Get(request.Certificacion.Id)).RowVersion);
    }

    private async Task<CertificacionDto> Get(Guid id)
    {
        var result = await fixture.Client.GetFromJsonAsync<CertificacionDto>($"/api/certificaciones/{id}");
        Assert.NotNull(result);
        return result;
    }

    private async Task AssertRejectedWithoutSave(SincronizarCertificacionRequest request, HttpStatusCode status)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/api/certificaciones/sincronizar", request);
        Assert.Equal(status, response.StatusCode);
        Assert.False(await fixture.WithDb(db => db.Certificaciones.AnyAsync(c => c.Id == request.Certificacion.Id)));
        Assert.False(await fixture.WithDb(db => db.LineasCertificacion.AnyAsync(l => l.CertificacionId == request.Certificacion.Id)));
        Assert.False(await fixture.WithDb(db => db.OperacionesSincronizacion.AnyAsync(o => o.OperacionId == request.OperacionId)));
    }
}
