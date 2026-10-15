using System.Security.Cryptography;
using System.Text.Json;
using Constructora.Application;
using Constructora.Domain;
using Constructora.Infrastructure.Server;
using Constructora.Server.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Constructora.Server.Controllers;

[ApiController]
[Route("api/certificaciones")]
public sealed class CertificacionesController(
    ServidorDbContext db,
    IHubContext<CertificacionHub> hub,
    ILogger<CertificacionesController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const decimal MaxCantidad = 99999999999999.9999m;

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CertificacionDto>> Obtener(Guid id, CancellationToken cancellationToken)
    {
        var certificacion = await db.Certificaciones.AsNoTracking().Include(c => c.Lineas)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return certificacion is null
            ? NotFound(new ProblemDetails { Title = "La certificación no existe.", Status = 404 })
            : Ok(certificacion.ToDto());
    }

    [HttpGet]
    public async Task<ActionResult<List<CertificacionDto>>> ObtenerPorProyecto(
        [FromQuery] Guid proyectoId, CancellationToken cancellationToken)
    {
        if (proyectoId == Guid.Empty)
            return BadRequest(new ProblemDetails { Title = "Debe indicar proyectoId.", Status = 400 });
        var certificaciones = await db.Certificaciones.AsNoTracking().Include(c => c.Lineas)
            .Where(c => c.ProyectoId == proyectoId).OrderBy(c => c.NumeroCertificacion)
            .ToListAsync(cancellationToken);
        return certificaciones.Select(c => c.ToDto()).ToList();
    }

    [HttpPost("sincronizar")]
    public async Task<IActionResult> Sincronizar(
        [FromBody] SincronizarCertificacionRequest request, CancellationToken cancellationToken)
    {
        var errores = ValidarSolicitud(request);
        if (errores.Count != 0)
            return ValidationProblem(new ValidationProblemDetails(errores));

        // Canonical line ordering means an unchanged operation may be retried after a reload.
        var dto = request.Certificacion;
        var canonica = dto with { Lineas = dto.Lineas.OrderBy(l => l.Id).ToList() };
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonica, JsonOptions)));
        CertificacionDto respuesta;

        try
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var repetida = await RecuperarOperacionAsync(request.OperacionId, hash, cancellationToken);
                if (repetida is not null)
                    return repetida;

                var proyecto = await db.Proyectos.AsNoTracking()
                    .SingleOrDefaultAsync(p => p.Id == dto.ProyectoId, cancellationToken);
                if (proyecto is null)
                    return ErrorValidacion("Certificacion.ProyectoId", "El proyecto no existe.");
                if (dto.Fecha < proyecto.FechaInicio)
                    return ErrorValidacion("Certificacion.Fecha", "La fecha no puede ser anterior al inicio del proyecto.");

                var existente = await db.Certificaciones.Include(c => c.Lineas)
                    .SingleOrDefaultAsync(c => c.Id == dto.Id, cancellationToken);
                if (existente is null && dto.RowVersion != 0)
                    return Conflict(new ConflictoSincronizacion("La certificación ya no existe en el servidor.", null));
                if (existente is not null)
                {
                    if (existente.RowVersion != dto.RowVersion)
                        return Conflict(new ConflictoSincronizacion("Otro usuario ha modificado la certificación.", existente.ToDto()));
                    if (existente.ProyectoId != dto.ProyectoId)
                        return ErrorValidacion("Certificacion.ProyectoId", "No se puede trasladar una certificación a otro proyecto.");
                    if (existente.Estado == EstadoCertificacion.Facturada)
                        return ErrorValidacion("Certificacion.Estado", "Una certificación facturada no se puede modificar.");
                    if (dto.Estado < existente.Estado)
                        return ErrorValidacion("Certificacion.Estado", "El estado de una certificación no puede retroceder.");
                }

                var idsPartidas = dto.Lineas.Select(l => l.PartidaId).ToArray();
                var partidasValidas = await db.Partidas.AsNoTracking()
                    .CountAsync(p => idsPartidas.Contains(p.Id) && p.ProyectoId == dto.ProyectoId, cancellationToken);
                if (partidasValidas != idsPartidas.Length)
                    return ErrorValidacion("Certificacion.Lineas", "Todas las partidas deben existir y pertenecer al proyecto.");

                var idsLineas = dto.Lineas.Select(l => l.Id).ToArray();
                if (await db.LineasCertificacion.AsNoTracking().AnyAsync(
                        l => idsLineas.Contains(l.Id) && l.CertificacionId != dto.Id, cancellationToken))
                    return ErrorValidacion("Certificacion.Lineas", "Una línea pertenece a otra certificación.");

                var certificacion = existente ?? new Certificacion
                {
                    Id = dto.Id,
                    ProyectoId = dto.ProyectoId
                };
                if (existente is null)
                    db.Certificaciones.Add(certificacion);
                else
                {
                    // xmin is generated by PostgreSQL. Never assign the client's token as its new value.
                    db.Entry(certificacion).Property(c => c.RowVersion).OriginalValue = dto.RowVersion;
                    var porId = existente.Lineas.ToDictionary(l => l.Id);
                    if (dto.Lineas.Any(l => porId.TryGetValue(l.Id, out var actual) && actual.PartidaId != l.PartidaId))
                        return ErrorValidacion("Certificacion.Lineas", "Para cambiar una partida, elimine la línea y cree otra con un identificador nuevo.");
                }

                certificacion.NumeroCertificacion = dto.NumeroCertificacion;
                certificacion.Fecha = dto.Fecha;
                certificacion.Estado = dto.Estado;
                certificacion.ActualizadaUtc = DateTime.UtcNow;
                if (existente is not null)
                {
                    // Every aggregate mutation, including line-only edits, must compare and advance xmin.
                    db.Entry(certificacion).Property(c => c.ActualizadaUtc).IsModified = true;
                    foreach (var eliminada in certificacion.Lineas.Where(l => !idsLineas.Contains(l.Id)).ToList())
                    {
                        certificacion.Lineas.Remove(eliminada);
                        db.LineasCertificacion.Remove(eliminada);
                    }
                }

                var lineasExistentes = certificacion.Lineas.ToDictionary(l => l.Id);
                foreach (var entrada in dto.Lineas)
                {
                    if (!lineasExistentes.TryGetValue(entrada.Id, out var linea))
                    {
                        linea = new LineaCertificacion
                        {
                            Id = entrada.Id,
                            CertificacionId = dto.Id,
                            PartidaId = entrada.PartidaId
                        };
                        certificacion.Lineas.Add(linea);
                        // Client-generated GUIDs must still be inserted when the parent already exists.
                        db.LineasCertificacion.Add(linea);
                    }
                    linea.CantidadAcumuladaAnterior = entrada.CantidadAcumuladaAnterior;
                    linea.CantidadPeriodo = entrada.CantidadPeriodo;
                    linea.PorcentajeAvance = entrada.PorcentajeAvance;
                }

                await db.SaveChangesAsync(cancellationToken);
                respuesta = certificacion.ToDto();
                db.OperacionesSincronizacion.Add(new OperacionSincronizacion
                {
                    OperacionId = request.OperacionId,
                    CertificacionId = dto.Id,
                    HashSolicitud = hash,
                    RespuestaJson = JsonSerializer.Serialize(respuesta, JsonOptions),
                    CompletadaUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Conflicto de concurrencia al sincronizar {CertificacionId}.", dto.Id);
            db.ChangeTracker.Clear();
            // Another retry of the same operation may have committed while this request waited.
            return await RecuperarOperacionAsync(request.OperacionId, hash, cancellationToken)
                ?? await CrearConflictoAsync(dto.Id, "Otro usuario ha modificado la certificación.", cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
               { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation })
        {
            logger.LogWarning(ex, "Conflicto de integridad al sincronizar {CertificacionId}.", dto.Id);
            db.ChangeTracker.Clear();
            return await RecuperarOperacionAsync(request.OperacionId, hash, cancellationToken)
                ?? await CrearConflictoAsync(dto.Id,
                    "La operación entra en conflicto con datos existentes o modificados simultáneamente.", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo sincronizar la certificación {CertificacionId}.", dto.Id);
            return Problem(title: "No se pudo guardar la certificación.", statusCode: StatusCodes.Status500InternalServerError);
        }

        try
        {
            // A notification failure cannot turn a committed save into an apparent failure.
            await hub.Clients.All.SendAsync("CertificacionActualizada",
                new NotificacionCertificacion(dto.ProyectoId, dto.Id), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "La certificación {CertificacionId} se guardó, pero no se pudo publicar la notificación.", dto.Id);
        }
        return Ok(respuesta);
    }

    private async Task<IActionResult?> RecuperarOperacionAsync(Guid operacionId, string hash, CancellationToken cancellationToken)
    {
        var recibo = await db.OperacionesSincronizacion.AsNoTracking()
            .SingleOrDefaultAsync(o => o.OperacionId == operacionId, cancellationToken);
        if (recibo is null)
            return null;
        if (!string.Equals(recibo.HashSolicitud, hash, StringComparison.Ordinal))
            return Conflict(new ConflictoSincronizacion("El identificador de operación ya se utilizó con otros datos.", null));
        var respuesta = JsonSerializer.Deserialize<CertificacionDto>(recibo.RespuestaJson, JsonOptions)
            ?? throw new InvalidOperationException("El recibo de sincronización no contiene una respuesta válida.");
        return Ok(respuesta);
    }

    private async Task<IActionResult> CrearConflictoAsync(Guid certificacionId, string mensaje, CancellationToken cancellationToken)
    {
        var actual = await db.Certificaciones.AsNoTracking().Include(c => c.Lineas)
            .SingleOrDefaultAsync(c => c.Id == certificacionId, cancellationToken);
        return Conflict(new ConflictoSincronizacion(mensaje, actual?.ToDto()));
    }

    private ActionResult ErrorValidacion(string campo, string mensaje) =>
        ValidationProblem(new ValidationProblemDetails(new Dictionary<string, string[]> { [campo] = [mensaje] }));

    private static Dictionary<string, string[]> ValidarSolicitud(SincronizarCertificacionRequest request)
    {
        var errores = new Dictionary<string, string[]>();
        if (request.OperacionId == Guid.Empty)
            errores["OperacionId"] = ["Debe indicar un identificador de operación no vacío y conservarlo en los reintentos."];
        if (request.Certificacion is null)
        {
            errores["Certificacion"] = ["Debe indicar la certificación."];
            return errores;
        }
        var c = request.Certificacion;
        if (c.Id == Guid.Empty || c.ProyectoId == Guid.Empty)
            errores["Certificacion.Id"] = ["La certificación y el proyecto deben tener identificadores no vacíos."];
        if (c.NumeroCertificacion <= 0)
            errores["Certificacion.NumeroCertificacion"] = ["El número de certificación debe ser positivo."];
        if (c.Fecha == default)
            errores["Certificacion.Fecha"] = ["Debe indicar una fecha válida."];
        if (!Enum.IsDefined(c.Estado))
            errores["Certificacion.Estado"] = ["El estado no es válido."];
        if (c.Lineas is null || c.Lineas.Count > 10000 || c.Lineas.Any(l => l is null))
        {
            errores["Certificacion.Lineas"] = ["Debe enviar una lista de hasta 10000 líneas válidas."];
            return errores;
        }
        if (c.Lineas.Select(l => l.Id).Distinct().Count() != c.Lineas.Count ||
            c.Lineas.Select(l => l.PartidaId).Distinct().Count() != c.Lineas.Count)
            errores["Certificacion.Lineas"] = ["No se pueden repetir identificadores de línea ni partidas."];
        for (var i = 0; i < c.Lineas.Count; i++)
        {
            var l = c.Lineas[i];
            if (l.Id == Guid.Empty || l.PartidaId == Guid.Empty)
                errores[$"Certificacion.Lineas[{i}].Id"] = ["La línea y la partida deben tener identificadores no vacíos."];
            if (!CantidadValida(l.CantidadAcumuladaAnterior) || !CantidadValida(l.CantidadPeriodo))
                errores[$"Certificacion.Lineas[{i}].CantidadPeriodo"] = ["Las cantidades deben ser positivas o cero, con hasta 14 enteros y 4 decimales."];
            if (l.PorcentajeAvance is < 0 or > 100 || decimal.Round(l.PorcentajeAvance, 4) != l.PorcentajeAvance)
                errores[$"Certificacion.Lineas[{i}].PorcentajeAvance"] = ["El avance debe estar entre 0 y 100, con hasta 4 decimales."];
        }
        return errores;
    }

    private static bool CantidadValida(decimal cantidad) =>
        cantidad >= 0 && cantidad <= MaxCantidad && decimal.Round(cantidad, 4) == cantidad;
}
