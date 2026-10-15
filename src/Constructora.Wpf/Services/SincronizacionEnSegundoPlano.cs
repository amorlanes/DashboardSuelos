using System.Collections.Concurrent;
using Constructora.Application;
using Constructora.Client.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Constructora.Wpf.Services;

public sealed record ActualizacionSincronizacion(bool Conectado, string Mensaje);

/// <summary>Owns all network work and awaits each operation before application shutdown.</summary>
public sealed class SincronizacionEnSegundoPlano : IDisposable
{
    private readonly ISincronizadorService _sincronizador;
    private readonly ILogger<SincronizacionEnSegundoPlano> _logger;
    private readonly HubConnection _hub;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentDictionary<Guid, byte> _proyectosPendientes = new();
    private readonly object _gate = new();
    private Task? _loop;
    private Guid? _proyectoActivo;
    private bool _disposed;

    public event Func<ActualizacionSincronizacion, CancellationToken, Task>? Actualizado;

    public SincronizacionEnSegundoPlano(ISincronizadorService sincronizador, Uri apiUri,
        ILogger<SincronizacionEnSegundoPlano> logger)
    {
        _sincronizador = sincronizador;
        _logger = logger;
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(apiUri, "hubs/certificaciones"))
            .WithAutomaticReconnect()
            .Build();
        _hub.On<NotificacionCertificacion>("CertificacionActualizada", notification =>
        {
            _proyectosPendientes.TryAdd(notification.ProyectoId, 0);
            SolicitarSincronizacion();
        });
        _hub.Reconnected += _ =>
        {
            // A lost notification is recovered by fetching the selected project's current snapshot.
            SolicitarSincronizacion();
            return Task.CompletedTask;
        };
    }

    public void Iniciar()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _loop ??= EjecutarAsync(_lifetime.Token);
        }
    }

    public void SeleccionarProyecto(Guid proyectoId)
    {
        lock (_gate) _proyectoActivo = proyectoId;
        _proyectosPendientes.TryAdd(proyectoId, 0);
        SolicitarSincronizacion();
    }

    public void SolicitarSincronizacion()
    {
        lock (_gate)
        {
            if (_disposed || _lifetime.IsCancellationRequested) return;
            if (_signal.CurrentCount == 0) _signal.Release();
        }
    }

    private async Task EjecutarAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var online = await _sincronizador.HayConexionAsync(cancellationToken).ConfigureAwait(false);
                    if (!online)
                    {
                        await PublicarAsync(new(false, "Sin conexión. Los cambios se guardan en SQLite."),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var catalogoActualizado = await _sincronizador.CargarCatalogoAsync(cancellationToken)
                            .ConfigureAwait(false);
                        var summary = await _sincronizador.SincronizarPendientesAsync(cancellationToken)
                            .ConfigureAwait(false);
                        Guid? proyectoActivo;
                        lock (_gate) proyectoActivo = _proyectoActivo;
                        if (proyectoActivo.HasValue) _proyectosPendientes.TryAdd(proyectoActivo.Value, 0);
                        foreach (var projectId in _proyectosPendientes.Keys)
                        {
                            if (await _sincronizador.ActualizarCertificacionesAsync(projectId, cancellationToken)
                                    .ConfigureAwait(false))
                                _proyectosPendientes.TryRemove(projectId, out _);
                        }
                        if (_hub.State == HubConnectionState.Disconnected)
                        {
                            try { await _hub.StartAsync(cancellationToken).ConfigureAwait(false); }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                _logger.LogWarning(exception,
                                    "SignalR no está disponible; se mantiene la consulta periódica.");
                            }
                        }
                        var message = summary.SinConexion
                            ? "Se interrumpió la conexión. Los cambios siguen guardados localmente."
                            : summary.Conflictos > 0
                                ? $"Hay {summary.Conflictos} conflicto(s) pendiente(s) de revisión."
                                : $"Sincronización comprobada. {summary.Enviadas} certificación(es) enviada(s).";
                        if (!catalogoActualizado && !summary.SinConexion)
                            message += " No se pudo actualizar el catálogo; se conserva la copia local.";
                        await PublicarAsync(new(!summary.SinConexion, message), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (SincronizacionDatosException exception)
                {
                    _logger.LogWarning(exception, "El servidor rechazó una operación de sincronización.");
                    await PublicarAsync(new(true,
                        $"El servidor rechazó los datos: {exception.Message} Corrija la certificación y vuelva a guardar."),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Error en el ciclo de sincronización.");
                    await PublicarAsync(new(false,
                        "No se pudo sincronizar. Los datos locales se conservan y se reintentará."),
                        cancellationToken).ConfigureAwait(false);
                }
                await _signal.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task PublicarAsync(ActualizacionSincronizacion update, CancellationToken cancellationToken)
    {
        var handlers = Actualizado;
        if (handlers is null) return;
        foreach (Func<ActualizacionSincronizacion, CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            try { await handler(update, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogError(exception, "No se pudo actualizar la vista de sincronización."); }
        }
    }

    public async Task DetenerAsync()
    {
        _lifetime.Cancel();
        Task? running;
        lock (_gate) running = _loop;
        if (running is not null) await running.ConfigureAwait(false);
        await _hub.StopAsync().ConfigureAwait(false);
        await _hub.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        // The window awaits DetenerAsync before closing and disposing the DI container.
        _lifetime.Dispose();
        _signal.Dispose();
    }
}
