using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Constructora.Application;
using Constructora.Client.Core;
using Constructora.Domain;
using Constructora.Wpf.Services;
using Microsoft.Extensions.Logging;

namespace Constructora.Wpf.ViewModels;

public sealed partial class CertificacionViewModel : ObservableObject
{
    private readonly ISincronizadorService _sincronizador;
    private readonly SincronizacionEnSegundoPlano _background;
    private readonly ILogger<CertificacionViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operaciones = new(1, 1);
    private Guid? _proyectoAbierto;
    private bool _detenido;

    public ObservableCollection<ProyectoDto> Proyectos { get; } = [];
    public ObservableCollection<PartidaDto> Partidas { get; } = [];
    public ObservableCollection<CertificacionDto> Certificaciones { get; } = [];
    public ObservableCollection<ConflictoLocal> Conflictos { get; } = [];
    public IReadOnlyList<EstadoCertificacion> Estados { get; } = Enum.GetValues<EstadoCertificacion>();

    [ObservableProperty] private ProyectoDto? proyectoSeleccionado;
    [ObservableProperty] private CertificacionDto? certificacionSeleccionada;
    [ObservableProperty] private CertificacionEditor? certificacionActual;
    [ObservableProperty] private ConflictoLocal? conflictoSeleccionado;
    [ObservableProperty] private string estadoConexion = "Iniciando copia local…";
    [ObservableProperty] private string mensaje = "Seleccione un proyecto para abrir sus certificaciones.";
    [ObservableProperty] private bool ocupada;
    [ObservableProperty] private bool tieneErroresEdicion;
    public bool EditorTieneFoco { get; set; }
    public BimViewModel Bim { get; }

