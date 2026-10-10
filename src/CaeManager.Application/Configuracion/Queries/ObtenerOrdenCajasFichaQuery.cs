using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

/// <summary>
/// El orden de cajas que el usuario actual guardó para ese tipo de ficha 360 en
/// el Tenant actual. Lista vacía = no tiene ninguno y la ficha usa el orden
/// automático. Devuelve las claves tal como se guardaron: quien pinta la ficha
/// las concilia con sus cajas (<c>OrdenCajasFicha.Conciliar</c>).
/// </summary>
public record ObtenerOrdenCajasFichaQuery(string TipoFicha) : IRequest<IReadOnlyList<string>>;

public class ObtenerOrdenCajasFichaQueryHandler(IConfiguracionQueryContext dbContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerOrdenCajasFichaQuery, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> Handle(ObtenerOrdenCajasFichaQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return [];

        var claves = await dbContext.OrdenesCajasFicha
            .Where(o => o.UsuarioId == usuarioId.Value && o.TipoFicha == request.TipoFicha)
            .Select(o => o.Claves)
            .FirstOrDefaultAsync(cancellationToken);

        return claves ?? [];
    }
}
