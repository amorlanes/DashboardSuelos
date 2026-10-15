using Constructora.Application;
using Constructora.Infrastructure.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Constructora.Server.Controllers;

[ApiController]
[Route("api/proyectos")]
public sealed class ProyectosController(ServidorDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ProyectoDto>>> ObtenerProyectos(CancellationToken cancellationToken) =>
        await db.Proyectos.AsNoTracking().OrderBy(p => p.Codigo)
            .Select(p => new ProyectoDto(p.Id, p.Nombre, p.Codigo, p.FechaInicio)).ToListAsync(cancellationToken);

    [HttpGet("{id:guid}/partidas")]
    public async Task<ActionResult<List<PartidaDto>>> ObtenerPartidas(Guid id, CancellationToken cancellationToken)
    {
        if (!await db.Proyectos.AnyAsync(p => p.Id == id, cancellationToken))
            return NotFound(new ProblemDetails { Title = "El proyecto no existe.", Status = 404 });
        return await db.Partidas.AsNoTracking().Where(p => p.ProyectoId == id).OrderBy(p => p.Codigo)
            .Select(p => new PartidaDto(p.Id, p.ProyectoId, p.Codigo, p.Descripcion, p.Unidad,
                p.PrecioUnitario, p.CantidadPrevista)).ToListAsync(cancellationToken);
    }
}
