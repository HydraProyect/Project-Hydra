using CaeManager.Application.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerIntencionConversacion;

public record IntencionConversacionDto(IntencionCae IntencionActual, IntencionCae IntencionPropuesta,
    ConfianzaIntencion Confianza, bool ConfirmadaPorPersona, TipoEntidadIntencion? TipoEntidadPropuesta, Guid? EntidadPropuestaId)
{
    public bool CambiadaPorPersona => ConfirmadaPorPersona && IntencionActual != IntencionPropuesta;

    public static IntencionConversacionDto Desde(IntencionConversacion intencion, bool entidadVisible) =>
        new(intencion.IntencionActual, intencion.IntencionPropuesta, intencion.Confianza, intencion.ConfirmadaPorPersona,
            entidadVisible ? intencion.TipoEntidadPropuesta : null, entidadVisible ? intencion.EntidadPropuestaId : null);
}

public record ObtenerIntencionConversacionQuery(Guid ConversacionId) : IRequest<IntencionConversacionDto?>;

public class ObtenerIntencionConversacionQueryHandler(
    IConversacionRepository conversaciones, IIntencionConversacionRepository intenciones, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerIntencionConversacionQuery, IntencionConversacionDto?>
{
    public async Task<IntencionConversacionDto?> Handle(ObtenerIntencionConversacionQuery request, CancellationToken cancellationToken)
    {
        var conversacion = await conversaciones.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // ClienteId es deuda terminológica del ancla a la Empresa en posición de Cliente empresarial.
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion.ClienteId,
            conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken)) return null;
        var intencion = await intenciones.ObtenerPorConversacionIdAsync(request.ConversacionId, cancellationToken);
        if (intencion is null) return null;
        var entidadVisible = intencion.EntidadPropuestaId is { } entidadId && intencion.TipoEntidadPropuesta switch
        {
            TipoEntidadIntencion.Trabajador => await alcanceDatos.TrabajadorVisibleAsync(entidadId, cancellationToken),
            TipoEntidadIntencion.Empresa => await alcanceDatos.EmpresaParaGestionVisibleAsync(entidadId, cancellationToken),
            TipoEntidadIntencion.Centro => await alcanceDatos.CentroParaGestionVisibleAsync(entidadId, cancellationToken),
            TipoEntidadIntencion.TipoDocumento => true,
            _ => false
        };
        return IntencionConversacionDto.Desde(intencion, entidadVisible);
    }
}
