using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Constructora.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Constructora.Client.Core;

public sealed class SincronizadorService(
    IDbContextFactory<SqliteLocalDbContext> contextFactory,
    HttpClient httpClient,
    ILogger<SincronizadorService> logger) : ISincronizadorService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _escritura = new(1, 1);
    private readonly SemaphoreSlim _sincronizacion = new(1, 1);

    public async Task InicializarAsync(CancellationToken cancellationToken = default)
    {
        await EscribirAsync(async db =>
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        }, cancellationToken);
    }

    public Task GuardarLocalAsync(CertificacionDto certificacion, CancellationToken cancellationToken = default)
    {
        Validar(certificacion);
        return EscribirAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ValidarCatalogoAsync(db, certificacion, cancellationToken);
            var local = await db.Certificaciones.Include(c => c.Lineas)
                .SingleOrDefaultAsync(c => c.Id == certificacion.Id, cancellationToken);
            if (local is null)
            {
                local = new CertificacionLocal { Id = certificacion.Id, RowVersion = certificacion.RowVersion,
                    VersionesEdicionJson = JsonSerializer.Serialize(new[] { certificacion.RowVersion }, Json) };
                db.Certificaciones.Add(local);
            }
            else if (local.ProyectoId != certificacion.ProyectoId)
            {
                throw new ArgumentException("Una certificación existente no puede cambiar de proyecto.", nameof(certificacion));
            }

            if (!local.TieneConflicto && local.RowVersion != certificacion.RowVersion &&
                !Deserializar<List<uint>>(local.VersionesEdicionJson).Contains(certificacion.RowVersion))
            {
                // El editor conserva una versión anterior a la última descarga remota.
                // Se guarda su trabajo, pero no se promueve silenciosamente al nuevo token.
                local.VersionServidorJson = JsonSerializer.Serialize(local.ToDto(), Json);
                local.RowVersion = certificacion.RowVersion;
                local.TieneConflicto = true;
                local.UltimoError = "El editor se abrió antes de una actualización remota. Revisa ambas versiones.";
            }
            var operaciones = await db.OperacionesPendientes.Where(o => o.CertificacionId == local.Id).ToListAsync(cancellationToken);
            if (operaciones.Any(o => o.Rechazada))
            {
                // HTTP 4xx confirma que esa operación no se aplicó. La corrección puede
                // sustituirla junto a snapshots posteriores que nunca salieron del equipo.
                if (operaciones.Any(o => o.Preparada && !o.Rechazada))
                    throw new InvalidOperationException("Existe una operación ambigua que debe confirmarse antes de corregir los datos.");
                db.OperacionesPendientes.RemoveRange(operaciones);
            }

            Aplicar(local, certificacion);
            local.RevisionLocal++;
            local.EsPendienteSincronizacion = true;
            if (!local.TieneConflicto) local.UltimoError = null;
            Encolar(db, local);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Certificación {CertificacionId} guardada localmente, revisión {Revision}",
                local.Id, local.RevisionLocal);
        }, cancellationToken);
    }

    public async Task<bool> HayConexionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (EsFalloConexion(exception, cancellationToken))
        {
            logger.LogDebug(exception, "API no disponible; se mantiene el trabajo local");
            return false;
        }
    }

    public async Task<ResumenSincronizacion> SincronizarPendientesAsync(CancellationToken cancellationToken = default)
    {
        await _sincronizacion.WaitAsync(cancellationToken);
        try
        {
            if (!await HayConexionAsync(cancellationToken)) return new(0, 0, true);
            var enviadas = 0;
            var conflictos = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operacion = await PrepararSiguienteAsync(cancellationToken);
                if (operacion is null) return new(enviadas, conflictos, false);
                try
                {
                    using var content = new StringContent(operacion.PayloadJson, Encoding.UTF8, "application/json");
                    using var response = await httpClient.PostAsync("api/certificaciones/sincronizar", content, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        var conflicto = await response.Content.ReadFromJsonAsync<ConflictoSincronizacion>(Json, cancellationToken)
                            ?? throw new JsonException("Respuesta de conflicto vacía.");
                        await RegistrarConflictoAsync(operacion.CertificacionId, conflicto, cancellationToken);
                        conflictos++;
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var detalle = await response.Content.ReadAsStringAsync(cancellationToken);
                        await RegistrarErrorAsync(operacion.CertificacionId, detalle, cancellationToken);
                        if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                        {
                            logger.LogWarning("Sincronización aplazada por respuesta HTTP {StatusCode}", response.StatusCode);
                            return new(enviadas, conflictos, true);
                        }
                        await MarcarRechazadaAsync(operacion.Id, cancellationToken);
                        throw new SincronizacionDatosException($"El servidor rechazó la certificación: {detalle}", (int)response.StatusCode);
                    }
                    var servidor = await response.Content.ReadFromJsonAsync<CertificacionDto>(Json, cancellationToken)
                        ?? throw new JsonException("Respuesta de sincronización vacía.");
                    if (servidor.Id != operacion.CertificacionId || servidor.RowVersion == 0)
                        throw new JsonException("La respuesta del servidor no identifica la certificación y su versión.");
                    await CompletarAsync(operacion, servidor, cancellationToken);
                    enviadas++;
                }
                catch (Exception exception) when (EsFalloConexion(exception, cancellationToken))
                {
                    // Una respuesta perdida conserva exactamente OperacionId y payload para el siguiente intento.
                    await RegistrarErrorAsync(operacion.CertificacionId, "Sin conexión; operación pendiente de reintento.", cancellationToken);
                    logger.LogWarning(exception, "No se pudo confirmar la operación {OperacionId}; se reintentará sin cambiarla", operacion.OperacionId);
                    return new(enviadas, conflictos, true);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Error de sincronización que requiere revisión; se conservan los datos locales");
            throw;
        }
        finally { _sincronizacion.Release(); }
    }

    public async Task<bool> CargarCatalogoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var proyectos = await LeerApiAsync<List<ProyectoDto>>("api/proyectos", cancellationToken);
            var partidas = new List<PartidaDto>();
            foreach (var proyecto in proyectos)
                partidas.AddRange(await LeerApiAsync<List<PartidaDto>>($"api/proyectos/{proyecto.Id}/partidas", cancellationToken));
            await EscribirAsync(async db =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                foreach (var p in proyectos)
                {
                    var local = await db.Proyectos.FindAsync([p.Id], cancellationToken);
                    if (local is null) { local = new ProyectoLocal { Id = p.Id }; db.Proyectos.Add(local); }
                    local.Nombre = p.Nombre; local.Codigo = p.Codigo; local.FechaInicio = p.FechaInicio;
                }
                foreach (var p in partidas)
                {
                    var local = await db.Partidas.FindAsync([p.Id], cancellationToken);
                    if (local is null) { local = new PartidaLocal { Id = p.Id }; db.Partidas.Add(local); }
                    local.ProyectoId = p.ProyectoId; local.Codigo = p.Codigo; local.Descripcion = p.Descripcion;
                    local.Unidad = p.Unidad; local.PrecioUnitario = p.PrecioUnitario; local.CantidadPrevista = p.CantidadPrevista;
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
            return true;
        }
        catch (Exception exception) when (EsFalloConexion(exception, cancellationToken))
        {
            logger.LogWarning(exception, "Catálogo remoto no disponible; se conserva el catálogo local");
            return false;
        }
    }

    public async Task<bool> ActualizarCertificacionesAsync(Guid proyectoId, CancellationToken cancellationToken = default)
    {
        await _sincronizacion.WaitAsync(cancellationToken);
        try
        {
            var certificaciones = await LeerApiAsync<List<CertificacionDto>>($"api/certificaciones?proyectoId={proyectoId}", cancellationToken);
            await EscribirAsync(async db =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                foreach (var dto in certificaciones)
                {
                    var local = await db.Certificaciones.Include(c => c.Lineas).SingleOrDefaultAsync(c => c.Id == dto.Id, cancellationToken);
                    if (local?.EsPendienteSincronizacion == true) continue;
                    if (local is null) { local = new CertificacionLocal { Id = dto.Id }; db.Certificaciones.Add(local); }
                    Aplicar(local, dto);
                    if (local.RowVersion != dto.RowVersion || local.VersionesEdicionJson == "[]")
                        local.VersionesEdicionJson = JsonSerializer.Serialize(new[] { dto.RowVersion }, Json);
                    local.RowVersion = dto.RowVersion;
                    local.RevisionLocal++;
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
            return true;
        }
        catch (Exception exception) when (EsFalloConexion(exception, cancellationToken))
        {
            logger.LogWarning(exception, "No se pudieron actualizar las certificaciones; se conservan las locales");
            return false;
        }
        finally { _sincronizacion.Release(); }
    }

    public async Task<IReadOnlyList<ProyectoDto>> ObtenerProyectosAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.Proyectos.AsNoTracking().OrderBy(p => p.Codigo).ToListAsync(cancellationToken)).Select(p => p.ToDto()).ToList();
    }

    public async Task<IReadOnlyList<PartidaDto>> ObtenerPartidasAsync(Guid proyectoId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.Partidas.AsNoTracking().Where(p => p.ProyectoId == proyectoId).OrderBy(p => p.Codigo)
            .ToListAsync(cancellationToken)).Select(p => p.ToDto()).ToList();
    }

    public async Task<IReadOnlyList<CertificacionDto>> ObtenerCertificacionesAsync(Guid proyectoId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.Certificaciones.AsNoTracking().Include(c => c.Lineas).Where(c => c.ProyectoId == proyectoId)
            .OrderBy(c => c.NumeroCertificacion).ToListAsync(cancellationToken)).Select(c => c.ToDto()).ToList();
    }

    public async Task<CertificacionDto?> ObtenerCertificacionAsync(Guid certificacionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var local = await db.Certificaciones.AsNoTracking().Include(c => c.Lineas)
            .SingleOrDefaultAsync(c => c.Id == certificacionId, cancellationToken);
        return local?.ToDto();
    }

    public async Task<IReadOnlyList<ConflictoLocal>> ObtenerConflictosAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return (await db.Certificaciones.AsNoTracking().Include(c => c.Lineas).Where(c => c.TieneConflicto)
            .ToListAsync(cancellationToken)).Select(c => new ConflictoLocal(c.Id, c.ToDto(),
                c.VersionServidorJson is null ? null : Deserializar<CertificacionDto>(c.VersionServidorJson),
                c.UltimoError ?? "La certificación fue modificada en otro equipo.")).ToList();
    }

    public async Task ResolverConflictoAsync(Guid certificacionId, CertificacionDto versionResuelta, CancellationToken cancellationToken = default)
    {
        Validar(versionResuelta);
        if (certificacionId != versionResuelta.Id) throw new ArgumentException("El identificador de la resolución no coincide.", nameof(versionResuelta));
        await _sincronizacion.WaitAsync(cancellationToken);
        try
        {
            await EscribirAsync(async db =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var local = await db.Certificaciones.Include(c => c.Lineas).SingleAsync(c => c.Id == certificacionId, cancellationToken);
                if (!local.TieneConflicto) throw new InvalidOperationException("La certificación no tiene un conflicto pendiente.");
                if (local.ProyectoId != versionResuelta.ProyectoId) throw new ArgumentException("No se puede cambiar el proyecto al resolver un conflicto.");
                await ValidarCatalogoAsync(db, versionResuelta, cancellationToken);
                local.RowVersion = local.VersionServidorJson is null ? 0 : Deserializar<CertificacionDto>(local.VersionServidorJson).RowVersion;
                local.VersionesEdicionJson = JsonSerializer.Serialize(new[] { local.RowVersion }, Json);
                Aplicar(local, versionResuelta);
                local.RevisionLocal++;
                local.TieneConflicto = false;
                local.VersionServidorJson = null;
                local.UltimoError = null;
                local.EsPendienteSincronizacion = true;
                db.OperacionesPendientes.RemoveRange(await db.OperacionesPendientes.Where(o => o.CertificacionId == certificacionId).ToListAsync(cancellationToken));
                Encolar(db, local);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }, cancellationToken);
        }
        finally { _sincronizacion.Release(); }
    }

    public async Task AceptarVersionServidorAsync(Guid certificacionId, CancellationToken cancellationToken = default)
    {
        await _sincronizacion.WaitAsync(cancellationToken);
        try
        {
            await EscribirAsync(async db =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var local = await db.Certificaciones.Include(c => c.Lineas)
                    .SingleAsync(c => c.Id == certificacionId, cancellationToken);
                if (!local.TieneConflicto || local.VersionServidorJson is null)
                    throw new InvalidOperationException("El conflicto no contiene una versión del servidor que se pueda aceptar.");
                var servidor = Deserializar<CertificacionDto>(local.VersionServidorJson);
                if (servidor.Id != local.Id || servidor.ProyectoId != local.ProyectoId)
                    throw new InvalidOperationException("La versión del servidor no corresponde a la certificación local.");
                Aplicar(local, servidor);
                local.RowVersion = servidor.RowVersion;
                local.VersionesEdicionJson = JsonSerializer.Serialize(new[] { servidor.RowVersion }, Json);
                local.RevisionLocal++;
                local.EsPendienteSincronizacion = false;
                local.TieneConflicto = false;
                local.VersionServidorJson = null;
                local.UltimoError = null;
                db.OperacionesPendientes.RemoveRange(await db.OperacionesPendientes
                    .Where(o => o.CertificacionId == certificacionId).ToListAsync(cancellationToken));
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Versión del servidor aceptada localmente para certificación {CertificacionId}", certificacionId);
            }, cancellationToken);
        }
        finally { _sincronizacion.Release(); }
    }

    private async Task<OperacionSincronizacionLocal?> PrepararSiguienteAsync(CancellationToken cancellationToken)
    {
        OperacionSincronizacionLocal? resultado = null;
        await EscribirAsync(async db =>
        {
            var operacion = await db.OperacionesPendientes
                .Where(o => !o.Rechazada && db.Certificaciones.Any(c => c.Id == o.CertificacionId && !c.TieneConflicto)
                    && !db.OperacionesPendientes.Any(rechazada => rechazada.CertificacionId == o.CertificacionId && rechazada.Rechazada))
                .OrderBy(o => o.Id).FirstOrDefaultAsync(cancellationToken);
            if (operacion is null) return;
            if (!operacion.Preparada)
            {
                var local = await db.Certificaciones.SingleAsync(c => c.Id == operacion.CertificacionId, cancellationToken);
                var solicitud = Deserializar<SincronizarCertificacionRequest>(operacion.PayloadJson);
                operacion.PayloadJson = JsonSerializer.Serialize(solicitud with
                { Certificacion = solicitud.Certificacion with { RowVersion = local.RowVersion } }, Json);
                operacion.Preparada = true;
                await db.SaveChangesAsync(cancellationToken);
            }
            resultado = operacion;
        }, cancellationToken);
        return resultado;
    }

    private Task CompletarAsync(OperacionSincronizacionLocal operacion, CertificacionDto servidor, CancellationToken cancellationToken) =>
        EscribirAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var local = await db.Certificaciones.Include(c => c.Lineas).SingleAsync(c => c.Id == operacion.CertificacionId, cancellationToken);
            var pendiente = await db.OperacionesPendientes.SingleAsync(o => o.Id == operacion.Id, cancellationToken);
            if (local.RevisionLocal == operacion.RevisionLocal) Aplicar(local, servidor);
            local.RowVersion = servidor.RowVersion;
            var versiones = Deserializar<List<uint>>(local.VersionesEdicionJson);
            if (!versiones.Contains(servidor.RowVersion)) versiones.Add(servidor.RowVersion);
            local.VersionesEdicionJson = JsonSerializer.Serialize(versiones, Json);
            local.UltimoError = null;
            db.OperacionesPendientes.Remove(pendiente);
            local.EsPendienteSincronizacion = await db.OperacionesPendientes.AnyAsync(o => o.CertificacionId == local.Id && o.Id != operacion.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    private Task RegistrarConflictoAsync(Guid certificacionId, ConflictoSincronizacion conflicto, CancellationToken cancellationToken) =>
        EscribirAsync(async db =>
        {
            var local = await db.Certificaciones.SingleAsync(c => c.Id == certificacionId, cancellationToken);
            local.TieneConflicto = true;
            local.EsPendienteSincronizacion = true;
            local.UltimoError = conflicto.Mensaje;
            local.VersionServidorJson = conflicto.VersionServidor is null ? null : JsonSerializer.Serialize(conflicto.VersionServidor, Json);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Conflicto en certificación {CertificacionId}; se requiere resolución explícita", certificacionId);
        }, cancellationToken);

    private Task RegistrarErrorAsync(Guid certificacionId, string mensaje, CancellationToken cancellationToken) =>
        EscribirAsync(async db =>
        {
            var local = await db.Certificaciones.SingleAsync(c => c.Id == certificacionId, cancellationToken);
            local.UltimoError = mensaje;
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    private Task MarcarRechazadaAsync(long operacionId, CancellationToken cancellationToken) =>
        EscribirAsync(async db =>
        {
            var operacion = await db.OperacionesPendientes.SingleAsync(o => o.Id == operacionId, cancellationToken);
            operacion.Rechazada = true;
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    private async Task EscribirAsync(Func<SqliteLocalDbContext, Task> accion, CancellationToken cancellationToken)
    {
        await _escritura.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await accion(db);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "No se pudo completar la operación sobre la base local");
            throw;
        }
        finally { _escritura.Release(); }
    }

    private async Task<T> LeerApiAsync<T>(string ruta, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ruta, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("API temporalmente no disponible.", null, response.StatusCode);
            throw new SincronizacionDatosException($"No se pudo leer {ruta}: HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken) ?? throw new JsonException($"Respuesta vacía: {ruta}.");
    }

    private static void Encolar(SqliteLocalDbContext db, CertificacionLocal local)
    {
        var operacionId = Guid.NewGuid();
        db.OperacionesPendientes.Add(new OperacionSincronizacionLocal
        {
            OperacionId = operacionId, CertificacionId = local.Id, RevisionLocal = local.RevisionLocal,
            PayloadJson = JsonSerializer.Serialize(new SincronizarCertificacionRequest(operacionId, local.ToDto()), Json)
        });
    }

    private static void Aplicar(CertificacionLocal local, CertificacionDto dto)
    {
        local.ProyectoId = dto.ProyectoId; local.NumeroCertificacion = dto.NumeroCertificacion;
        local.Fecha = dto.Fecha; local.Estado = dto.Estado;
        local.Lineas.RemoveAll(l => dto.Lineas.All(d => d.Id != l.Id));
        foreach (var linea in dto.Lineas)
        {
            var existente = local.Lineas.SingleOrDefault(l => l.Id == linea.Id);
            if (existente is null)
            {
                existente = new LineaCertificacionLocal { Id = linea.Id, CertificacionId = dto.Id };
                local.Lineas.Add(existente);
            }
            existente.PartidaId = linea.PartidaId; existente.CantidadAcumuladaAnterior = linea.CantidadAcumuladaAnterior;
            existente.CantidadPeriodo = linea.CantidadPeriodo; existente.PorcentajeAvance = linea.PorcentajeAvance;
        }
    }

    private static async Task ValidarCatalogoAsync(SqliteLocalDbContext db, CertificacionDto dto, CancellationToken cancellationToken)
    {
        var proyecto = await db.Proyectos.SingleOrDefaultAsync(p => p.Id == dto.ProyectoId, cancellationToken);
        if (proyecto is null)
            throw new InvalidOperationException("Descarga el catálogo del proyecto antes de trabajar sin conexión.");
        if (dto.Fecha < proyecto.FechaInicio)
            throw new ArgumentException("La fecha de certificación no puede ser anterior al inicio del proyecto.", nameof(dto));
        var ids = dto.Lineas.Select(l => l.PartidaId).ToArray();
        if (await db.Partidas.CountAsync(p => ids.Contains(p.Id) && p.ProyectoId == dto.ProyectoId, cancellationToken) != ids.Length)
            throw new ArgumentException("Todas las partidas deben pertenecer al proyecto y estar disponibles localmente.", nameof(dto));
    }

    private static void Validar(CertificacionDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.Id == Guid.Empty || dto.ProyectoId == Guid.Empty || dto.NumeroCertificacion <= 0 ||
            dto.Fecha == default || !Enum.IsDefined(dto.Estado))
            throw new ArgumentException("Identificadores, número y estado de certificación no válidos.", nameof(dto));
        if (dto.Lineas is null || dto.Lineas.Count > 10000 || dto.Lineas.Any(l => l is null || l.Id == Guid.Empty || l.PartidaId == Guid.Empty ||
            !CantidadValida(l.CantidadAcumuladaAnterior) || !CantidadValida(l.CantidadPeriodo) ||
            l.PorcentajeAvance is < 0 or > 100 || decimal.Round(l.PorcentajeAvance, 4) != l.PorcentajeAvance) ||
            dto.Lineas.Select(l => l.Id).Distinct().Count() != dto.Lineas.Count ||
            dto.Lineas.Select(l => l.PartidaId).Distinct().Count() != dto.Lineas.Count)
            throw new ArgumentException("Las líneas necesitan identificadores únicos, cantidades no negativas y avances entre 0 y 100.", nameof(dto));
    }

    private static bool CantidadValida(decimal cantidad) => cantidad is >= 0 and <= 99999999999999.9999m && decimal.Round(cantidad, 4) == cantidad;

    private static bool EsFalloConexion(Exception exception, CancellationToken token) =>
        exception is HttpRequestException || (exception is OperationCanceledException && !token.IsCancellationRequested);

    private static T Deserializar<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new JsonException("La operación local contiene datos vacíos.");

    public void Dispose()
    {
        _escritura.Dispose();
        _sincronizacion.Dispose();
        GC.SuppressFinalize(this);
    }
}
