using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.TieneAutorizacionLecturaConcedida;
using CaeManager.Web.Components.Layout;
using MediatR;

namespace CaeManager.Web.Features.Inbound;

/// <summary>
/// Implementación de <see cref="IEsTenantTecInbound"/> (puerto del Núcleo): pregunta a
/// <see cref="TieneAutorizacionLecturaConcedidaQuery"/> si el Tenant activo, como Tenant propietario, tiene alguna autorización de
/// lectura documental concedida (decisión D6 de la demo de Inbound). Pasa por MediatR, así que el despacho comparte la puerta de
/// acceso a datos del ámbito y los behaviors de siempre (Encargo de administración incluido).
///
/// <para>
/// Falla cerrado: si la Query falla o la deniega cualquier behavior, responde <c>false</c> y el grupo no aparece. Una cancelación
/// sí se propaga: no es una respuesta. Es presentación, no autoridad.
/// </para>
/// </summary>
public sealed class EsTenantTecPorAutorizacionConcedida(IMediator mediador) : IEsTenantTecInbound
{
    public async Task<bool> EsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await mediador.Send(new TieneAutorizacionLecturaConcedidaQuery(), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
