using Constructora.Application.Bim;
using Constructora.Client.Bim;
using Constructora.Client.Core;
using Constructora.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Constructora.Client.Bim.Tests;

public sealed class BimServiceTests : IAsyncLifetime
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "constructora-bim-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _proyecto = Guid.NewGuid();
    private readonly Guid _otroProyecto = Guid.NewGuid();
    private readonly Guid _hormigon = Guid.NewGuid();
    private readonly Guid _encofrado = Guid.NewGuid();
    private readonly Guid _otraPartida = Guid.NewGuid();
    private readonly Guid _certificacion = Guid.NewGuid();
    private readonly FallaGuardar _fallo = new();
    private DbFactory<SqliteLocalDbContext> _catalogo = null!;
    private DbFactory<BimLocalDbContext> _bim = null!;
    private ImportadorPrueba _importador = null!;
    private BimService _servicio = null!;
    private string ArchivoDir => Path.Combine(_raiz, "Originales");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_raiz);
        _catalogo = new(new DbContextOptionsBuilder<SqliteLocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_raiz, "core.db")};Foreign Keys=True;Pooling=False").Options,
            o => new SqliteLocalDbContext(o));
        _bim = new(new DbContextOptionsBuilder<BimLocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_raiz, "bim.db")};Foreign Keys=True;Pooling=False")
            .AddInterceptors(_fallo).Options, o => new BimLocalDbContext(o));
        await using var catalogo = _catalogo.CreateDbContext();
        await catalogo.Database.EnsureCreatedAsync();
        catalogo.Proyectos.AddRange(
            new ProyectoLocal { Id = _proyecto, Nombre = "Obra", Codigo = "OBRA" },
            new ProyectoLocal { Id = _otroProyecto, Nombre = "Otra", Codigo = "OTRA" });
        catalogo.Partidas.AddRange(
            new PartidaLocal { Id = _hormigon, ProyectoId = _proyecto, Codigo = "H", Unidad = "m³", PrecioUnitario = 150m },
            new PartidaLocal { Id = _encofrado, ProyectoId = _proyecto, Codigo = "E", Unidad = "m²", PrecioUnitario = 20m },
            new PartidaLocal { Id = _otraPartida, ProyectoId = _otroProyecto, Codigo = "H", Unidad = "m3", PrecioUnitario = 2m });
        catalogo.Certificaciones.Add(new CertificacionLocal
        {
            Id = _certificacion, ProyectoId = _proyecto, NumeroCertificacion = 1, Estado = EstadoCertificacion.Aprobada,
            RowVersion = 42, Lineas = [new() { Id = Guid.NewGuid(), PartidaId = _hormigon, CantidadPeriodo = 6m }]
        });
        await catalogo.SaveChangesAsync();
        _importador = new();
        _servicio = CrearServicio();
        await _servicio.InicializarAsync();
    }

    private BimService CrearServicio() => new(_bim, _catalogo, _importador, NullLogger<BimService>.Instance, ArchivoDir);

    public Task DisposeAsync()
    {
        _servicio.Dispose();
        Directory.Delete(_raiz, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DosRevisionesCalculanDiferenciasYConservanCertificacionAprobada()
    {
        var primera = await Importar("uno", null, Elemento("A", 10, 12), Elemento("B", 3, 0));
        await _servicio.VincularAsync(primera.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenNeto, 1m);
        await _servicio.VincularAsync(primera.ModeloId, "A", _encofrado, ReglaMedicionBim.AreaNeta, 2m);
        await _servicio.VincularAsync(primera.ModeloId, "B", _hormigon, ReglaMedicionBim.VolumenNeto, 1m);
        var segunda = await Importar("dos", primera.ModeloId, Elemento("A", 12, 13), Elemento("C", 1, 1));
        var resultado = await _servicio.CompararAsync(primera.Id, segunda.Id);
        Assert.Equal(3, resultado.Cambios.Count);
        Assert.Equal(-110m, resultado.ImpactoEconomicoConocido); // 2*150 + (13-12)*2*20 - 3*150
        Assert.Equal(1, resultado.ElementosSinValorar); // C has no mapping.
        Assert.Contains(resultado.Cambios, c => c.GlobalId == "A" && c.Cambio == TipoCambioBim.Modificado);
        Assert.Contains(resultado.Cambios, c => c.GlobalId == "B" && c.Cambio == TipoCambioBim.Eliminado);
        Assert.Contains(resultado.Cambios, c => c.GlobalId == "C" && c.Cambio == TipoCambioBim.Anadido);
        Assert.Equal(3, (await _servicio.ObtenerVinculosAsync(primera.ModeloId)).Count); // Deleted B's mapping is retained.
        Assert.Equal(2, (await _servicio.ObtenerMedicionesAsync(segunda.Id)).Count);
        Assert.Equal(10m, (await _servicio.ObtenerElementosAsync(primera.Id)).Single(e => e.GlobalId == "A").Cantidades.VolumenNeto);
        await using var catalogo = _catalogo.CreateDbContext();
        var certificado = await catalogo.Certificaciones.Include(x => x.Lineas).SingleAsync(x => x.Id == _certificacion);
        Assert.Equal(EstadoCertificacion.Aprobada, certificado.Estado);
        Assert.Equal(42u, certificado.RowVersion);
        Assert.Equal(6m, Assert.Single(certificado.Lineas).CantidadPeriodo);
    }

    [Fact]
    public async Task ReabrirConservaHistorialYReimportarMismoHashNoCreaRevision()
    {
        var primera = await Importar("uno", null, Elemento("A", 2, 4));
        await _servicio.VincularAsync(primera.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenNeto, 1m);
        using var reabierto = CrearServicio();
        await reabierto.InicializarAsync();
        var repetida = await reabierto.ImportarAsync(_proyecto, primera.ModeloId, Ruta("uno"));
        Assert.Equal(primera.Id, repetida.Id);
        Assert.Single(await reabierto.ObtenerVersionesAsync(primera.ModeloId));
        Assert.Single(await reabierto.ObtenerVinculosAsync(primera.ModeloId));
        Assert.Single(Directory.GetFiles(ArchivoDir));
        Assert.Equal(1, _importador.Lecturas);
    }

    [Fact]
    public async Task GlobalIdIgualEnModelosDistintosNoComparteVinculos()
    {
        var a = await Importar("a", null, Elemento("A", 2, 1));
        var b = await Importar("b", null, Elemento("A", 7, 1));
        await _servicio.VincularAsync(a.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenNeto, 1m);
        Assert.Empty(await _servicio.ObtenerVinculosAsync(b.ModeloId));
        Assert.Empty(await _servicio.ObtenerMedicionesAsync(b.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => _servicio.CompararAsync(a.Id, b.Id));
    }

    [Fact]
    public async Task NoVinculaPartidasDeOtroProyectoNiUnidadesIncompatibles()
    {
        var a = await Importar("a", null, Elemento("A", 2, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => _servicio.VincularAsync(a.ModeloId, "A", _otraPartida, ReglaMedicionBim.VolumenNeto, 1m));
        await Assert.ThrowsAsync<ArgumentException>(() => _servicio.VincularAsync(a.ModeloId, "A", _encofrado, ReglaMedicionBim.VolumenNeto, 1m));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _servicio.VincularAsync(a.ModeloId, "desconocido", _hormigon, ReglaMedicionBim.VolumenNeto, 1m));
        Assert.Empty(await _servicio.ObtenerVinculosAsync(a.ModeloId));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000000001")]
    [InlineData("0.0000001")]
    public async Task RechazaFactoresNoValidos(string texto)
    {
        var a = await Importar("a", null, Elemento("A", 2, 1));
        var factor = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _servicio.VincularAsync(a.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenNeto, factor));
    }

    [Fact]
    public async Task CantidadAusenteNoSeSustituyePorBrutaOCeroYGeneraValoracionParcial()
    {
        var a = await Importar("a", null, Elemento("A", 2, 1));
        await _servicio.VincularAsync(a.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenNeto, 1m);
        var elemento = Elemento("A", null, 1) with { Cantidades = new(null, 99, 1, 2, null, 1) };
        var b = await Importar("b", a.ModeloId, elemento);
        var medida = Assert.Single(await _servicio.ObtenerMedicionesAsync(b.Id));
        Assert.Null(medida.Cantidad);
        Assert.Null(medida.Importe);
        Assert.NotNull(medida.Aviso);
        var comparacion = await _servicio.CompararAsync(a.Id, b.Id);
        Assert.Equal(1, comparacion.ElementosSinValorar);
        Assert.Equal(0m, comparacion.ImpactoEconomicoConocido); // The previous known amount alone is not a meaningful delta.
        Assert.Contains(comparacion.Advertencias, x => x.Contains("parcial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CambiarReglaDeMismaPartidaNoDuplicaCosteYReflejaPrecioActual()
    {
        var elemento = Elemento("A", 2, 1) with { Cantidades = new(2, 3, 1, 1, null, 1) };
        var a = await Importar("a", null, elemento);
        var neto = await _servicio.VincularAsync(a.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenNeto, 1m);
        var bruto = await _servicio.VincularAsync(a.ModeloId, "A", _hormigon, ReglaMedicionBim.VolumenBruto, 2m);
        Assert.Equal(neto.Id, bruto.Id);
        await using var catalogo = _catalogo.CreateDbContext();
        var partida = await catalogo.Partidas.SingleAsync(x => x.Id == _hormigon);
        partida.PrecioUnitario = 200m;
        await catalogo.SaveChangesAsync();
        var medida = Assert.Single(await _servicio.ObtenerMedicionesAsync(a.Id));
        Assert.Equal(200m, medida.PrecioUnitario);
        Assert.Equal(6m, medida.Cantidad);
        Assert.Equal(1200m, medida.Importe);
    }

    [Fact]
    public async Task ConservaCopiasDeOriginalesAunqueElUsuarioModifiqueSuIfc()
    {
        var a = await Importar("a", null, Elemento("A", 2, 1));
        Assert.NotEqual(Ruta("a"), _importador.UltimaRuta);
        await File.WriteAllTextAsync(Ruta("a"), "modificado por el usuario");
        var exportado = Path.Combine(_raiz, "copia.ifc");
        await _servicio.ExportarOriginalAsync(a.Id, exportado);
        Assert.Equal("a", await File.ReadAllTextAsync(exportado));
        Assert.Equal(2m, Assert.Single(await _servicio.ObtenerElementosAsync(a.Id)).Cantidades.VolumenNeto);
        await Assert.ThrowsAsync<IOException>(() => _servicio.ExportarOriginalAsync(a.Id, exportado));
        Assert.Equal("a", await File.ReadAllTextAsync(exportado));
    }

    [Fact]
    public async Task ExportacionDetectaCorrupcionSinCrearArchivoDestino()
    {
        var a = await Importar("a", null, Elemento("A", 2, 1));
        await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(ArchivoDir)), "corrupto");
        var exportado = Path.Combine(_raiz, "copia.ifc");
        await Assert.ThrowsAsync<InvalidDataException>(() => _servicio.ExportarOriginalAsync(a.Id, exportado));
        Assert.False(File.Exists(exportado));
        Assert.Empty(Directory.GetFiles(_raiz, ".bim-export-*"));
    }

    [Fact]
    public async Task ParseFallidoYFalloDbNoDejanModeloNiRevisionNiArchivosParciales()
    {
        _importador.Error = new InvalidDataException("IFC inválido");
        await File.WriteAllTextAsync(Ruta("error"), "error");
        await Assert.ThrowsAsync<InvalidDataException>(() => _servicio.ImportarAsync(_proyecto, null, Ruta("error")));
        Assert.Empty(await _servicio.ObtenerModelosAsync(_proyecto));
        Assert.Empty(Directory.GetFiles(ArchivoDir));
        _importador.Error = null;
        _fallo.Activo = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => Importar("db", null, Elemento("A", 1, 1)));
        _fallo.Activo = false;
        Assert.Empty(await _servicio.ObtenerModelosAsync(_proyecto));
        Assert.Empty(Directory.GetFiles(ArchivoDir));
        Assert.True(File.Exists(Ruta("db")));
    }

    [Fact]
    public async Task FalloSqlDespuesDeInsertarModeloRevierteTodaLaTransaccion()
    {
        await using (var db = _bim.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RechazarRevision BEFORE INSERT ON Versiones
                BEGIN SELECT RAISE(ABORT, 'Error de almacenamiento simulado'); END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => Importar("fallo-sql", null, Elemento("A", 1, 1)));
        Assert.Empty(await _servicio.ObtenerModelosAsync(_proyecto));
        await using var comprobacion = _bim.CreateDbContext();
        Assert.Empty(await comprobacion.Versiones.ToListAsync());
        Assert.Empty(Directory.GetFiles(ArchivoDir));
    }

    [Fact]
    public async Task CancelarDuranteLecturaConservaSoloArchivosDeRevisionesConfirmadas()
    {
        var a = await Importar("a", null, Elemento("A", 1, 1));
        _importador.Error = new OperationCanceledException("Cancelación durante importación IFC.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Importar("cancelar", a.ModeloId, Elemento("A", 2, 1)));
        Assert.Single(Directory.GetFiles(ArchivoDir));
        Assert.Single(await _servicio.ObtenerVersionesAsync(a.ModeloId));
    }

    [Fact]
    public async Task ProyectoIfcDistintoNoEntraEnHistorialYConservaOriginalAnterior()
    {
        var a = await Importar("a", null, Elemento("A", 1, 1));
        _importador.ProyectoIfc = "otro-ifc-project";
        await Assert.ThrowsAsync<InvalidDataException>(() => Importar("b", a.ModeloId, Elemento("A", 2, 1)));
        Assert.Single(await _servicio.ObtenerVersionesAsync(a.ModeloId));
        Assert.Single(Directory.GetFiles(ArchivoDir));
    }

    [Fact]
    public async Task LimiteArchivoYCancellationNoDejanArchivosNuevos()
    {
        var grande = Ruta("grande");
        await using (var archivo = File.Create(grande)) archivo.SetLength(BimService.MaximoBytesIfc + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => _servicio.ImportarAsync(_proyecto, null, grande));
        Assert.Empty(Directory.GetFiles(ArchivoDir));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _servicio.ImportarAsync(_proyecto, null, grande, cts.Token));
        Assert.Empty(await _servicio.ObtenerModelosAsync(_proyecto));
    }

    [Fact]
    public async Task ToleranciaIgnoraRuidoDeConversionPeroDetectaMetadatos()
    {
        // Importers convert explicit feet quantities to SI before handing data to persistence.
        var metros = 10m * 0.3048m;
        var a = await Importar("a", null, Elemento("A", metros, 1));
        var b = await Importar("b", a.ModeloId, Elemento("A", metros + 0.00000001m, 1));
        Assert.Empty((await _servicio.CompararAsync(a.Id, b.Id)).Cambios);
        var c = await Importar("c", a.ModeloId, Elemento("A", metros, 1) with { Tipo = "Tipo modificado" });
        Assert.Single((await _servicio.CompararAsync(b.Id, c.Id)).Cambios);
    }

    [Fact]
    public async Task ProyectoInexistenteYReimportacionCrossProyectoSeRechazan()
    {
        var a = await Importar("a", null, Elemento("A", 1, 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _servicio.ImportarAsync(Guid.NewGuid(), null, Ruta("a")));
        await Assert.ThrowsAsync<ArgumentException>(() => _servicio.ImportarAsync(_otroProyecto, a.ModeloId, Ruta("a")));
        Assert.Single(Directory.GetFiles(ArchivoDir));
    }

    private string Ruta(string contenido) => Path.Combine(_raiz, contenido + ".ifc");

    private async Task<VersionModeloBimDto> Importar(string contenido, Guid? modeloId, params ElementoModeloBimDto[] elementos)
    {
        _importador.Datos[contenido] = elementos;
        await File.WriteAllTextAsync(Ruta(contenido), contenido);
        return await _servicio.ImportarAsync(_proyecto, modeloId, Ruta(contenido));
    }

    private static ElementoModeloBimDto Elemento(string id, decimal? volumen, decimal? area) =>
        new(id, "IfcWall", "Muro " + id, "Muros", "Tipo", "H", new(volumen, null, area, null, null, 1));

    private sealed class ImportadorPrueba : IImportadorIfc
    {
        public Dictionary<string, IReadOnlyList<ElementoModeloBimDto>> Datos { get; } = [];
        public string ProyectoIfc { get; set; } = "proyecto-ifc";
        public Exception? Error { get; set; }
        public string? UltimaRuta { get; private set; }
        public int Lecturas { get; private set; }
        public async Task<ImportacionIfcDto> ImportarAsync(string rutaArchivo, CancellationToken cancellationToken = default)
        {
            Lecturas++;
            UltimaRuta = rutaArchivo;
            if (Error is not null) throw Error;
            var contenido = await File.ReadAllTextAsync(rutaArchivo, cancellationToken);
            return new("IFC4", ProyectoIfc, Datos[contenido], ["Cantidades importadas en SI."]);
        }
    }

    private sealed class DbFactory<T>(DbContextOptions<T> options, Func<DbContextOptions<T>, T> crear) : IDbContextFactory<T> where T : DbContext
    {
        public T CreateDbContext() => crear(options);
    }

    private sealed class FallaGuardar : SaveChangesInterceptor
    {
        public bool Activo { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) => Activo
                ? throw new DbUpdateException("Fallo SQLite simulado antes del commit.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