    public CertificacionViewModel(ISincronizadorService sincronizador,
        SincronizacionEnSegundoPlano background, ILogger<CertificacionViewModel> logger,
        BimViewModel bim)
    {
        _sincronizador = sincronizador;
        _background = background;
        _logger = logger;
        Bim = bim;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public async Task InicializarAsync()
    {
        await _sincronizador.InicializarAsync(_lifetime.Token);
        await Bim.InicializarAsync(_lifetime.Token);
        await RecargarCacheAsync(_lifetime.Token);
        EstadoConexion = "Copia local disponible; comprobando conexión…";
        _background.Actualizado += OnActualizadoAsync;
        _background.Iniciar();
    }

    [RelayCommand]
    private Task AbrirProyectoAsync() => EjecutarAsync(async () =>
    {
        if (ProyectoSeleccionado is null) { Mensaje = "Seleccione un proyecto."; return; }
        if (CertificacionActual?.TieneCambios == true)
        {
            Mensaje = "Guarde la certificación actual antes de cambiar de proyecto.";
            return;
        }
        var project = ProyectoSeleccionado;
        _proyectoAbierto = project.Id;
        CertificacionActual = null;
        await CargarProyectoLocalAsync(_proyectoAbierto.Value, _lifetime.Token);
        await Bim.AbrirProyectoAsync(_proyectoAbierto.Value, Partidas.ToArray(), _lifetime.Token);
        Bim.NombreProyectoAbierto = project.Nombre;
        _background.SeleccionarProyecto(_proyectoAbierto.Value);
        Mensaje = $"Proyecto abierto: {project.Nombre}. Puede trabajar sin conexión.";
    });

    [RelayCommand]
    private Task AbrirCertificacionAsync() => EjecutarAsync(async () =>
    {
        if (CertificacionSeleccionada is null) { Mensaje = "Seleccione una certificación."; return; }
        if (CertificacionActual?.TieneCambios == true)
        {
            Mensaje = "Guarde los cambios actuales antes de abrir otra certificación.";
            return;
        }
        var local = await _sincronizador.ObtenerCertificacionAsync(CertificacionSeleccionada.Id, _lifetime.Token);
        if (local is null) { Mensaje = "La certificación ya no está en la copia local."; return; }
        CertificacionActual = new CertificacionEditor(local, Partidas.ToArray());
        Mensaje = $"Certificación {local.NumeroCertificacion} abierta desde SQLite.";
    });

    [RelayCommand]
    private Task NuevaCertificacionAsync() => EjecutarAsync(() =>
    {
        if (!_proyectoAbierto.HasValue) { Mensaje = "Abra primero un proyecto."; return Task.CompletedTask; }
        if (CertificacionActual?.TieneCambios == true)
        {
            Mensaje = "Guarde los cambios actuales antes de crear otra certificación.";
            return Task.CompletedTask;
        }
        if (Partidas.Count == 0)
        {
            Mensaje = "El proyecto no tiene partidas en la copia local. Actualice el catálogo cuando haya conexión.";
            return Task.CompletedTask;
        }
        var previous = Certificaciones.OrderByDescending(item => item.NumeroCertificacion).FirstOrDefault();
        var previousLines = previous?.Lineas.ToDictionary(line => line.PartidaId)
                            ?? new Dictionary<Guid, LineaCertificacionDto>();
        var lines = Partidas.Select(item =>
        {
            var accumulated = previousLines.TryGetValue(item.Id, out var line)
                ? line.CantidadAcumuladaAnterior + line.CantidadPeriodo : 0;
            return new LineaCertificacionDto(Guid.NewGuid(), item.Id, accumulated, 0,
                item.CantidadPrevista > 0 ? decimal.Round(accumulated * 100 / item.CantidadPrevista, 4) : 0);
        }).ToList();
        var nextNumber = checked((previous?.NumeroCertificacion ?? 0) + 1);
        CertificacionActual = new CertificacionEditor(new CertificacionDto(Guid.NewGuid(),
            _proyectoAbierto.Value, nextNumber, DateOnly.FromDateTime(DateTime.Today),
            EstadoCertificacion.Borrador, 0, lines), Partidas.ToArray()) { TieneCambios = true };
        Mensaje = "Nuevo borrador. Guarde para conservar los cambios en este equipo.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task GuardarCertificacionAsync() => EjecutarAsync(async () =>
    {
        if (CertificacionActual is null) { Mensaje = "Abra o cree una certificación."; return; }
        if (TieneErroresEdicion)
        {
            Mensaje = "Corrija los valores marcados en rojo antes de guardar.";
            return;
        }
        var editor = CertificacionActual;
        var snapshot = editor.ToDto();
        var revision = editor.Revision;
        await _sincronizador.GuardarLocalAsync(snapshot, _lifetime.Token);
        editor.MarcarGuardada(revision);
        await CargarProyectoLocalAsync(snapshot.ProyectoId, _lifetime.Token);
        Mensaje = "Guardada en SQLite. La sincronización continúa en segundo plano.";
        _background.SolicitarSincronizacion();
    });

    [RelayCommand]
    private void Sincronizar() => _background.SolicitarSincronizacion();

    [RelayCommand]
    private Task ConservarVersionLocalAsync() => EjecutarAsync(async () =>
    {
        if (ConflictoSeleccionado is null) { Mensaje = "Seleccione un conflicto."; return; }
        if (CertificacionActual?.TieneCambios == true)
        {
            Mensaje = "Guarde los cambios del editor antes de resolver un conflicto.";
            return;
        }
        var conflict = ConflictoSeleccionado;
        // This is an explicit user decision to keep the local content over the displayed server version.
        var resolved = conflict.VersionLocal with { RowVersion = conflict.VersionServidor?.RowVersion ?? 0 };
        await _sincronizador.ResolverConflictoAsync(conflict.CertificacionId, resolved, _lifetime.Token);
        await RecargarCacheAsync(_lifetime.Token);
        Mensaje = "Se conservará la versión local. Se enviará con la versión del servidor revisada.";
        _background.SolicitarSincronizacion();
    });

    [RelayCommand]
    private Task AceptarVersionServidorAsync() => EjecutarAsync(async () =>
    {
        if (ConflictoSeleccionado?.VersionServidor is null)
        {
            Mensaje = "Este conflicto no incluye una versión del servidor que pueda aceptarse.";
            return;
        }
        if (CertificacionActual?.TieneCambios == true)
        {
            Mensaje = "Guarde los cambios del editor antes de resolver un conflicto.";
            return;
        }
        await _sincronizador.AceptarVersionServidorAsync(ConflictoSeleccionado.CertificacionId, _lifetime.Token);
        await RecargarCacheAsync(_lifetime.Token);
        Mensaje = "Versión del servidor aceptada. Copia local actualizada.";
    });

    private Task OnActualizadoAsync(ActualizacionSincronizacion update, CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(async () =>
        {
            await _operaciones.WaitAsync(cancellationToken);
            try
            {
                EstadoConexion = update.Conectado ? "Con conexión" : "Sin conexión";
                if (CertificacionActual?.TieneCambios != true) Mensaje = update.Mensaje;
                await RecargarCacheAsync(cancellationToken);
            }
            finally { _operaciones.Release(); }
        }, DispatcherPriority.Background, cancellationToken).Task.Unwrap();

    private async Task RecargarCacheAsync(CancellationToken cancellationToken)
    {
        var selectedProject = ProyectoSeleccionado?.Id;
        Reemplazar(Proyectos, await _sincronizador.ObtenerProyectosAsync(cancellationToken));
        ProyectoSeleccionado = Proyectos.FirstOrDefault(item => item.Id == selectedProject) ?? Proyectos.FirstOrDefault();
        if (_proyectoAbierto.HasValue)
        {
            await CargarProyectoLocalAsync(_proyectoAbierto.Value, cancellationToken);
            if (CertificacionActual is { TieneCambios: false } current)
            {
                var updated = await _sincronizador.ObtenerCertificacionAsync(current.Id, cancellationToken);
                if (updated is not null && !current.ActualizarVersionSiCoincide(updated) && !EditorTieneFoco)
                    CertificacionActual = new CertificacionEditor(updated, Partidas.ToArray());
            }
        }
        var selectedConflict = ConflictoSeleccionado?.CertificacionId;
        Reemplazar(Conflictos, await _sincronizador.ObtenerConflictosAsync(cancellationToken));
        ConflictoSeleccionado = Conflictos.FirstOrDefault(item => item.CertificacionId == selectedConflict)
                               ?? Conflictos.FirstOrDefault();
    }

    private async Task CargarProyectoLocalAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var selectedCertification = CertificacionSeleccionada?.Id;
        Reemplazar(Partidas, await _sincronizador.ObtenerPartidasAsync(projectId, cancellationToken));
        Bim.ActualizarPartidas(projectId, Partidas.ToArray());
        Reemplazar(Certificaciones, (await _sincronizador.ObtenerCertificacionesAsync(projectId, cancellationToken))
            .OrderByDescending(item => item.NumeroCertificacion));
        CertificacionSeleccionada = Certificaciones.FirstOrDefault(item => item.Id == selectedCertification)
                                   ?? Certificaciones.FirstOrDefault();
    }

    private async Task EjecutarAsync(Func<Task> action)
    {
        try
        {
            await _operaciones.WaitAsync(_lifetime.Token);
            try { Ocupada = true; await action(); }
            finally { Ocupada = false; _operaciones.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudo completar la operación de certificaciones.");
            Mensaje = $"No se pudo completar la operación: {exception.Message}";
        }
    }

    private static void Reemplazar<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values) collection.Add(value);
    }

    public async Task DetenerAsync()
    {
        if (_detenido) return;
        _detenido = true;
        _lifetime.Cancel();
        _background.Actualizado -= OnActualizadoAsync;
        await Bim.DetenerAsync();
        await _background.DetenerAsync();
        await _operaciones.WaitAsync();
        _operaciones.Release();
    }
}
