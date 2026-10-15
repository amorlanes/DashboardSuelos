using Constructora.Domain;
using Microsoft.EntityFrameworkCore;

namespace Constructora.Infrastructure.Server;

/// <summary>Receipt saved atomically with its aggregate, allowing safe retries after a lost response.</summary>
public sealed class OperacionSincronizacion
{
    public Guid OperacionId { get; set; }
    public Guid CertificacionId { get; set; }
    public string HashSolicitud { get; set; } = string.Empty;
    public string RespuestaJson { get; set; } = string.Empty;
    public DateTime CompletadaUtc { get; set; }
}

public sealed class ServidorDbContext(DbContextOptions<ServidorDbContext> options) : DbContext(options)
{
    public DbSet<Proyecto> Proyectos => Set<Proyecto>();
    public DbSet<Partida> Partidas => Set<Partida>();
    public DbSet<Certificacion> Certificaciones => Set<Certificacion>();
    public DbSet<LineaCertificacion> LineasCertificacion => Set<LineaCertificacion>();
    public DbSet<OperacionSincronizacion> OperacionesSincronizacion => Set<OperacionSincronizacion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var proyecto = modelBuilder.Entity<Proyecto>();
        proyecto.ToTable("Proyectos");
        proyecto.HasKey(x => x.Id);
        proyecto.Property(x => x.Nombre).HasMaxLength(250).IsRequired();
        proyecto.Property(x => x.Codigo).HasMaxLength(80).IsRequired();
        proyecto.HasIndex(x => x.Codigo).IsUnique();

        var partida = modelBuilder.Entity<Partida>();
        partida.ToTable("Partidas", t =>
        {
            t.HasCheckConstraint("CK_Partidas_Precio", "\"PrecioUnitario\" >= 0");
            t.HasCheckConstraint("CK_Partidas_Cantidad", "\"CantidadPrevista\" >= 0");
        });
        partida.HasKey(x => x.Id);
        partida.Property(x => x.Codigo).HasMaxLength(80).IsRequired();
        partida.Property(x => x.Descripcion).HasMaxLength(2000).IsRequired();
        partida.Property(x => x.Unidad).HasMaxLength(30).IsRequired();
        partida.Property(x => x.PrecioUnitario).HasPrecision(18, 4);
        partida.Property(x => x.CantidadPrevista).HasPrecision(18, 4);
        partida.HasIndex(x => new { x.ProyectoId, x.Codigo }).IsUnique();
        partida.HasOne(x => x.Proyecto).WithMany(x => x.Partidas)
            .HasForeignKey(x => x.ProyectoId).OnDelete(DeleteBehavior.Restrict);

        var certificacion = modelBuilder.Entity<Certificacion>();
        certificacion.ToTable("Certificaciones", t =>
        {
            t.HasCheckConstraint("CK_Certificaciones_Numero", "\"NumeroCertificacion\" > 0");
            t.HasCheckConstraint("CK_Certificaciones_Estado", "\"Estado\" BETWEEN 0 AND 2");
        });
        certificacion.HasKey(x => x.Id);
        certificacion.HasIndex(x => new { x.ProyectoId, x.NumeroCertificacion }).IsUnique();
        certificacion.Property(x => x.RowVersion).IsRowVersion();
        certificacion.Property(x => x.ActualizadaUtc).HasColumnType("timestamp with time zone");
        certificacion.HasOne(x => x.Proyecto).WithMany(x => x.Certificaciones)
            .HasForeignKey(x => x.ProyectoId).OnDelete(DeleteBehavior.Restrict);

        var linea = modelBuilder.Entity<LineaCertificacion>();
        linea.ToTable("LineasCertificacion", t =>
        {
            t.HasCheckConstraint("CK_Lineas_Anterior", "\"CantidadAcumuladaAnterior\" >= 0");
            t.HasCheckConstraint("CK_Lineas_Periodo", "\"CantidadPeriodo\" >= 0");
            t.HasCheckConstraint("CK_Lineas_Porcentaje", "\"PorcentajeAvance\" BETWEEN 0 AND 100");
        });
        linea.HasKey(x => x.Id);
        linea.Property(x => x.CantidadAcumuladaAnterior).HasPrecision(18, 4);
        linea.Property(x => x.CantidadPeriodo).HasPrecision(18, 4);
        linea.Property(x => x.PorcentajeAvance).HasPrecision(7, 4);
        linea.HasIndex(x => new { x.CertificacionId, x.PartidaId }).IsUnique();
        linea.HasOne(x => x.Certificacion).WithMany(x => x.Lineas)
            .HasForeignKey(x => x.CertificacionId).OnDelete(DeleteBehavior.Cascade);
        linea.HasOne(x => x.Partida).WithMany(x => x.Lineas)
            .HasForeignKey(x => x.PartidaId).OnDelete(DeleteBehavior.Restrict);

        var operacion = modelBuilder.Entity<OperacionSincronizacion>();
        operacion.ToTable("OperacionesSincronizacion");
        operacion.HasKey(x => x.OperacionId);
        operacion.Property(x => x.HashSolicitud).HasMaxLength(64).IsRequired();
        operacion.Property(x => x.RespuestaJson).HasColumnType("jsonb").IsRequired();
        operacion.Property(x => x.CompletadaUtc).HasColumnType("timestamp with time zone");
        operacion.HasIndex(x => x.CertificacionId);
    }
}
