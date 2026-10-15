using System.Security.Cryptography;
using System.Text.Json;
using Constructora.Application.Bim;
using Constructora.Client.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Constructora.Client.Bim;

public sealed class BimService(
    IDbContextFactory<BimLocalDbContext> bimFactory,
    IDbContextFactory<SqliteLocalDbContext> catalogoFactory,
    IImportadorIfc importador,
    ILogger<BimService> logger,
    string archivoDir) : IBimService, IDisposable
{
    public const long MaximoBytesIfc = 256L * 1024 * 1024;
    private readonly string _archivoDir = Path.GetFullPath(archivoDir);
    private readonly SemaphoreSlim _escritura = new(1, 1);

    public async Task InicializarAsync(CancellationToken cancellationToken = default)
    {
        await _escritura.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_archivoDir);
            await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }
        finally { _escritura.Release(); }
    }

    public async Task<IReadOnlyList<ModeloBimDto>> ObtenerModelosAsync(Guid proyectoId, CancellationToken cancellationToken = default)
    {
        await ValidarProyectoAsync(proyectoId, cancellationToken);
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        return await db.Modelos.AsNoTracking().Where(x => x.ProyectoId == proyectoId)
            .OrderBy(x => x.Nombre).Select(x => new ModeloBimDto(x.Id, x.ProyectoId, x.Nombre, x.CreadoUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<VersionModeloBimDto> ImportarAsync(Guid proyectoId, Guid? modeloId, string rutaArchivo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rutaArchivo);
        if (!string.Equals(Path.GetExtension(rutaArchivo), ".ifc", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Selecciona un archivo IFC sin comprimir (.ifc).", nameof(rutaArchivo));
        await _escritura.WaitAsync(cancellationToken);
        string? archivoPropio = null;
        try
        {
            await ValidarProyectoAsync(proyectoId, cancellationToken);
            await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
            ModeloBimLocal? modelo = null;
            if (modeloId is not null)
            {
                modelo = await db.Modelos.SingleOrDefaultAsync(x => x.Id == modeloId, cancellationToken)
                    ?? throw new KeyNotFoundException("El modelo seleccionado no existe.");
                if (modelo.ProyectoId != proyectoId)
                    throw new ArgumentException("El modelo pertenece a otro proyecto.", nameof(modeloId));
            }
            Directory.CreateDirectory(_archivoDir);
            var nombrePropio = $"{Guid.NewGuid():N}.ifc";
            var archivoNuevo = Path.Combine(_archivoDir, nombrePropio);
            var hash = await CopiarYCalcularHashAsync(rutaArchivo, archivoNuevo, cancellationToken);
            archivoPropio = archivoNuevo;
            if (modelo is not null)
            {
                var existente = await db.Versiones.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ModeloId == modelo.Id && x.Sha256 == hash, cancellationToken);
                if (existente is not null)
                {
                    File.Delete(archivoPropio);
                    archivoPropio = null;
                    return ToDto(existente);
                }
            }
            // Parse the private copy, so later edits/replacements of the selected file cannot alter history.
            var importacion = await importador.ImportarAsync(archivoPropio, cancellationToken);
            ValidarImportacion(importacion);
            if (modelo?.ProyectoIfcGlobalId is { } proyectoIfc && importacion.ProyectoIfcGlobalId is { } nuevoProyectoIfc
                && !string.Equals(proyectoIfc, nuevoProyectoIfc, StringComparison.Ordinal))
                throw new InvalidDataException("El IFC corresponde a otro IfcProject. Impórtalo como un modelo nuevo.");
            if (modelo is null)
            {
                modelo = new ModeloBimLocal
                {
                    Id = Guid.NewGuid(), ProyectoId = proyectoId, Nombre = Path.GetFileNameWithoutExtension(rutaArchivo),
                    CreadoUtc = DateTime.UtcNow, ProyectoIfcGlobalId = importacion.ProyectoIfcGlobalId
                };
                db.Modelos.Add(modelo);
            }
            else if (modelo.ProyectoIfcGlobalId is null)
                modelo.ProyectoIfcGlobalId = importacion.ProyectoIfcGlobalId;
            var numero = (await db.Versiones.Where(x => x.ModeloId == modelo.Id)
                .MaxAsync(x => (int?)x.Numero, cancellationToken) ?? 0) + 1;
            var version = new VersionModeloBimLocal
            {
                Id = Guid.NewGuid(), ModeloId = modelo.Id, Numero = numero,
                NombreArchivo = Path.GetFileName(rutaArchivo), ArchivoArchivado = nombrePropio,
                Sha256 = hash, ImportadaUtc = DateTime.UtcNow,
                ImportacionJson = JsonSerializer.Serialize(importacion)
            };
            db.Versiones.Add(version);
            await db.SaveChangesAsync(cancellationToken);
            archivoPropio = null;
            logger.LogInformation("Modelo BIM {ModeloId}, revisión {Numero}: {Cantidad} elementos importados.",
                modelo.Id, numero, importacion.Elementos.Count);
            return ToDto(version);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "No se pudo importar la revisión IFC del proyecto {ProyectoId}.", proyectoId);
            throw;
        }
        finally
        {
            if (archivoPropio is not null) EliminarArchivoPropio(archivoPropio);
            _escritura.Release();
        }
    }

    public async Task<IReadOnlyList<VersionModeloBimDto>> ObtenerVersionesAsync(Guid modeloId, CancellationToken cancellationToken = default)
    {
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        await ExigirModeloAsync(db, modeloId, cancellationToken);
        var versiones = await db.Versiones.AsNoTracking().Where(x => x.ModeloId == modeloId)
            .OrderByDescending(x => x.Numero).ToListAsync(cancellationToken);
        return versiones.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ElementoModeloBimDto>> ObtenerElementosAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        return LeerImportacion(await ExigirVersionAsync(db, versionId, cancellationToken)).Elementos;
    }

    public async Task<IReadOnlyList<VinculoElementoPartidaBimDto>> ObtenerVinculosAsync(Guid modeloId, CancellationToken cancellationToken = default)
    {
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        await ExigirModeloAsync(db, modeloId, cancellationToken);
        var vinculos = await db.Vinculos.AsNoTracking().Where(x => x.ModeloId == modeloId).ToListAsync(cancellationToken);
        return vinculos.Select(ToDto).ToList();
    }

    public async Task<VinculoElementoPartidaBimDto> VincularAsync(Guid modeloId, string globalId, Guid partidaId,
        ReglaMedicionBim regla, decimal factor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalId);
        if (!Enum.IsDefined(regla)) throw new ArgumentOutOfRangeException(nameof(regla));
        if (factor <= 0 || factor > 1_000_000_000m || decimal.Round(factor, 6) != factor)
            throw new ArgumentOutOfRangeException(nameof(factor), "El factor debe ser positivo, hasta 1.000.000.000 y con hasta seis decimales.");
        await _escritura.WaitAsync(cancellationToken);
        try
        {
            await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
            var modelo = await ExigirModeloAsync(db, modeloId, cancellationToken);
            await using var catalogo = await catalogoFactory.CreateDbContextAsync(cancellationToken);
            var partida = await catalogo.Partidas.AsNoTracking().SingleOrDefaultAsync(x => x.Id == partidaId, cancellationToken)
                ?? throw new KeyNotFoundException("La partida no existe en el catálogo local.");
            if (partida.ProyectoId != modelo.ProyectoId)
                throw new ArgumentException("La partida pertenece a otro proyecto.", nameof(partidaId));
            if (!UnidadCompatible(partida.Unidad, regla))
                throw new ArgumentException($"La unidad '{partida.Unidad}' es incompatible con la regla seleccionada.", nameof(regla));
            var versiones = await db.Versiones.AsNoTracking().Where(x => x.ModeloId == modeloId).ToListAsync(cancellationToken);
            if (!versiones.Any(x => LeerImportacion(x).Elementos.Any(e => e.GlobalId == globalId)))
                throw new KeyNotFoundException("El elemento no existe en ninguna revisión de este modelo.");
            var vinculo = await db.Vinculos.SingleOrDefaultAsync(x => x.ModeloId == modeloId && x.GlobalId == globalId
                && x.PartidaId == partidaId, cancellationToken);
            if (vinculo is null)
            {
                vinculo = new VinculoElementoPartidaBimLocal
                { Id = Guid.NewGuid(), ModeloId = modeloId, GlobalId = globalId, PartidaId = partidaId, Regla = regla };
                db.Vinculos.Add(vinculo);
            }
            // A different rule replaces the previous rule for this element/partida; net/gross alternatives are not additive.
            vinculo.Regla = regla;
            vinculo.Factor = factor;
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(vinculo);
        }
        finally { _escritura.Release(); }
    }

    public async Task EliminarVinculoAsync(Guid vinculoId, CancellationToken cancellationToken = default)
    {
        await _escritura.WaitAsync(cancellationToken);
        try
        {
            await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
            var vinculo = await db.Vinculos.FindAsync([vinculoId], cancellationToken);
            if (vinculo is null) return;
            db.Vinculos.Remove(vinculo);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { _escritura.Release(); }
    }

    public async Task<IReadOnlyList<MedicionVinculadaBimDto>> ObtenerMedicionesAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        var version = await ExigirVersionAsync(db, versionId, cancellationToken);
        var modelo = await ExigirModeloAsync(db, version.ModeloId, cancellationToken);
        var elementos = LeerImportacion(version).Elementos.ToDictionary(x => x.GlobalId, StringComparer.Ordinal);
        var vinculos = await db.Vinculos.AsNoTracking().Where(x => x.ModeloId == modelo.Id).ToListAsync(cancellationToken);
        await using var catalogo = await catalogoFactory.CreateDbContextAsync(cancellationToken);
        var partidas = await catalogo.Partidas.AsNoTracking().Where(x => x.ProyectoId == modelo.ProyectoId)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        return vinculos.Where(v => elementos.ContainsKey(v.GlobalId))
            .Select(v => Medir(version.Id, elementos[v.GlobalId], v, partidas.GetValueOrDefault(v.PartidaId)))
            .OrderBy(x => x.CodigoPartida, StringComparer.Ordinal).ThenBy(x => x.GlobalId, StringComparer.Ordinal).ToList();
    }

    public async Task<ComparacionVersionesBimDto> CompararAsync(Guid versionAnteriorId, Guid versionNuevaId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        var anterior = await ExigirVersionAsync(db, versionAnteriorId, cancellationToken);
        var nueva = await ExigirVersionAsync(db, versionNuevaId, cancellationToken);
        if (anterior.ModeloId != nueva.ModeloId)
            throw new ArgumentException("Sólo se pueden comparar revisiones del mismo modelo.");
        if (anterior.Numero >= nueva.Numero)
            throw new ArgumentException("Selecciona una revisión nueva posterior a la revisión anterior.");
        var modelo = await ExigirModeloAsync(db, anterior.ModeloId, cancellationToken);
        var datosAnteriores = LeerImportacion(anterior);
        var datosNuevos = LeerImportacion(nueva);
        var antiguos = datosAnteriores.Elementos.ToDictionary(x => x.GlobalId, StringComparer.Ordinal);
        var nuevos = datosNuevos.Elementos.ToDictionary(x => x.GlobalId, StringComparer.Ordinal);
        var cambios = new List<CambioElementoBimDto>();
        foreach (var id in antiguos.Keys.Union(nuevos.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var a = antiguos.GetValueOrDefault(id);
            var n = nuevos.GetValueOrDefault(id);
            if (a is null) cambios.Add(new(id, TipoCambioBim.Anadido, null, n));
            else if (n is null) cambios.Add(new(id, TipoCambioBim.Eliminado, a, null));
            else if (ElementoModificado(a, n)) cambios.Add(new(id, TipoCambioBim.Modificado, a, n));
        }
        var vinculos = (await db.Vinculos.AsNoTracking().Where(x => x.ModeloId == modelo.Id).ToListAsync(cancellationToken))
            .ToLookup(x => x.GlobalId, StringComparer.Ordinal);
        await using var catalogo = await catalogoFactory.CreateDbContextAsync(cancellationToken);
        var partidas = await catalogo.Partidas.AsNoTracking().Where(x => x.ProyectoId == modelo.ProyectoId)
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var advertencias = new List<string>
        {
            "Estimación con vínculos y precios actuales del catálogo; no modifica cantidades ejecutadas ni certificaciones aprobadas."
        };
        advertencias.AddRange(datosAnteriores.Advertencias.Concat(datosNuevos.Advertencias).Distinct(StringComparer.Ordinal));
        decimal impacto = 0;
        var sinValorar = 0;
        foreach (var cambio in cambios)
        {
            var enlaces = vinculos[cambio.GlobalId].ToList();
            var completo = enlaces.Count > 0;
            foreach (var enlace in enlaces)
            {
                var partida = partidas.GetValueOrDefault(enlace.PartidaId);
                var antes = cambio.Anterior is null ? 0m : Medir(anterior.Id, cambio.Anterior, enlace, partida).Importe;
                var despues = cambio.Nuevo is null ? 0m : Medir(nueva.Id, cambio.Nuevo, enlace, partida).Importe;
                if (antes is null || despues is null) completo = false;
                else impacto += despues.Value - antes.Value;
            }
            if (!completo) sinValorar++;
        }
        if (sinValorar > 0)
            advertencias.Add($"Valoración parcial: {sinValorar} elementos cambiados sin vínculo, cantidad compatible o tarifa disponible. El importe conocido no representa el coste total.");
        return new(versionAnteriorId, versionNuevaId, cambios, impacto, sinValorar, advertencias);
    }

    public async Task ExportarOriginalAsync(Guid versionId, string rutaDestino, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rutaDestino);
        var destino = Path.GetFullPath(rutaDestino);
        if (File.Exists(destino)) throw new IOException("El archivo de destino ya existe. Elige otro nombre para conservarlo.");
        await using var db = await bimFactory.CreateDbContextAsync(cancellationToken);
        var version = await ExigirVersionAsync(db, versionId, cancellationToken);
        if (version.ArchivoArchivado != Path.GetFileName(version.ArchivoArchivado))
            throw new InvalidDataException("La referencia al archivo original no es válida.");
        var original = Path.Combine(_archivoDir, version.ArchivoArchivado);
        var temporal = Path.Combine(Path.GetDirectoryName(destino)!, $".bim-export-{Guid.NewGuid():N}.tmp");
        var temporalPropio = false;
        try
        {
            var hash = await CopiarYCalcularHashAsync(original, temporal, cancellationToken);
            temporalPropio = true;
            if (!string.Equals(hash, version.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("El original archivado no coincide con su SHA-256. No se ha exportado.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporal, destino, overwrite: false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "No se pudo exportar el original BIM de la revisión {VersionId}.", versionId);
            throw;
        }
        finally { if (temporalPropio && File.Exists(temporal)) EliminarArchivoPropio(temporal); }
    }

    private async Task ValidarProyectoAsync(Guid proyectoId, CancellationToken cancellationToken)
    {
        await using var catalogo = await catalogoFactory.CreateDbContextAsync(cancellationToken);
        if (!await catalogo.Proyectos.AsNoTracking().AnyAsync(x => x.Id == proyectoId, cancellationToken))
            throw new KeyNotFoundException("Abre primero un proyecto disponible en el catálogo local.");
    }

    private static async Task<ModeloBimLocal> ExigirModeloAsync(BimLocalDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.Modelos.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException("El modelo BIM no existe.");

    private static async Task<VersionModeloBimLocal> ExigirVersionAsync(BimLocalDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.Versiones.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException("La revisión BIM no existe.");

    private static ImportacionIfcDto LeerImportacion(VersionModeloBimLocal version) =>
        JsonSerializer.Deserialize<ImportacionIfcDto>(version.ImportacionJson)
        ?? throw new InvalidDataException("No se ha podido leer la instantánea de la revisión IFC.");

    private static VersionModeloBimDto ToDto(VersionModeloBimLocal version)
    {
        var importacion = LeerImportacion(version);
        return new(version.Id, version.ModeloId, version.Numero, version.NombreArchivo, version.Sha256,
            importacion.Esquema, version.ImportadaUtc, importacion.Elementos.Count, importacion.Advertencias);
    }

    private static VinculoElementoPartidaBimDto ToDto(VinculoElementoPartidaBimLocal vinculo) =>
        new(vinculo.Id, vinculo.ModeloId, vinculo.GlobalId, vinculo.PartidaId, vinculo.Regla, vinculo.Factor);

    private static MedicionVinculadaBimDto Medir(Guid versionId, ElementoModeloBimDto elemento,
        VinculoElementoPartidaBimLocal vinculo, PartidaLocal? partida)
    {
        string? aviso = null;
        decimal? cantidad = null;
        decimal? importe = null;
        if (partida is null) aviso = "La partida ya no está disponible en el catálogo local.";
        else if (!UnidadCompatible(partida.Unidad, vinculo.Regla)) aviso = "La unidad actual de la partida es incompatible con la regla.";
        else if (Cantidad(elemento.Cantidades, vinculo.Regla) is not { } baseCantidad) aviso = "El IFC no aporta la cantidad seleccionada; no se sustituye por cero ni por otra regla.";
        else
        {
            try
            {
                cantidad = baseCantidad * vinculo.Factor;
                if (partida.PrecioUnitario < 0) aviso = "La tarifa actual es negativa; no se ha valorado.";
                else importe = cantidad.Value * partida.PrecioUnitario;
            }
            catch (OverflowException) { cantidad = null; aviso = "La cantidad o el importe excede el rango decimal admitido."; }
        }
        return new(vinculo.Id, versionId, elemento.GlobalId, elemento.Nombre, vinculo.PartidaId,
            partida?.Codigo ?? "(partida no disponible)", vinculo.Regla, vinculo.Factor, cantidad,
            partida?.Unidad ?? string.Empty, partida?.PrecioUnitario ?? 0m, importe, aviso);
    }

    private static decimal? Cantidad(CantidadesIfcDto cantidades, ReglaMedicionBim regla) => regla switch
    {
        ReglaMedicionBim.VolumenNeto => cantidades.VolumenNeto,
        ReglaMedicionBim.VolumenBruto => cantidades.VolumenBruto,
        ReglaMedicionBim.AreaNeta => cantidades.AreaNeta,
        ReglaMedicionBim.AreaBruta => cantidades.AreaBruta,
        ReglaMedicionBim.Longitud => cantidades.Longitud,
        ReglaMedicionBim.Unidades => cantidades.Unidades,
        _ => throw new ArgumentOutOfRangeException(nameof(regla))
    };

    private static bool UnidadCompatible(string unidad, ReglaMedicionBim regla)
    {
        var normalizada = unidad.Trim().ToLowerInvariant().Replace("³", "3", StringComparison.Ordinal)
            .Replace("²", "2", StringComparison.Ordinal).Replace("^", string.Empty, StringComparison.Ordinal);
        return regla switch
        {
            ReglaMedicionBim.VolumenNeto or ReglaMedicionBim.VolumenBruto => normalizada == "m3",
            ReglaMedicionBim.AreaNeta or ReglaMedicionBim.AreaBruta => normalizada == "m2",
            ReglaMedicionBim.Longitud => normalizada is "m" or "ml",
            ReglaMedicionBim.Unidades => normalizada is "ud" or "uds" or "u" or "unidad" or "unidades" or "un",
            _ => false
        };
    }

    private static bool ElementoModificado(ElementoModeloBimDto anterior, ElementoModeloBimDto nuevo) =>
        anterior.ClaseIfc != nuevo.ClaseIfc || anterior.Nombre != nuevo.Nombre || anterior.Familia != nuevo.Familia
        || anterior.Tipo != nuevo.Tipo || anterior.CodigoPartida != nuevo.CodigoPartida
        || Enum.GetValues<ReglaMedicionBim>().Any(regla => CantidadDistinta(Cantidad(anterior.Cantidades, regla), Cantidad(nuevo.Cantidades, regla)));

    private static bool CantidadDistinta(decimal? anterior, decimal? nueva)
    {
        if (anterior is null || nueva is null) return anterior != nueva;
        var tolerancia = Math.Max(0.000001m, Math.Max(Math.Abs(anterior.Value), Math.Abs(nueva.Value)) * 0.000001m);
        return Math.Abs(anterior.Value - nueva.Value) > tolerancia;
    }

    private static void ValidarImportacion(ImportacionIfcDto importacion)
    {
        if (string.IsNullOrWhiteSpace(importacion.Esquema)) throw new InvalidDataException("El IFC no indica un esquema válido.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var elemento in importacion.Elementos)
        {
            if (string.IsNullOrWhiteSpace(elemento.GlobalId) || !ids.Add(elemento.GlobalId))
                throw new InvalidDataException("El IFC contiene identificadores de elemento vacíos o repetidos.");
            if (Enum.GetValues<ReglaMedicionBim>().Any(regla => Cantidad(elemento.Cantidades, regla) < 0))
                throw new InvalidDataException($"El elemento {elemento.GlobalId} contiene cantidades negativas.");
        }
    }

    private async Task<string> CopiarYCalcularHashAsync(string origen, string destino, CancellationToken cancellationToken)
    {
        await using var entrada = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (entrada.Length > MaximoBytesIfc) throw new InvalidDataException("El IFC supera el límite de 256 MiB.");
        var creado = false;
        try
        {
            await using var salida = new FileStream(destino, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            creado = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int leidos;
            while ((leidos = await entrada.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += leidos;
                if (total > MaximoBytesIfc) throw new InvalidDataException("El IFC supera el límite de 256 MiB.");
                hash.AppendData(buffer, 0, leidos);
                await salida.WriteAsync(buffer.AsMemory(0, leidos), cancellationToken);
            }
            await salida.FlushAsync(cancellationToken);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            if (creado) EliminarArchivoPropio(destino);
            throw;
        }
    }

    private void EliminarArchivoPropio(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { logger.LogWarning(exception, "No se pudo retirar un archivo temporal BIM propio."); }
    }

    public void Dispose() => _escritura.Dispose();
}
