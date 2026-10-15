namespace Constructora.Application.Bim;

public enum ReglaMedicionBim { VolumenNeto, VolumenBruto, AreaNeta, AreaBruta, Longitud, Unidades }
public enum TipoCambioBim { Anadido, Eliminado, Modificado }

public sealed record CantidadesIfcDto(decimal? VolumenNeto, decimal? VolumenBruto,
    decimal? AreaNeta, decimal? AreaBruta, decimal? Longitud, decimal? Unidades);

public sealed record ElementoModeloBimDto(string GlobalId, string ClaseIfc, string Nombre,
    string Familia, string Tipo, string? CodigoPartida, CantidadesIfcDto Cantidades);

public sealed record ImportacionIfcDto(string Esquema, string? ProyectoIfcGlobalId,
    IReadOnlyList<ElementoModeloBimDto> Elementos, IReadOnlyList<string> Advertencias);

public interface IImportadorIfc
{
    Task<ImportacionIfcDto> ImportarAsync(string rutaArchivo, CancellationToken cancellationToken = default);
}

public sealed record ModeloBimDto(Guid Id, Guid ProyectoId, string Nombre, DateTime CreadoUtc);
public sealed record VersionModeloBimDto(Guid Id, Guid ModeloId, int Numero, string NombreArchivo,
    string Sha256, string Esquema, DateTime ImportadaUtc, int NumeroElementos,
    IReadOnlyList<string> Advertencias);
public sealed record VinculoElementoPartidaBimDto(Guid Id, Guid ModeloId, string GlobalId,
    Guid PartidaId, ReglaMedicionBim Regla, decimal Factor);
public sealed record MedicionVinculadaBimDto(Guid VinculoId, Guid VersionId, string GlobalId,
    string NombreElemento, Guid PartidaId, string CodigoPartida, ReglaMedicionBim Regla,
    decimal Factor, decimal? Cantidad, string Unidad, decimal PrecioUnitario,
    decimal? Importe, string? Aviso);
public sealed record CambioElementoBimDto(string GlobalId, TipoCambioBim Cambio,
    ElementoModeloBimDto? Anterior, ElementoModeloBimDto? Nuevo);
public sealed record ComparacionVersionesBimDto(Guid VersionAnteriorId, Guid VersionNuevaId,
    IReadOnlyList<CambioElementoBimDto> Cambios, decimal ImpactoEconomicoConocido,
    int ElementosSinValorar, IReadOnlyList<string> Advertencias);

public interface IBimService
{
    Task InicializarAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModeloBimDto>> ObtenerModelosAsync(Guid proyectoId, CancellationToken cancellationToken = default);
    Task<VersionModeloBimDto> ImportarAsync(Guid proyectoId, Guid? modeloId, string rutaArchivo,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VersionModeloBimDto>> ObtenerVersionesAsync(Guid modeloId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ElementoModeloBimDto>> ObtenerElementosAsync(Guid versionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VinculoElementoPartidaBimDto>> ObtenerVinculosAsync(Guid modeloId, CancellationToken cancellationToken = default);
    Task<VinculoElementoPartidaBimDto> VincularAsync(Guid modeloId, string globalId, Guid partidaId,
        ReglaMedicionBim regla, decimal factor, CancellationToken cancellationToken = default);
    Task EliminarVinculoAsync(Guid vinculoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MedicionVinculadaBimDto>> ObtenerMedicionesAsync(Guid versionId, CancellationToken cancellationToken = default);
    Task<ComparacionVersionesBimDto> CompararAsync(Guid versionAnteriorId, Guid versionNuevaId,
        CancellationToken cancellationToken = default);
    Task ExportarOriginalAsync(Guid versionId, string rutaDestino, CancellationToken cancellationToken = default);
}
