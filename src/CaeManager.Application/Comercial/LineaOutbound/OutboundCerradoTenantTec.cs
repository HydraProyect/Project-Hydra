using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comercial.LineaOutbound;

/// <summary>
/// ¿Está Outbound cerrado para este Tenant beneficiario? (D3, decisiones de Chris del 2026-11-19; DN-1 a DN-3 del
/// 2026-11-16 y D-9). Lo está si el Tenant lleva el marcador <c>Tenants.EsParticipanteTec</c> y no tiene una línea
/// Outbound de pago vigente: un Producto Contratado de <see cref="Domain.Comercial.LineaProductoContratado.OutboundGestionPropia"/>
/// <see cref="EstadoLineaOutbound.Vigente"/> y con el inicio alcanzado (<see cref="IEstadoLineaOutbound"/>). Programada,
/// Finalizada y sin línea cuentan como «sin línea» (DN-2, F-3).
///
/// <para>
/// Solo restringe: un Tenant sin marcador nunca queda cerrado, tenga o no fila de la línea (F-2: sin backfill, los Tenants
/// que hoy operan Outbound no la tienen). Decide por el Tenant beneficiario, nunca por el actor ni por el Pagador TALVEG
/// (F-4, DN-6): vale igual con Sesión Privilegiada y para un Operador CAE externo que ya existía.
/// </para>
///
/// <para>
/// Falla cerrado. <c>Tenants</c> no tiene RLS, así que el marcador de cualquier Tenant se lee; la línea se lee bajo la
/// política <c>aislamiento_tenant</c> de <c>ProductosContratados</c>, que solo deja ver las filas del Tenant activo:
/// preguntar por un Tenant marcado que no es el activo responde «sin línea», y por tanto cerrado. Un Tenant que no aparece
/// cuenta como cerrado. Una excepción al leer se propaga y la petición falla. No se consulta bajo
/// <c>cae_app_aprovisionamiento</c>, que no ve las filas que no son Inbound (ver <see cref="IEstadoLineaOutbound"/>): su
/// consumidor, <see cref="Common.LimiteTenantTecBehavior{TRequest,TResponse}"/>, corre antes de la elevación.
/// </para>
/// </summary>
public interface IOutboundCerradoTenantTec
{
    Task<bool> EstaCerradoAsync(Guid tenantBeneficiarioId, CancellationToken cancellationToken = default);
}

public sealed class OutboundCerradoTenantTecService(ITenantsQueryContext tenants, IEstadoLineaOutbound estadoLinea)
    : IOutboundCerradoTenantTec
{
    public async Task<bool> EstaCerradoAsync(Guid tenantBeneficiarioId, CancellationToken cancellationToken = default)
    {
        var marcado = await tenants.Tenants
            .Where(t => t.Id == tenantBeneficiarioId)
            .Select(t => (bool?)t.EsParticipanteTec)
            .SingleOrDefaultAsync(cancellationToken);

        return marcado switch
        {
            null => true,
            false => false,
            true => !await estadoLinea.TieneLineaOutboundVigenteAsync(tenantBeneficiarioId, cancellationToken),
        };
    }
}

/// <summary>El error de D3, el mismo en todos los puntos que cierra.</summary>
public static class ErroresLimiteTenantTec
{
    public static readonly Error OutboundCerrado = Error.Crear(
        "Autorizacion.CapacidadNoIncluidaEnTenantTec",
        "Tu organización participa como empresa contratista y no tiene contratada la línea Outbound, así que esta acción " +
        "no está incluida.");
}
