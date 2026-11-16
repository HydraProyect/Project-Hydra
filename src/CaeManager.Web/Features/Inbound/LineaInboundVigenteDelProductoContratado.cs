using CaeManager.Application.Inbound.Comercial.Queries.LineaInboundVigente;
using CaeManager.Web.Components.Layout;
using MediatR;

namespace CaeManager.Web.Features.Inbound;

/// <summary>
/// Implementación de <see cref="ILineaInboundVigente"/> (puerto del Núcleo): pregunta a <see cref="LineaInboundVigenteQuery"/>
/// si el Tenant activo, como Tenant beneficiario, tiene un Producto Contratado de la línea Inbound vigente. Pasa por MediatR,
/// así que el despacho comparte la puerta de acceso a datos del ámbito y los behaviors de siempre (Encargo de
/// administración incluido).
///
/// <para>
/// Falla cerrado: si la Query falla o la deniega cualquier behavior, responde <c>false</c> y la sección «Inbound» no
/// aparece. Una cancelación sí se propaga: no es una respuesta. Es presentación, no autoridad: cada Query y cada Command de
/// Inbound sigue autorizándose por su cuenta.
/// </para>
/// </summary>
public sealed class LineaInboundVigenteDelProductoContratado(IMediator mediador) : ILineaInboundVigente
{
    public async Task<bool> EstaVigenteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await mediador.Send(new LineaInboundVigenteQuery(), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
