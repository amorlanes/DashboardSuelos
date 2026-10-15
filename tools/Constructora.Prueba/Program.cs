using System.Diagnostics;
using Constructora.Client.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Constructora.Prueba;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        }));
        var logger = loggerFactory.CreateLogger("Constructora.Prueba");
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var options = Opciones.Parse(args);
            if (options.Ayuda)
            {
                MostrarAyuda();
                return 0;
            }
            if (!options.SoloPreparar && !OperatingSystem.IsWindows())
                throw new InvalidOperationException("La aplicación de escritorio necesita Windows 10 u 11. Usa --prepare-only para preparar únicamente SQLite.");
            var dataDirectory = options.DirectorioDatos ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Constructora", "Certificaciones");
            Directory.CreateDirectory(dataDirectory);
            var dbPath = Path.Combine(dataDirectory, "certificaciones.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                ForeignKeys = true,
                DefaultTimeout = 10
            }.ToString();
            var dbOptions = new DbContextOptionsBuilder<SqliteLocalDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new SqliteLocalDbContext(dbOptions))
            {
                // EnsureCreated inicializa una base nueva; nunca actualiza un esquema existente.
                await db.Database.EnsureCreatedAsync(cancellation.Token);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellation.Token);
                await PrepararCatalogoAsync(db, logger, cancellation.Token);
            }
            logger.LogInformation("Datos locales preparados en {DatabasePath}. Los registros existentes se han conservado.", dbPath);
            if (options.SoloPreparar) return 0;

            var executable = Path.Combine(AppContext.BaseDirectory, "Constructora.Wpf.exe");
            if (!File.Exists(executable))
                executable = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "Constructora.Wpf.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("No se encuentra Constructora.Wpf.exe. Extrae todo el paquete y conserva la carpeta Preparador junto a la aplicación.", executable);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Windows no pudo abrir la aplicación.");
            logger.LogInformation("Aplicación iniciada. Puedes crear una certificación nueva en el proyecto de demostración y guardarla sin conexión.");
            await process.WaitForExitAsync(cancellation.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"La aplicación terminó con el código {process.ExitCode}. Revisa el mensaje de Windows y el archivo cliente.log de la carpeta de datos.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Preparación cancelada. No se han borrado los datos locales.");
            return 130;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "No se pudo preparar o iniciar Constructora: {Message}", exception.Message);
            return 1;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static async Task PrepararCatalogoAsync(SqliteLocalDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var proyectoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var proyecto = await db.Proyectos.FindAsync([proyectoId], cancellationToken);
        if (proyecto is not null && proyecto.Codigo != "DEMO-01")
        {
            logger.LogWarning("El identificador de demostración ya pertenece a otro proyecto. Se conserva su catálogo sin añadir partidas.");
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (proyecto is null)
        {
            if (await db.Proyectos.AnyAsync(p => p.Codigo == "DEMO-01", cancellationToken))
            {
                logger.LogWarning("Ya existe un proyecto DEMO-01 con otro identificador. Se conserva su catálogo sin añadir otra demostración.");
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            db.Proyectos.Add(new ProyectoLocal
            {
                Id = proyectoId,
                Nombre = "Vivienda de demostración",
                Codigo = "DEMO-01",
                FechaInicio = new DateOnly(2026, 1, 1)
            });
        }
        var partidas = new[]
        {
            new PartidaLocal
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), ProyectoId = proyectoId,
                Codigo = "EST-01", Descripcion = "Hormigón estructural", Unidad = "m3", PrecioUnitario = 150m, CantidadPrevista = 120m
            },
            new PartidaLocal
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), ProyectoId = proyectoId,
                Codigo = "CER-01", Descripcion = "Cerramiento", Unidad = "m2", PrecioUnitario = 85m, CantidadPrevista = 250m
            }
        };
        foreach (var partida in partidas)
        {
            if (await db.Partidas.AnyAsync(p => p.Id == partida.Id, cancellationToken)) continue;
            if (await db.Partidas.AnyAsync(p => p.ProyectoId == proyectoId && p.Codigo == partida.Codigo, cancellationToken))
            {
                logger.LogWarning("La partida {Codigo} ya existe con otro identificador. Se conserva sin sustituirla.", partida.Codigo);
                continue;
            }
            db.Partidas.Add(partida);
        }
        var cambios = await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Catálogo de demostración comprobado: {InsertedCount} registros nuevos.", cambios);
    }

    private static void MostrarAyuda() => Console.WriteLine("""
        Constructora.Prueba

        Sin argumentos: prepara el catálogo de demostración y abre Constructora en Windows.
        Los datos existentes se conservan. No se crea ninguna certificación automáticamente.

        Opciones:
          --prepare-only             Prepara SQLite sin iniciar la ventana; funciona también en Linux.
          --data-directory DIRECTORIO Usa otra carpeta de datos; requiere --prepare-only.
          --help, -h                 Muestra esta ayuda.

        Carpeta habitual: %LOCALAPPDATA%\Constructora\Certificaciones
        """);

    private sealed record Opciones(bool SoloPreparar, string? DirectorioDatos, bool Ayuda)
    {
        public static Opciones Parse(string[] args)
        {
            var preparar = false;
            var ayuda = false;
            string? directorio = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--prepare-only" when !preparar:
                        preparar = true;
                        break;
                    case "--help" or "-h" when !ayuda:
                        ayuda = true;
                        break;
                    case "--data-directory" when directorio is null:
                        if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException("--data-directory necesita una carpeta. Consulta --help.");
                        directorio = Path.GetFullPath(args[i]);
                        break;
                    default:
                        throw new ArgumentException($"Opción desconocida o repetida: {args[i]}. Consulta --help.");
                }
            }
            if (!ayuda && directorio is not null && !preparar)
                throw new ArgumentException("--data-directory requiere --prepare-only; la aplicación usa su carpeta habitual de Windows.");
            return new Opciones(preparar, directorio, ayuda);
        }
    }
}
