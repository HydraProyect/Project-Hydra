using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.BuscarClientePorCif;

/// <summary>
/// Busca un Cliente por CIF exacto — usada al crear un usuario con rol
/// Cliente (ver Usuarios.razor), para vincularlo por CIF a la razón social
/// que ya existe en la base general en vez de teclear un Id a mano. Sin
/// alcance de datos: solo Administrador llega a esta pantalla y ya tiene
/// visibilidad total.
/// </summary>
public record BuscarClientePorCifQuery(string Cif) : IRequest<ClientePorCifDto?>;

public record ClientePorCifDto(Guid Id, string RazonSocial, string Cif);

public class BuscarClientePorCifQueryHandler(IClientesQueryContext dbContext)
    : IRequestHandler<BuscarClientePorCifQuery, ClientePorCifDto?>
{
    public async Task<ClientePorCifDto?> Handle(BuscarClientePorCifQuery request, CancellationToken cancellationToken)
    {
        var cifNormalizado = request.Cif.Trim().ToUpperInvariant();

        return await dbContext.Clientes
            .Where(c => c.Cif == cifNormalizado)
            .Select(c => new ClientePorCifDto(c.Id, c.RazonSocial, c.Cif))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
