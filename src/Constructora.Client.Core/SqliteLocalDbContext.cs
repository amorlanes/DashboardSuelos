using Constructora.Application;
using Constructora.Domain;
using Microsoft.EntityFrameworkCore;

namespace Constructora.Client.Core;

public sealed class ProyectoLocal
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Codigo { get; set; } = string.Empty;
    public DateOnly FechaInicio { get; set; }
    public ProyectoDto ToDto() => new(Id, Nombre, Codigo, FechaInicio);
}

public sealed class PartidaLocal
{
    public Guid Id { get; set; }
    public Guid ProyectoId { get; set; }
    public string Codigo { get; set; } = string.Empty;
    public string Descripcion { get; set; } = string.Empty;
    public string Unidad { get; set; } = string.Empty;
    public decimal PrecioUnitario { get; set; }
    public decimal CantidadPrevista { get; set; }
    public PartidaDto ToDto() => new(Id, ProyectoId, Codigo, Descripcion, Unidad, PrecioUnitario, CantidadPrevista);
}

public sealed class CertificacionLocal
{
    public Guid Id { get; set; }
    public Guid ProyectoId { get; set; }
    public int NumeroCertificacion { get; set; }
    public DateOnly Fecha { get; set; }
    public EstadoCertificacion Estado { get; set; }
    // Token del servidor. SQLite no genera xmin ni un rowversion binario.
    public uint RowVersion { get; set; }
    public long RevisionLocal { get; set; }
    public string VersionesEdicionJson { get; set; } = "[]";
    public bool EsPendienteSincronizacion { get; set; }
    public bool TieneConflicto { get; set; }
    public string? VersionServidorJson { get; set; }
    public string? UltimoError { get; set; }
    public List<LineaCertificacionLocal> Lineas { get; set; } = [];
    public CertificacionDto ToDto() => new(Id, ProyectoId, NumeroCertificacion, Fecha, Estado, RowVersion,
        Lineas.OrderBy(l => l.Id).Select(l => l.ToDto()).ToList());
}

public sealed class LineaCertificacionLocal
{
    public Guid Id { get; set; }
    public Guid CertificacionId { get; set; }
    public Guid PartidaId { get; set; }
    public decimal CantidadAcumuladaAnterior { get; set; }
    public decimal CantidadPeriodo { get; set; }
    public decimal PorcentajeAvance { get; set; }
    public LineaCertificacionDto ToDto() => new(Id, PartidaId, CantidadAcumuladaAnterior, CantidadPeriodo, PorcentajeAvance);
}

public sealed class OperacionSincronizacionLocal
{
    public long Id { get; set; }
    public Guid OperacionId { get; set; }
    public Guid CertificacionId { get; set; }
    public long RevisionLocal { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public bool Preparada { get; set; }
    public bool Rechazada { get; set; }
}

public sealed class SqliteLocalDbContext(DbContextOptions<SqliteLocalDbContext> options) : DbContext(options)
{
    public DbSet<ProyectoLocal> Proyectos => Set<ProyectoLocal>();
    public DbSet<PartidaLocal> Partidas => Set<PartidaLocal>();
    public DbSet<CertificacionLocal> Certificaciones => Set<CertificacionLocal>();
    public DbSet<OperacionSincronizacionLocal> OperacionesPendientes => Set<OperacionSincronizacionLocal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProyectoLocal>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.Nombre).IsRequired().HasMaxLength(200);
            b.Property(p => p.Codigo).IsRequired().HasMaxLength(50);
        });
        modelBuilder.Entity<PartidaLocal>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.HasIndex(p => new { p.ProyectoId, p.Codigo }).IsUnique();
            b.HasOne<ProyectoLocal>().WithMany().HasForeignKey(p => p.ProyectoId).OnDelete(DeleteBehavior.Restrict);
            b.Property(p => p.PrecioUnitario).HasPrecision(18, 4);
            b.Property(p => p.CantidadPrevista).HasPrecision(18, 4);
        });
        modelBuilder.Entity<CertificacionLocal>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).ValueGeneratedNever();
            b.HasIndex(c => new { c.ProyectoId, c.NumeroCertificacion }).IsUnique();
            b.HasOne<ProyectoLocal>().WithMany().HasForeignKey(c => c.ProyectoId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(c => c.Lineas).WithOne().HasForeignKey(l => l.CertificacionId).OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.RowVersion).ValueGeneratedNever();
            b.Property(c => c.RevisionLocal).IsConcurrencyToken();
        });
        modelBuilder.Entity<LineaCertificacionLocal>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.HasIndex(l => new { l.CertificacionId, l.PartidaId }).IsUnique();
            b.HasOne<PartidaLocal>().WithMany().HasForeignKey(l => l.PartidaId).OnDelete(DeleteBehavior.Restrict);
            b.Property(l => l.CantidadAcumuladaAnterior).HasPrecision(18, 4);
            b.Property(l => l.CantidadPeriodo).HasPrecision(18, 4);
            b.Property(l => l.PorcentajeAvance).HasPrecision(9, 4);
        });
        modelBuilder.Entity<OperacionSincronizacionLocal>(b =>
        {
            b.HasKey(o => o.Id);
            b.HasIndex(o => o.OperacionId).IsUnique();
            b.HasIndex(o => new { o.CertificacionId, o.RevisionLocal }).IsUnique();
            b.HasOne<CertificacionLocal>().WithMany().HasForeignKey(o => o.CertificacionId).OnDelete(DeleteBehavior.Cascade);
            b.Property(o => o.PayloadJson).IsRequired();
        });
    }
}
