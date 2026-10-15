using Constructora.Application;
using Microsoft.AspNetCore.SignalR;

namespace Constructora.Server.Hubs;

public sealed class CertificacionHub(ILogger<CertificacionHub> logger) : Hub
{
    public async Task NotificarCertificacionActualizada(Guid proyectoId, Guid certificacionId)
    {
        if (proyectoId == Guid.Empty || certificacionId == Guid.Empty)
            throw new HubException("El proyecto y la certificación deben tener identificadores válidos.");

        try
        {
            await Clients.All.SendAsync("CertificacionActualizada",
                new NotificacionCertificacion(proyectoId, certificacionId), Context.ConnectionAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo notificar la certificación {CertificacionId}.", certificacionId);
            throw new HubException("No se pudo publicar la notificación.");
        }
    }
}
