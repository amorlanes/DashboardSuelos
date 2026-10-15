using Constructora.Application;

namespace Constructora.Client.Core;

public interface ISincronizadorService
{
    Task InicializarAsync(CancellationToken cancellationToken = default);
    Task GuardarLocalAsync(CertificacionDto certificacion, CancellationToken cancellationToken = default);
    Task<ResumenSincronizacion> SincronizarPendientesAsync(CancellationToken cancellationToken = default);
    Task<bool> HayConexionAsync(CancellationToken cancellationToken = default);
    Task<bool> CargarCatalogoAsync(CancellationToken cancellationToken = default);
    Task<bool> ActualizarCertificacionesAsync(Guid proyectoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProyectoDto>> ObtenerProyectosAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PartidaDto>> ObtenerPartidasAsync(Guid proyectoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CertificacionDto>> ObtenerCertificacionesAsync(Guid proyectoId, CancellationToken cancellationToken = default);
    Task<CertificacionDto?> ObtenerCertificacionAsync(Guid certificacionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConflictoLocal>> ObtenerConflictosAsync(CancellationToken cancellationToken = default);
    Task ResolverConflictoAsync(Guid certificacionId, CertificacionDto versionResuelta, CancellationToken cancellationToken = default);
    Task AceptarVersionServidorAsync(Guid certificacionId, CancellationToken cancellationToken = default);
}

public sealed record ResumenSincronizacion(int Enviadas, int Conflictos, bool SinConexion);
public sealed record ConflictoLocal(Guid CertificacionId, CertificacionDto VersionLocal,
    CertificacionDto? VersionServidor, string Mensaje);

public sealed class SincronizacionDatosException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
