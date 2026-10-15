using Constructora.Domain;

namespace Constructora.Application;

public sealed record ProyectoDto(Guid Id, string Nombre, string Codigo, DateOnly FechaInicio);
public sealed record PartidaDto(Guid Id, Guid ProyectoId, string Codigo, string Descripcion,
    string Unidad, decimal PrecioUnitario, decimal CantidadPrevista);
public sealed record LineaCertificacionDto(Guid Id, Guid PartidaId, decimal CantidadAcumuladaAnterior,
    decimal CantidadPeriodo, decimal PorcentajeAvance);
public sealed record CertificacionDto(Guid Id, Guid ProyectoId, int NumeroCertificacion, DateOnly Fecha,
    EstadoCertificacion Estado, uint RowVersion, List<LineaCertificacionDto> Lineas);
public sealed record SincronizarCertificacionRequest(Guid OperacionId, CertificacionDto Certificacion);
public sealed record ConflictoSincronizacion(string Mensaje, CertificacionDto? VersionServidor);
public sealed record NotificacionCertificacion(Guid ProyectoId, Guid CertificacionId);

public static class MapeoCertificaciones
{
    public static CertificacionDto ToDto(this Certificacion c) => new(c.Id, c.ProyectoId,
        c.NumeroCertificacion, c.Fecha, c.Estado, c.RowVersion,
        c.Lineas.OrderBy(l => l.Id).Select(l => new LineaCertificacionDto(l.Id, l.PartidaId,
            l.CantidadAcumuladaAnterior, l.CantidadPeriodo, l.PorcentajeAvance)).ToList());
}
