using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Constructora.Application;
using Constructora.Domain;

namespace Constructora.Wpf.ViewModels;

public sealed partial class LineaCertificacionEditor : ObservableObject
{
    public Guid Id { get; }
    public Guid PartidaId { get; }
    public string Codigo { get; }
    public string Descripcion { get; }
    public string Unidad { get; }
    public decimal PrecioUnitario { get; }
    public decimal CantidadPrevista { get; }
    public decimal CantidadAcumuladaAnterior { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PorcentajeAvance))]
    [NotifyPropertyChangedFor(nameof(ImportePeriodo))]
    private decimal cantidadPeriodo;

    public decimal PorcentajeAvance => CantidadPrevista > 0
        ? decimal.Round((CantidadAcumuladaAnterior + CantidadPeriodo) * 100 / CantidadPrevista, 4)
        : 0;
    public decimal ImportePeriodo => decimal.Round(CantidadPeriodo * PrecioUnitario, 2);

    public LineaCertificacionEditor(LineaCertificacionDto line, PartidaDto item)
    {
        Id = line.Id;
        PartidaId = line.PartidaId;
        Codigo = item.Codigo;
        Descripcion = item.Descripcion;
        Unidad = item.Unidad;
        PrecioUnitario = item.PrecioUnitario;
        CantidadPrevista = item.CantidadPrevista;
        CantidadAcumuladaAnterior = line.CantidadAcumuladaAnterior;
        cantidadPeriodo = line.CantidadPeriodo;
    }

    public LineaCertificacionDto ToDto() => new(Id, PartidaId, CantidadAcumuladaAnterior,
        CantidadPeriodo, PorcentajeAvance);
}

public sealed partial class CertificacionEditor : ObservableObject
{
    private long _revision;
    public Guid Id { get; }
    public Guid ProyectoId { get; }
    public uint RowVersion { get; private set; }
    public long Revision => _revision;
    public ObservableCollection<LineaCertificacionEditor> Lineas { get; } = [];

    [ObservableProperty] private int numeroCertificacion;
    [ObservableProperty] private DateTime fecha;
    [ObservableProperty] private EstadoCertificacion estado;
    [ObservableProperty] private bool tieneCambios;

    public CertificacionEditor(CertificacionDto certification, IReadOnlyList<PartidaDto> partidas)
    {
        Id = certification.Id;
        ProyectoId = certification.ProyectoId;
        RowVersion = certification.RowVersion;
        numeroCertificacion = certification.NumeroCertificacion;
        fecha = certification.Fecha.ToDateTime(TimeOnly.MinValue);
        estado = certification.Estado;
        var items = partidas.ToDictionary(item => item.Id);
        foreach (var line in certification.Lineas)
        {
            if (!items.TryGetValue(line.PartidaId, out var item))
                throw new InvalidOperationException($"Falta la partida {line.PartidaId} en el catálogo local.");
            var editor = new LineaCertificacionEditor(line, item);
            editor.PropertyChanged += OnLineaModificada;
            Lineas.Add(editor);
        }
    }

    private void OnLineaModificada(object? sender, PropertyChangedEventArgs e) => MarcarModificada();
    partial void OnNumeroCertificacionChanged(int value) => MarcarModificada();
    partial void OnFechaChanged(DateTime value) => MarcarModificada();
    partial void OnEstadoChanged(EstadoCertificacion value) => MarcarModificada();

    public void MarcarModificada()
    {
        _revision++;
        TieneCambios = true;
    }

    public void MarcarGuardada(long revisionGuardada)
    {
        // A keystroke during SQLite I/O belongs to a subsequent save.
        if (_revision == revisionGuardada) TieneCambios = false;
    }

    public bool ActualizarVersionSiCoincide(CertificacionDto certification)
    {
        if (TieneCambios || certification.Id != Id || certification.ProyectoId != ProyectoId
            || certification.NumeroCertificacion != NumeroCertificacion
            || certification.Fecha != DateOnly.FromDateTime(Fecha) || certification.Estado != Estado
            || certification.Lineas.Count != Lineas.Count) return false;
        var current = Lineas.Select(line => line.ToDto()).ToDictionary(line => line.Id);
        if (certification.Lineas.Any(line => !current.TryGetValue(line.Id, out var local) || local != line))
            return false;
        RowVersion = certification.RowVersion;
        return true;
    }

    public CertificacionDto ToDto()
    {
        if (NumeroCertificacion <= 0)
            throw new InvalidOperationException("El número de certificación debe ser mayor que cero.");
        if (Lineas.Any(line => line.CantidadPeriodo < 0))
            throw new InvalidOperationException("Las cantidades del período no pueden ser negativas.");
        return new CertificacionDto(Id, ProyectoId, NumeroCertificacion, DateOnly.FromDateTime(Fecha),
            Estado, RowVersion, Lineas.Select(line => line.ToDto()).ToList());
    }
}
