using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerIntencionConversacion;

public record ObtenerIntencionConversacionQuery(Guid ConversacionId) : IRequest<IntencionConversacion?>;

public class ObtenerIntencionConversacionQueryHandler(
    IConversacionRepository conversaciones, IIntencionConversacionRepository intenciones, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerIntencionConversacionQuery, IntencionConversacion?>
{
    public async Task<IntencionConversacion?> Handle(ObtenerIntencionConversacionQuery request, CancellationToken cancellationToken)
    {
        var conversacion = await conversaciones.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId,
            conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken)) return null;
        return await intenciones.ObtenerPorConversacionIdAsync(request.ConversacionId, cancellationToken);
    }
}
