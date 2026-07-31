using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;

/// <summary>
/// ClienteId null devuelve solo las macros genéricas del tenant (ClienteId
/// null en la entidad); ClienteId con valor devuelve las genéricas MÁS las
/// específicas de ese Cliente — es lo que necesita el selector de macro al
/// responder una conversación (§ diseño aprobado): quien responde quiere ver
/// las plantillas generales y las propias de ese cliente juntas, no elegir
/// entre una u otra. La misma query alimenta el filtro de Macros.razor, así
/// que "sin filtro" ahí muestra las genéricas — comportamiento consistente
/// entre las dos pantallas que la usan.
/// </summary>
public record ObtenerMacrosQuery(Guid? ClienteId = null) : IRequest<IReadOnlyList<MacroListaDto>>;

public record MacroListaDto(Guid Id, Guid? ClienteId, string? ClienteRazonSocial, string Titulo, string CuerpoHtml);

public class ObtenerMacrosQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerMacrosQuery, IReadOnlyList<MacroListaDto>>
{
    public async Task<IReadOnlyList<MacroListaDto>> Handle(ObtenerMacrosQuery request, CancellationToken cancellationToken)
    {
        // Un ClienteId fuera de la cartera se trata como si no se hubiera
        // pedido ninguno: se devuelven solo las macros genéricas, en vez de
        // las plantillas de un cliente ajeno (hallazgo N-3). No es un error
        // para no confirmar que ese cliente existe.
        var clienteId = await alcanceDatos.ClienteOpcionalVisibleAsync(request.ClienteId, cancellationToken)
            ? request.ClienteId
            : null;

        var consulta = clienteId is null
            ? dbContext.MacrosRespuesta.Where(m => m.ClienteId == null)
            : dbContext.MacrosRespuesta.Where(m => m.ClienteId == null || m.ClienteId == clienteId);

        return await (
            from macro in consulta
            join cliente in dbContext.Clientes on macro.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            orderby macro.Titulo
            select new MacroListaDto(macro.Id, macro.ClienteId, cliente != null ? cliente.RazonSocial : null, macro.Titulo, macro.CuerpoHtml))
            .ToListAsync(cancellationToken);
    }
}
