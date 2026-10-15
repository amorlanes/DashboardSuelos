using Constructora.Infrastructure.Server;
using Constructora.Server.Hubs;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<ServidorDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Servidor");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("Configure ConnectionStrings__Servidor con la conexión PostgreSQL del entorno.");
    options.UseNpgsql(connectionString);
});
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();
app.MapControllers();
app.MapHub<CertificacionHub>("/hubs/certificaciones");
app.MapGet("/health", async (ServidorDbContext db, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    try
    {
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { estado = "Disponible" })
            : Results.Problem("PostgreSQL no está disponible.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Fallo al comprobar la conexión PostgreSQL.");
        return Results.Problem("PostgreSQL no está disponible.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

if (app.Configuration.GetValue<bool>("Database:Initialize"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ServidorDbContext>();
    await db.Database.EnsureCreatedAsync();
    app.Logger.LogInformation("Esquema inicial PostgreSQL comprobado. No se han insertado datos de empresa.");
}

await app.RunAsync();

public partial class Program;
