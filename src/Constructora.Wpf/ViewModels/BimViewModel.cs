using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Constructora.Application;
using Constructora.Application.Bim;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Constructora.Wpf.ViewModels;

public sealed partial class BimViewModel : ObservableObject
{
    private readonly IBimService _bim;
    private readonly ILogger<BimViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operaciones = new(1, 1);
    private CancellationTokenSource _proyectoLifetime = new();
    private Guid? _proyectoId;
    private Guid? _versionCargadaId;
    private long _revisionProyecto;
    private bool _detenido;
    private IReadOnlyList<ElementoModeloBimDto> _elementosSeleccionados = [];

    public ColeccionBim<ModeloBimDto> Modelos { get; } = [];
    public ColeccionBim<VersionModeloBimDto> Versiones { get; } = [];
    public ColeccionBim<ElementoModeloBimDto> Elementos { get; } = [];
    public ColeccionBim<PartidaDto> Partidas { get; } = [];
    public ColeccionBim<MedicionFilaBim> Mediciones { get; } = [];
    public ColeccionBim<CambioFilaBim> Cambios { get; } = [];
    public ColeccionBim<string> Advertencias { get; } = [];
    public ICollectionView ElementosFiltrados { get; }
    public IReadOnlyList<ReglaMedicionOpcion> Reglas { get; } =
    [
        new(ReglaMedicionBim.VolumenNeto, "Volumen neto (m³)"),
        new(ReglaMedicionBim.VolumenBruto, "Volumen bruto (m³)"),
        new(ReglaMedicionBim.AreaNeta, "Área neta (m²)"),
        new(ReglaMedicionBim.AreaBruta, "Área bruta (m²)"),
        new(ReglaMedicionBim.Longitud, "Longitud (m)"),
        new(ReglaMedicionBim.Unidades, "Número de unidades (ud.)")
    ];

