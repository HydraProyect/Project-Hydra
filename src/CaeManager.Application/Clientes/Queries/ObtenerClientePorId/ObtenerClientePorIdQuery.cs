using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientePorId;

public record ObtenerClientePorIdQuery(Guid Id) : IRequest<ClienteDetalleDto?>;

/// <summary>
/// <paramref name="Version"/> viaja al formulario para volver en el Command
/// de edición: es lo que permite detectar que otra persona guardó mientras
/// tanto. Sin ella, el handler recarga la fila justo antes de escribir y el
/// token de concurrencia compara el valor consigo mismo — la columna existe,
/// pero nunca choca (ver EditarClienteCommandHandler).
/// </summary>
public record ClienteDetalleDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, string? Notas, DateTime CreadoEnUtc,
    Guid? EjecutivoUsuarioId, Guid Version);

public class ObtenerClientePorIdQueryHandler(IClientesQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientePorIdQuery, ClienteDetalleDto?>
{
    public async Task<ClienteDetalleDto?> Handle(ObtenerClientePorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.Id, cancellationToken)) return null;

        return await dbContext.Clientes
            .Where(c => c.Id == request.Id)
            .Select(c => new ClienteDetalleDto(
                c.Id, c.RazonSocial, c.Cif, c.EsCritico, c.Notas, c.CreadoEnUtc, c.EjecutivoUsuarioId, c.Version))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
