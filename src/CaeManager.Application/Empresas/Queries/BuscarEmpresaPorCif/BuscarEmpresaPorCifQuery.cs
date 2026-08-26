using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.BuscarEmpresaPorCif;

/// <summary>
/// Busca una Empresa por CIF exacto — usada al crear/editar un usuario con
/// rol Cliente (ver Usuarios.razor), para vincularlo por CIF a la Empresa
/// contraparte que ya existe en vez de teclear un Id a mano. Sustituye a
/// BuscarClientePorCifQuery (retirada F4.2a): un usuario Cliente se vincula
/// hoy a una Empresa (ver ApplicationUser.ClienteId), no a la tabla legacy
/// Clientes, que ya no recibe altas desde F3b. Sin alcance de datos: solo
/// Administrador llega a esta pantalla y ya tiene visibilidad total.
/// </summary>
public record BuscarEmpresaPorCifQuery(string Cif) : IRequest<EmpresaPorCifDto?>;

public record EmpresaPorCifDto(Guid Id, string RazonSocial, string Cif);

public class BuscarEmpresaPorCifQueryHandler(IEmpresasQueryContext dbContext)
    : IRequestHandler<BuscarEmpresaPorCifQuery, EmpresaPorCifDto?>
{
    public async Task<EmpresaPorCifDto?> Handle(BuscarEmpresaPorCifQuery request, CancellationToken cancellationToken)
    {
        var cifNormalizado = request.Cif.Trim().ToUpperInvariant();

        return await dbContext.Empresas
            .Where(e => e.Cif == cifNormalizado)
            .Select(e => new EmpresaPorCifDto(e.Id, e.RazonSocial, e.Cif!))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