    [ObservableProperty] private string nombreProyectoAbierto = "Abra un proyecto para vincular sus modelos IFC.";
    [ObservableProperty] private string mensaje = "Los modelos y sus vínculos se conservan en este equipo y funcionan sin conexión.";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Disponible))] private bool ocupada;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Disponible))] private bool tieneProyecto;
    [ObservableProperty] private ModeloBimDto? modeloSeleccionado;
    [ObservableProperty] private VersionModeloBimDto? versionSeleccionada;
    [ObservableProperty] private VersionModeloBimDto? versionAnterior;
    [ObservableProperty] private ElementoModeloBimDto? elementoSeleccionado;
    [ObservableProperty] private PartidaDto? partidaSeleccionada;
    [ObservableProperty] private MedicionFilaBim? medicionSeleccionada;
    [ObservableProperty] private ReglaMedicionOpcion? reglaSeleccionada;
    [ObservableProperty] private string factorTexto = "1";
    [ObservableProperty] private string filtroElementos = "";
    [ObservableProperty] private string resumenMediciones = "No hay mediciones vinculadas.";
    [ObservableProperty] private string resumenVersion = "Seleccione un modelo y cargue una revisión.";
    [ObservableProperty] private string resumenComparacion = "Seleccione dos revisiones del mismo modelo para compararlas.";

    public bool Disponible => TieneProyecto && !Ocupada;

    public BimViewModel(IBimService bim, ILogger<BimViewModel> logger)
    {
        _bim = bim;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        ElementosFiltrados = CollectionViewSource.GetDefaultView(Elementos);
        ElementosFiltrados.Filter = value => value is ElementoModeloBimDto item && CoincideFiltro(item);
        ReglaSeleccionada = Reglas[0];
    }

    partial void OnFiltroElementosChanged(string value) => ElementosFiltrados.Refresh();

    private bool CoincideFiltro(ElementoModeloBimDto item) => string.IsNullOrWhiteSpace(FiltroElementos)
        || new[] { item.GlobalId, item.ClaseIfc, item.Nombre, item.Familia, item.Tipo }
            .Any(value => value.Contains(FiltroElementos.Trim(), StringComparison.OrdinalIgnoreCase));

    public void ActualizarSeleccion(IReadOnlyList<ElementoModeloBimDto> seleccion) =>
        _elementosSeleccionados = seleccion;

    public void ActualizarPartidas(Guid proyectoId, IReadOnlyList<PartidaDto> partidas)
    {
        if (_proyectoId != proyectoId) return;
        var selected = PartidaSeleccionada?.Id;
        Reemplazar(Partidas, partidas);
        PartidaSeleccionada = Partidas.FirstOrDefault(item => item.Id == selected) ?? Partidas.FirstOrDefault();
    }

    public Task InicializarAsync(CancellationToken cancellationToken = default) =>
        EjecutarAsync(token => _bim.InicializarAsync(token), cancellationToken);

    public Task AbrirProyectoAsync(Guid proyectoId, IReadOnlyList<PartidaDto> partidas,
        CancellationToken cancellationToken = default)
    {
        if (!_dispatcher.CheckAccess())
            return _dispatcher.InvokeAsync(() => AbrirProyectoAsync(proyectoId, partidas, cancellationToken))
                .Task.Unwrap();
        _revisionProyecto++;
        _proyectoLifetime.Cancel();
        _proyectoLifetime.Dispose();
        _proyectoLifetime = new CancellationTokenSource();
        _proyectoId = proyectoId;
        TieneProyecto = true;
        ModeloSeleccionado = null;
        Reemplazar(Modelos, []);
        Reemplazar(Partidas, partidas);
        PartidaSeleccionada = Partidas.FirstOrDefault();
        return EjecutarAsync(async token =>
        {
            await _bim.InicializarAsync(token);
            await RecargarModelosAsync(null, token);
            Mensaje = Modelos.Count == 0
                ? "Proyecto abierto. Pulse «Vincular IFC» para importar su primer modelo."
                : "Proyecto abierto. Seleccione un modelo IFC y cargue una revisión.";
        }, cancellationToken);
    }

    partial void OnModeloSeleccionadoChanged(ModeloBimDto? value)
    {
        VersionSeleccionada = null;
        VersionAnterior = null;
        Versiones.Clear();
        LimpiarVersion();
    }

    partial void OnVersionSeleccionadaChanged(VersionModeloBimDto? value) => LimpiarVersion();

    partial void OnVersionAnteriorChanged(VersionModeloBimDto? value)
    {
        Cambios.Clear();
        ResumenComparacion = "Pulse «Comparar revisiones» para calcular las diferencias.";
        Reemplazar(Advertencias, VersionSeleccionada?.Advertencias ?? []);
    }

    private void LimpiarVersion()
    {
        _versionCargadaId = null;
        ElementoSeleccionado = null;
        MedicionSeleccionada = null;
        Elementos.Clear();
        Mediciones.Clear();
        _elementosSeleccionados = [];
        ResumenMediciones = "No hay mediciones vinculadas.";
        Cambios.Clear();
        Reemplazar(Advertencias, VersionSeleccionada?.Advertencias ?? []);
        ResumenVersion = "Pulse «Cargar versión» para consultar sus elementos y mediciones.";
        ResumenComparacion = "Seleccione dos revisiones del mismo modelo para compararlas.";
    }

    [RelayCommand]
    private Task VincularIfcAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto()) return;
        var dialog = CrearDialogoImportacion("Vincular un nuevo modelo IFC al proyecto");
        if (dialog.ShowDialog() != true) return;
        var imported = await _bim.ImportarAsync(_proyectoId!.Value, null, dialog.FileName, token);
        await RecargarModelosAsync(imported.ModeloId, token);
        await CargarVersionesInternoAsync(imported.Id, token);
        Mensaje = "Modelo IFC vinculado y archivado. Seleccione un elemento y una partida para crear una medición de referencia.";
    });

    [RelayCommand]
    private Task ImportarRevisionAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || ModeloSeleccionado is null)
        { Mensaje = "Seleccione el modelo al que pertenece la nueva revisión IFC."; return; }
        var modelId = ModeloSeleccionado.Id;
        var dialog = CrearDialogoImportacion("Importar una nueva revisión del modelo IFC seleccionado");
        if (dialog.ShowDialog() != true) return;
        var imported = await _bim.ImportarAsync(_proyectoId!.Value, modelId, dialog.FileName, token);
        if (ModeloSeleccionado?.Id != modelId) return;
        await CargarVersionesInternoAsync(imported.Id, token);
        Mensaje = "Revisión IFC importada. Los vínculos se conservan por GlobalId; compare con la revisión anterior para revisar los cambios.";
    });

    [RelayCommand]
    private Task CargarModeloAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || ModeloSeleccionado is null)
        { Mensaje = "Seleccione un modelo IFC."; return; }
        await CargarVersionesInternoAsync(null, token);
        Mensaje = "Modelo cargado. Sus mediciones son referencias para la revisión técnica.";
    });

    [RelayCommand]
    private Task CargarVersionAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || VersionSeleccionada is null)
        { Mensaje = "Seleccione una versión del modelo IFC."; return; }
        await CargarVersionInternoAsync(token);
        Mensaje = "Versión cargada desde la copia local. Un guion indica que el IFC no aporta esa cantidad.";
    });

    [RelayCommand]
    private Task VincularElementoAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || ModeloSeleccionado is null || ElementoSeleccionado is null
            || PartidaSeleccionada is null || ReglaSeleccionada is null
            || _versionCargadaId != VersionSeleccionada?.Id)
        { Mensaje = "Cargue una versión y seleccione un elemento, una partida y una regla de medición."; return; }
        if (!LeerFactor(out var factor)) return;
        await _bim.VincularAsync(ModeloSeleccionado.Id, ElementoSeleccionado.GlobalId,
            PartidaSeleccionada.Id, ReglaSeleccionada.Valor, factor, token);
        await RecargarMedicionesAsync(token);
        Cambios.Clear();
        ResumenComparacion = "Los vínculos han cambiado. Vuelva a comparar las revisiones para actualizar el impacto económico.";
        Mensaje = "Vínculo guardado. Puede vincular el mismo elemento a varias partidas con reglas diferentes; la certificación no se modifica automáticamente.";
    });

    [RelayCommand]
    private Task DesvincularAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || MedicionSeleccionada is null)
        { Mensaje = "Seleccione una medición vinculada para retirarla."; return; }
        await _bim.EliminarVinculoAsync(MedicionSeleccionada.Valor.VinculoId, token);
        await RecargarMedicionesAsync(token);
        Cambios.Clear();
        ResumenComparacion = "Los vínculos han cambiado. Vuelva a comparar las revisiones para actualizar el impacto económico.";
        Mensaje = "Vínculo retirado. El elemento IFC y las certificaciones se conservan.";
    });

    [RelayCommand]
    private Task VincularSeleccionadosAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || ModeloSeleccionado is null || _elementosSeleccionados.Count == 0
            || PartidaSeleccionada is null || ReglaSeleccionada is null
            || _versionCargadaId != VersionSeleccionada?.Id)
        { Mensaje = "Cargue una versión, seleccione elementos con Ctrl o Mayús y elija su partida y regla."; return; }
        if (!LeerFactor(out var factor)) return;
        var selection = _elementosSeleccionados.ToArray();
        var modelId = ModeloSeleccionado.Id;
        var partidaId = PartidaSeleccionada.Id;
        var rule = ReglaSeleccionada.Valor;
        var saved = 0;
        var complete = false;
        string? error = null;
        try
        {
            foreach (var item in selection)
            {
                token.ThrowIfCancellationRequested();
                await _bim.VincularAsync(modelId, item.GlobalId, partidaId, rule, factor, token);
                saved++;
            }
            complete = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Vinculación múltiple incompleta: {Guardados} de {Total} elementos.",
                saved, selection.Length);
            error = MensajeDeError(exception);
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                await RecargarMedicionesAsync(token);
                Cambios.Clear();
                ResumenComparacion = "Los vínculos han cambiado. Vuelva a comparar las revisiones para actualizar el impacto económico.";
                Mensaje = complete
                    ? $"{saved} de {selection.Length} vínculos guardados. Las certificaciones conservan sus cantidades."
                    : $"Operación parcial: se guardaron {saved} de {selection.Length} vínculos. {error} Revise las mediciones antes de repetir la selección.";
            }
        }
    });

    [RelayCommand]
    private Task CompararAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || VersionAnterior is null || VersionSeleccionada is null
            || VersionAnterior.Id == VersionSeleccionada.Id)
        { Mensaje = "Seleccione dos revisiones diferentes del mismo modelo."; return; }
        if (VersionAnterior.Numero >= VersionSeleccionada.Numero)
        { Mensaje = "La revisión anterior debe ser más antigua que la revisión nueva."; return; }
        var previous = VersionAnterior;
        var current = VersionSeleccionada;
        var result = await _bim.CompararAsync(previous.Id, current.Id, token);
        if (VersionSeleccionada?.Id != current.Id || VersionAnterior?.Id != previous.Id) return;
        Reemplazar(Cambios, result.Cambios.Select(item => new CambioFilaBim(item)));
        Reemplazar(Advertencias, current.Advertencias.Concat(result.Advertencias).Distinct());
        var added = result.Cambios.Count(item => item.Cambio == TipoCambioBim.Anadido);
        var removed = result.Cambios.Count(item => item.Cambio == TipoCambioBim.Eliminado);
        var changed = result.Cambios.Count(item => item.Cambio == TipoCambioBim.Modificado);
        ResumenComparacion = $"V{previous.Numero} → V{current.Numero}: {added} añadidos, {removed} eliminados y {changed} modificados. "
            + (result.ElementosSinValorar > 0
                ? $"Estimación parcial: {result.ImpactoEconomicoConocido:N2} €; {result.ElementosSinValorar} elementos cambiados sin valorar."
                : $"Impacto económico estimado: {result.ImpactoEconomicoConocido:N2} €.");
        Mensaje = "Comparación calculada con los vínculos y precios actuales. Revise sus advertencias antes de utilizar la estimación.";
    });

    [RelayCommand]
    private Task ExportarOriginalAsync() => EjecutarAsync(async token =>
    {
        if (!RequiereProyecto() || VersionSeleccionada is null)
        { Mensaje = "Seleccione la versión IFC que desea exportar."; return; }
        var version = VersionSeleccionada;
        var dialog = new SaveFileDialog
        {
            Title = "Exportar el IFC original de esta revisión", Filter = "Modelo IFC (*.ifc)|*.ifc",
            FileName = Path.GetFileNameWithoutExtension(version.NombreArchivo) + $"-v{version.Numero}.ifc",
            DefaultExt = ".ifc", AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;
        if (File.Exists(dialog.FileName))
        { Mensaje = "Ese archivo ya existe. Exporte con otro nombre para conservar ambas copias."; return; }
        await _bim.ExportarOriginalAsync(version.Id, dialog.FileName, token);
        Mensaje = "IFC original exportado. Las asociaciones a partidas se conservan en la aplicación y no alteran el archivo IFC.";
    });

    private async Task RecargarModelosAsync(Guid? selectedId, CancellationToken token)
    {
        var projectId = _proyectoId!.Value;
        var values = await _bim.ObtenerModelosAsync(projectId, token);
        token.ThrowIfCancellationRequested();
        if (_proyectoId != projectId) return;
        Reemplazar(Modelos, values);
        ModeloSeleccionado = Modelos.FirstOrDefault(item => item.Id == selectedId) ?? Modelos.FirstOrDefault();
    }

    private async Task CargarVersionesInternoAsync(Guid? selectedId, CancellationToken token)
    {
        if (ModeloSeleccionado is null) return;
        var modelId = ModeloSeleccionado.Id;
        var values = await _bim.ObtenerVersionesAsync(modelId, token);
        token.ThrowIfCancellationRequested();
        if (ModeloSeleccionado?.Id != modelId) return;
        Reemplazar(Versiones, values.OrderByDescending(item => item.Numero));
        VersionSeleccionada = Versiones.FirstOrDefault(item => item.Id == selectedId) ?? Versiones.FirstOrDefault();
        VersionAnterior = Versiones.FirstOrDefault(item => item.Numero < VersionSeleccionada?.Numero);
        await CargarVersionInternoAsync(token);
    }

    private async Task CargarVersionInternoAsync(CancellationToken token)
    {
        if (VersionSeleccionada is null) return;
        var version = VersionSeleccionada;
        var elements = await _bim.ObtenerElementosAsync(version.Id, token);
        var measurements = await _bim.ObtenerMedicionesAsync(version.Id, token);
        token.ThrowIfCancellationRequested();
        if (VersionSeleccionada?.Id != version.Id) return;
        Reemplazar(Elementos, elements);
        Reemplazar(Mediciones, measurements.Select(item => new MedicionFilaBim(item)));
        ActualizarResumenMediciones();
        ElementoSeleccionado = Elementos.FirstOrDefault();
        MedicionSeleccionada = null;
        Reemplazar(Advertencias, version.Advertencias);
        _versionCargadaId = version.Id;
        ResumenVersion = $"V{version.Numero} · {version.Esquema} · {Elementos.Count} elementos · {Mediciones.Count} mediciones vinculadas. "
            + "Cantidades normalizadas a m, m², m³ y unidades.";
    }

    private async Task RecargarMedicionesAsync(CancellationToken token)
    {
        if (VersionSeleccionada is null) return;
        var id = VersionSeleccionada.Id;
        var values = await _bim.ObtenerMedicionesAsync(id, token);
        token.ThrowIfCancellationRequested();
        if (VersionSeleccionada?.Id != id) return;
        Reemplazar(Mediciones, values.Select(item => new MedicionFilaBim(item)));
        ActualizarResumenMediciones();
        MedicionSeleccionada = null;
    }

    private void ActualizarResumenMediciones()
    {
        var missing = Mediciones.Count(item => item.Valor.Importe is null);
        var total = Mediciones.Sum(item => item.Valor.Importe ?? 0);
        ResumenMediciones = $"{Mediciones.Count} mediciones · importe conocido {total:N2} €"
            + (missing > 0 ? $" · estimación parcial: {missing} mediciones sin valorar." : ".");
    }

    private bool RequiereProyecto()
    {
        if (_proyectoId.HasValue) return true;
        Mensaje = "Abra un proyecto antes de trabajar con modelos IFC.";
        return false;
    }

    private bool LeerFactor(out decimal factor)
    {
        const NumberStyles estilo = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign
            | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (decimal.TryParse(FactorTexto, estilo, CultureInfo.CurrentCulture, out factor) && factor > 0
            && factor <= 1_000_000_000m && decimal.Round(factor, 6) == factor)
            return true;
        Mensaje = "El factor debe ser positivo, hasta 1.000.000.000 y con un máximo de seis decimales, sin separadores de miles. Utilice «"
            + 1.5m.ToString(CultureInfo.CurrentCulture) + "» para uno y medio, o 1 para conservar la cantidad IFC.";
        return false;
    }

    private async Task EjecutarAsync(Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        if (_detenido) return;
        var revision = _revisionProyecto;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token, _proyectoLifetime.Token, cancellationToken);
        var acquired = false;
        try
        {
            await _operaciones.WaitAsync(linked.Token);
            acquired = true;
            if (revision != _revisionProyecto) return;
            Ocupada = true;
            await action(linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudo completar la operación BIM local.");
            if (revision == _revisionProyecto)
                Mensaje = MensajeDeError(exception);
        }
        finally
        {
            if (acquired)
            {
                Ocupada = false;
                _operaciones.Release();
            }
        }
    }

    private static string MensajeDeError(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or InvalidDataException or KeyNotFoundException
            ? exception.Message
            : "No se pudo completar la operación IFC. Compruebe el archivo y los permisos; el registro de la aplicación contiene los detalles.";

    private static OpenFileDialog CrearDialogoImportacion(string title) => new()
    {
        Title = title, Filter = "Modelo IFC STEP (*.ifc)|*.ifc", CheckFileExists = true,
        CheckPathExists = true, Multiselect = false
    };

    private static void Reemplazar<T>(ColeccionBim<T> target, IEnumerable<T> values) => target.Reemplazar(values);

    public async Task DetenerAsync()
    {
        if (_detenido) return;
        _detenido = true;
        _lifetime.Cancel();
        _proyectoLifetime.Cancel();
        await _operaciones.WaitAsync();
        _operaciones.Release();
    }
}

public sealed record ReglaMedicionOpcion(ReglaMedicionBim Valor, string Nombre);

public sealed record MedicionFilaBim(MedicionVinculadaBimDto Valor)
{
    public string ReglaDescripcion => TextoRegla(Valor.Regla);

    public static string TextoRegla(ReglaMedicionBim regla) => regla switch
    {
        ReglaMedicionBim.VolumenNeto => "Volumen neto", ReglaMedicionBim.VolumenBruto => "Volumen bruto",
        ReglaMedicionBim.AreaNeta => "Área neta", ReglaMedicionBim.AreaBruta => "Área bruta",
        ReglaMedicionBim.Longitud => "Longitud", ReglaMedicionBim.Unidades => "Unidades",
        _ => throw new ArgumentOutOfRangeException(nameof(regla))
    };
}

public sealed record CambioFilaBim(CambioElementoBimDto Valor)
{
    public string TipoCambio => Valor.Cambio switch
    {
        TipoCambioBim.Anadido => "Añadido", TipoCambioBim.Eliminado => "Eliminado",
        TipoCambioBim.Modificado => "Modificado", _ => throw new ArgumentOutOfRangeException(nameof(Valor))
    };
    public string Nombre => Valor.Nuevo?.Nombre ?? Valor.Anterior?.Nombre ?? "";
    public string ClaseIfc => Valor.Nuevo?.ClaseIfc ?? Valor.Anterior?.ClaseIfc ?? "";
}
