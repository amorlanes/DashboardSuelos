using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Constructora.Application;
using Constructora.Application.Bim;
using Constructora.Client.Bim;
using Constructora.Client.Core;
using Constructora.Domain;
using Constructora.Infrastructure.Ifc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Constructora.Bim.Integration.Tests;

public sealed class FlujoIfcLocalTests
{
    private static readonly Guid ProyectoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid HormigonId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CerramientoId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string MuroHormigon = "1YvctVUKr0kugbFTf53O9L";
    private const string MuroEliminado = "2YvctVUKr0kugbFTf53O9M";
    private const string MuroAnadido = "3YvctVUKr0kugbFTf53O9N";

    [Fact]
    public async Task ImportarVincularCompararYReabrirConservaIdentidadesMedicionesYValoracionParcial()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var primera = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));
        Assert.Equal("IFC4", primera.Esquema);
        Assert.Equal(1, primera.Numero);
        Assert.Equal(2, primera.NumeroElementos);
        var elementosV1 = await ambiente.Bim.ObtenerElementosAsync(primera.Id);
        Assert.Equal(12m, elementosV1.Single(e => e.GlobalId == MuroHormigon).Cantidades.VolumenNeto);
        Assert.Equal(30m, elementosV1.Single(e => e.GlobalId == MuroEliminado).Cantidades.AreaNeta);
        var hormigon = await ambiente.Bim.VincularAsync(primera.ModeloId, MuroHormigon,
            HormigonId, ReglaMedicionBim.VolumenNeto, 1m);
        var cerramiento = await ambiente.Bim.VincularAsync(primera.ModeloId, MuroEliminado,
            CerramientoId, ReglaMedicionBim.AreaNeta, 1m);
        var medicionesV1 = await ambiente.Bim.ObtenerMedicionesAsync(primera.Id);
        Assert.Equal(12m, medicionesV1.Single(m => m.VinculoId == hormigon.Id).Cantidad);
        Assert.Equal("m3", medicionesV1.Single(m => m.VinculoId == hormigon.Id).Unidad);
        Assert.Equal(1800m, medicionesV1.Single(m => m.VinculoId == hormigon.Id).Importe);
        Assert.Equal(30m, medicionesV1.Single(m => m.VinculoId == cerramiento.Id).Cantidad);
        Assert.Equal(2550m, medicionesV1.Single(m => m.VinculoId == cerramiento.Id).Importe);

        var segunda = await ambiente.Bim.ImportarAsync(ProyectoId, primera.ModeloId, Ejemplo(2));
        Assert.Equal(2, segunda.Numero);
        Assert.Equal(primera.ModeloId, segunda.ModeloId);
        var parcial = await ambiente.Bim.CompararAsync(primera.Id, segunda.Id);
        Assert.Equal(3, parcial.Cambios.Count);
        Assert.Equal(TipoCambioBim.Modificado, parcial.Cambios.Single(c => c.GlobalId == MuroHormigon).Cambio);
        Assert.Equal(TipoCambioBim.Eliminado, parcial.Cambios.Single(c => c.GlobalId == MuroEliminado).Cambio);
        Assert.Equal(TipoCambioBim.Anadido, parcial.Cambios.Single(c => c.GlobalId == MuroAnadido).Cambio);
        // +3 m³ × 150 €/m³ - 30 m² × 85 €/m². El nuevo muro aún no tiene vínculo.
        Assert.Equal(-2100m, parcial.ImpactoEconomicoConocido);
        Assert.Equal(1, parcial.ElementosSinValorar);
        Assert.Contains(parcial.Advertencias, aviso => aviso.Contains("parcial", StringComparison.OrdinalIgnoreCase));

        var nuevo = await ambiente.Bim.VincularAsync(primera.ModeloId, MuroAnadido,
            CerramientoId, ReglaMedicionBim.AreaNeta, 1m);
        var completa = await ambiente.Bim.CompararAsync(primera.Id, segunda.Id);
        // Al vincular el nuevo muro se suman sus 36 m² × 85 €/m²: +960 € en total.
        Assert.Equal(960m, completa.ImpactoEconomicoConocido);
        Assert.Equal(0, completa.ElementosSinValorar);
        var medicionesV2 = await ambiente.Bim.ObtenerMedicionesAsync(segunda.Id);
        Assert.Equal(2, medicionesV2.Count);
        Assert.Equal(15m, medicionesV2.Single(m => m.VinculoId == hormigon.Id).Cantidad);
        Assert.Equal(36m, medicionesV2.Single(m => m.VinculoId == nuevo.Id).Cantidad);
        Assert.DoesNotContain(medicionesV2, m => m.GlobalId == MuroEliminado);

        await ambiente.ReabrirAsync();

        Assert.Equal(primera.ModeloId, Assert.Single(await ambiente.Bim.ObtenerModelosAsync(ProyectoId)).Id);
        Assert.Equal(new[] { segunda.Id, primera.Id },
            (await ambiente.Bim.ObtenerVersionesAsync(primera.ModeloId)).Select(v => v.Id));
        Assert.Equal(new[] { hormigon.Id, cerramiento.Id, nuevo.Id }.Order(),
            (await ambiente.Bim.ObtenerVinculosAsync(primera.ModeloId)).Select(v => v.Id).Order());
        Assert.Equal(elementosV1, await ambiente.Bim.ObtenerElementosAsync(primera.Id));
        Assert.Equal(medicionesV1, await ambiente.Bim.ObtenerMedicionesAsync(primera.Id));
        Assert.Equal(medicionesV2, await ambiente.Bim.ObtenerMedicionesAsync(segunda.Id));
        Assert.Equal(960m, (await ambiente.Bim.CompararAsync(primera.Id, segunda.Id)).ImpactoEconomicoConocido);
    }

    [Fact]
    public async Task UnElementoPuedeAportarVolumenYAreaAPartidasDistintasYActualizarSuFactorSinDuplicarse()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var version = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));
        var volumen = await ambiente.Bim.VincularAsync(version.ModeloId, MuroHormigon,
            HormigonId, ReglaMedicionBim.VolumenNeto, 1m);
        await ambiente.Bim.VincularAsync(version.ModeloId, MuroHormigon,
            CerramientoId, ReglaMedicionBim.AreaNeta, 0.5m);
        var actualizado = await ambiente.Bim.VincularAsync(version.ModeloId, MuroHormigon,
            HormigonId, ReglaMedicionBim.VolumenNeto, 1.25m);

        Assert.Equal(volumen.Id, actualizado.Id);
        Assert.Equal(2, (await ambiente.Bim.ObtenerVinculosAsync(version.ModeloId)).Count);
        var mediciones = await ambiente.Bim.ObtenerMedicionesAsync(version.Id);
        Assert.Equal(15m, mediciones.Single(m => m.PartidaId == HormigonId).Cantidad);
        Assert.Equal(2250m, mediciones.Single(m => m.PartidaId == HormigonId).Importe);
        Assert.Equal(20m, mediciones.Single(m => m.PartidaId == CerramientoId).Cantidad);
        Assert.Equal(1700m, mediciones.Single(m => m.PartidaId == CerramientoId).Importe);
        await Assert.ThrowsAsync<ArgumentException>(() => ambiente.Bim.VincularAsync(version.ModeloId,
            MuroHormigon, CerramientoId, ReglaMedicionBim.VolumenNeto, 1m));
        Assert.Equal(2, (await ambiente.Bim.ObtenerVinculosAsync(version.ModeloId)).Count);
    }

    [Fact]
    public async Task ReimportarLosMismosBytesEnElMismoModeloNoDuplicaVersionNiOriginal()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var primera = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));
        var repetida = await ambiente.Bim.ImportarAsync(ProyectoId, primera.ModeloId, Ejemplo(1));

        Assert.Equal(primera.Id, repetida.Id);
        Assert.Equal(primera.Sha256, repetida.Sha256);
        Assert.Single(await ambiente.Bim.ObtenerModelosAsync(ProyectoId));
        Assert.Equal(primera.Id, Assert.Single(await ambiente.Bim.ObtenerVersionesAsync(primera.ModeloId)).Id);
        Assert.Single(Directory.GetFiles(ambiente.ArchivoDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OriginalArchivadoSobreviveAlBorradoDelOrigenYSeExportaSinCambiarUnByte()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var origen = Path.Combine(ambiente.Directorio, "entrada.ifc");
        var exportado = Path.Combine(ambiente.Directorio, "exportado.ifc");
        File.Copy(Ejemplo(1), origen);
        var bytesOriginales = await File.ReadAllBytesAsync(origen);
        var interpretado = await ambiente.Importador.ImportarAsync(origen);
        var version = await ambiente.Bim.ImportarAsync(ProyectoId, null, origen);
        File.Delete(origen);

        await ambiente.ReabrirAsync();
        await ambiente.Bim.ExportarOriginalAsync(version.Id, exportado);

        var bytesExportados = await File.ReadAllBytesAsync(exportado);
        Assert.Equal(bytesOriginales, bytesExportados);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytesOriginales)), version.Sha256, ignoreCase: true);
        var reinterpretado = await ambiente.Importador.ImportarAsync(exportado);
        Assert.Equal(interpretado.Esquema, reinterpretado.Esquema);
        Assert.Equal(interpretado.ProyectoIfcGlobalId, reinterpretado.ProyectoIfcGlobalId);
        Assert.Equal(interpretado.Elementos, reinterpretado.Elementos);
    }

    [Fact]
    public async Task CoincidirGlobalIdsNoPermiteCompararModelosDiferentes()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var primera = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));
        var otroModelo = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));

        Assert.NotEqual(primera.ModeloId, otroModelo.ModeloId);
        Assert.Equal((await ambiente.Bim.ObtenerElementosAsync(primera.Id)).Select(e => e.GlobalId),
            (await ambiente.Bim.ObtenerElementosAsync(otroModelo.Id)).Select(e => e.GlobalId));
        await Assert.ThrowsAsync<ArgumentException>(() => ambiente.Bim.CompararAsync(primera.Id, otroModelo.Id));
    }

    [Fact]
    public async Task IfcTruncadoNoDejaModeloRevisionNiOriginalParciales()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var origen = Path.Combine(ambiente.Directorio, "truncado.ifc");
        var texto = await File.ReadAllTextAsync(Ejemplo(1));
        await File.WriteAllTextAsync(origen, texto[..texto.IndexOf("END-ISO-10303-21;", StringComparison.Ordinal)]);
        var antes = await ambiente.CapturarCoreAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => ambiente.Bim.ImportarAsync(ProyectoId, null, origen));

        Assert.Empty(await ambiente.Bim.ObtenerModelosAsync(ProyectoId));
        Assert.Empty(Directory.GetFiles(ambiente.ArchivoDir, "*", SearchOption.AllDirectories));
        Assert.Equal(antes, await ambiente.CapturarCoreAsync());
        // Un fallo no bloquea el siguiente intento válido.
        Assert.Equal(1, (await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1))).Numero);
    }

    [Fact]
    public async Task ExportarDetectaUnOriginalAlteradoYConservaArchivosExistentes()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var version = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));
        var destino = Path.Combine(ambiente.Directorio, "original-exportado.ifc");
        await File.WriteAllTextAsync(destino, "Archivo del usuario que debe conservarse.");

        await Assert.ThrowsAsync<IOException>(() => ambiente.Bim.ExportarOriginalAsync(version.Id, destino));
        Assert.Equal("Archivo del usuario que debe conservarse.", await File.ReadAllTextAsync(destino));

        var archivado = Assert.Single(Directory.GetFiles(ambiente.ArchivoDir, "*", SearchOption.AllDirectories));
        await File.AppendAllTextAsync(archivado, "\nContenido alterado después de la importación.");
        var destinoNuevo = Path.Combine(ambiente.Directorio, "no-debe-crearse.ifc");
        await Assert.ThrowsAsync<InvalidDataException>(() => ambiente.Bim.ExportarOriginalAsync(version.Id, destinoNuevo));
        Assert.False(File.Exists(destinoNuevo));
        Assert.Empty(Directory.GetFiles(ambiente.Directorio, ".bim-export-*.tmp"));
    }

    [Fact]
    public async Task ImportacionesVinculosYComparacionesNoModificanCertificacionesNiColaDeSincronizacion()
    {
        await using var ambiente = await Ambiente.CrearAsync();
        var antes = await ambiente.CapturarCoreAsync();
        var primera = await ambiente.Bim.ImportarAsync(ProyectoId, null, Ejemplo(1));
        var elemento = (await ambiente.Bim.ObtenerElementosAsync(primera.Id))
            .First(e => e.Cantidades.VolumenNeto.HasValue);
        var vinculo = await ambiente.Bim.VincularAsync(primera.ModeloId, elemento.GlobalId,
            HormigonId, ReglaMedicionBim.VolumenNeto, 1m);
        var segunda = await ambiente.Bim.ImportarAsync(ProyectoId, primera.ModeloId, Ejemplo(2));
        await ambiente.Bim.ObtenerMedicionesAsync(primera.Id);
        await ambiente.Bim.ObtenerMedicionesAsync(segunda.Id);
        await ambiente.Bim.CompararAsync(primera.Id, segunda.Id);
        await ambiente.Bim.EliminarVinculoAsync(vinculo.Id);
        await ambiente.ReabrirAsync();

        Assert.Equal(antes, await ambiente.CapturarCoreAsync());
        await using var db = await ambiente.CoreFactory.CreateDbContextAsync();
        var certificacion = await db.Certificaciones.Include(c => c.Lineas).SingleAsync();
        Assert.Equal(2.5m, Assert.Single(certificacion.Lineas).CantidadPeriodo);
        Assert.Equal(EstadoCertificacion.Aprobada, certificacion.Estado);
        Assert.True(certificacion.EsPendienteSincronizacion);
        Assert.True(certificacion.TieneConflicto);
        Assert.Single(await db.OperacionesPendientes.ToListAsync());
    }

    private static string Ejemplo(int version) => Path.Combine(AppContext.BaseDirectory, "IFC", $"Modelo-v{version}.ifc");

    private sealed class Ambiente : IAsyncDisposable
    {
        private ServiceProvider _servicios;
        private readonly string _corePath;
        private readonly string _bimPath;
        public string Directorio { get; }
        public string ArchivoDir { get; }
        public IBimService Bim => _servicios.GetRequiredService<IBimService>();
        public IImportadorIfc Importador => _servicios.GetRequiredService<IImportadorIfc>();
        public IDbContextFactory<SqliteLocalDbContext> CoreFactory =>
            _servicios.GetRequiredService<IDbContextFactory<SqliteLocalDbContext>>();

        private Ambiente()
        {
            Directorio = Path.Combine(Path.GetTempPath(), $"constructora-bim-integracion-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Directorio);
            _corePath = Path.Combine(Directorio, "certificaciones.sqlite");
            _bimPath = Path.Combine(Directorio, "modelos-bim.sqlite");
            ArchivoDir = Path.Combine(Directorio, "originales");
            _servicios = CrearServicios();
        }

        public static async Task<Ambiente> CrearAsync()
        {
            var ambiente = new Ambiente();
            try
            {
                await using var db = await ambiente.CoreFactory.CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                db.Proyectos.Add(new ProyectoLocal { Id = ProyectoId, Nombre = "Vivienda de demostración",
                    Codigo = "DEMO-01", FechaInicio = new DateOnly(2026, 1, 1) });
                db.Partidas.AddRange(
                    new PartidaLocal { Id = HormigonId, ProyectoId = ProyectoId, Codigo = "EST-01",
                        Descripcion = "Hormigón estructural", Unidad = "m3", PrecioUnitario = 150m, CantidadPrevista = 120m },
                    new PartidaLocal { Id = CerramientoId, ProyectoId = ProyectoId, Codigo = "CER-01",
                        Descripcion = "Cerramiento", Unidad = "m2", PrecioUnitario = 85m, CantidadPrevista = 250m });
                var certificacionId = Guid.NewGuid();
                var lineaId = Guid.NewGuid();
                var dto = new CertificacionDto(certificacionId, ProyectoId, 1, new DateOnly(2026, 10, 1),
                    EstadoCertificacion.Aprobada, 32, [new(lineaId, HormigonId, 7m, 2.5m, 9m)]);
                db.Certificaciones.Add(new CertificacionLocal { Id = certificacionId, ProyectoId = ProyectoId,
                    NumeroCertificacion = 1, Fecha = dto.Fecha, Estado = dto.Estado, RowVersion = dto.RowVersion,
                    RevisionLocal = 4, EsPendienteSincronizacion = true, TieneConflicto = true,
                    VersionesEdicionJson = "[25,32]", VersionServidorJson = JsonSerializer.Serialize(dto),
                    UltimoError = "Conflicto previo a la importación BIM.",
                    Lineas = [new() { Id = lineaId, CertificacionId = certificacionId, PartidaId = HormigonId,
                        CantidadAcumuladaAnterior = 7m, CantidadPeriodo = 2.5m, PorcentajeAvance = 9m }] });
                db.OperacionesPendientes.Add(new OperacionSincronizacionLocal { CertificacionId = certificacionId,
                    OperacionId = Guid.NewGuid(), RevisionLocal = 4, Preparada = true,
                    PayloadJson = JsonSerializer.Serialize(new SincronizarCertificacionRequest(Guid.NewGuid(), dto)) });
                await db.SaveChangesAsync();
                await ambiente.Bim.InicializarAsync();
                return ambiente;
            }
            catch
            {
                await ambiente.DisposeAsync();
                throw;
            }
        }

        private ServiceProvider CrearServicios()
        {
            var servicios = new ServiceCollection();
            servicios.AddLogging();
            servicios.AddConstructoraClient(new Uri("http://127.0.0.1:1/"), _corePath);
            servicios.AddSingleton<IImportadorIfc, ImportadorIfc>();
            servicios.AddConstructoraBim(_bimPath, ArchivoDir);
            return servicios.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        public async Task ReabrirAsync()
        {
            await _servicios.DisposeAsync();
            _servicios = CrearServicios();
            await Bim.InicializarAsync();
        }

        public async Task<string> CapturarCoreAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = _corePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            await connection.OpenAsync();
            var tablas = new[] { "Proyectos", "Partidas", "Certificaciones", "LineaCertificacionLocal", "OperacionesPendientes" };
            var contenido = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            foreach (var tabla in tablas)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{tabla}\" ORDER BY rowid";
                await using var reader = await command.ExecuteReaderAsync();
                var filas = new List<string[]> { Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray() };
                while (await reader.ReadAsync())
                    filas.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<NULL>" :
                        Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!).ToArray());
                contenido.Add(tabla, filas);
            }
            return JsonSerializer.Serialize(contenido);
        }

        public async ValueTask DisposeAsync()
        {
            await _servicios.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Directorio)) Directory.Delete(Directorio, recursive: true);
        }
    }
}
