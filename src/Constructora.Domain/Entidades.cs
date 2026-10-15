namespace Constructora.Domain;

public enum EstadoCertificacion { Borrador, Aprobada, Facturada }

public sealed class Proyecto
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Codigo { get; set; } = string.Empty;
    public DateOnly FechaInicio { get; set; }
    public ICollection<Partida> Partidas { get; set; } = new List<Partida>();
    public ICollection<Certificacion> Certificaciones { get; set; } = new List<Certificacion>();
}

public sealed class Partida
{
    public Guid Id { get; set; }
    public Guid ProyectoId { get; set; }
    public Proyecto Proyecto { get; set; } = null!;
    public string Codigo { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string Unidad { get; set; } = string.Empty;
    public decimal PrecioUnitario { get; set; }
    public decimal CantidadPrevista { get; set; }
    public ICollection<LineaCertificacion> Lineas { get; set; } = new List<LineaCertificacion>();
}

public sealed class Certificacion
{
    public Guid Id { get; set; }
    public Guid ProyectoId { get; set; }
    public Proyecto Proyecto { get; set; } = null!;
    public int NumeroCertificacion { get; set; }
    public DateOnly Fecha { get; set; }
    public EstadoCertificacion Estado { get; set; }
    // Npgsql maps this generated concurrency token to PostgreSQL xmin.
    public uint RowVersion { get; set; }
    public DateTime ActualizadaUtc { get; set; }
    public ICollection<LineaCertificacion> Lineas { get; set; } = new List<LineaCertificacion>();
}

public sealed class LineaCertificacion
{
    public Guid Id { get; set; }
    public Guid CertificacionId { get; set; }
    public Certificacion Certificacion { get; set; } = null!;
    public Guid PartidaId { get; set; }
    public Partida Partida { get; set; } = null!;
    public decimal CantidadAcumuladaAnterior { get; set; }
    public decimal CantidadPeriodo { get; set; }
    public decimal PorcentajeAvance { get; set; }
}
