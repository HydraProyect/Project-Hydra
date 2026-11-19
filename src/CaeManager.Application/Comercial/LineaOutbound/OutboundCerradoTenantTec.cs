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
/// (F-4, DN-6): vale igual con Sesión Privilegiada y para un Operador CAE externo que ya existía. Donde el Tenant TEC
/// sería el Operador CAE de otro Tenant (una delegación, una Asignación de Operación, un Encargo de administración o una
/// Asignación de Cartera en una operación externa), se pregunta también por el Tenant de origen de ese Operador CAE (EP-4:
/// «sin gestión de la CAE de terceros, ni Operador CAE»): ver <see cref="LimiteTenantTecDelOperadorCae"/>.
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

/// <summary>
/// D3 sobre el Operador CAE (EP-4, ronda 3 del revisor-puente): sin línea Outbound vigente, el Tenant TEC participante no
/// es Operador CAE de otro Tenant. Una Asignación de Operación, una delegación, un Encargo de administración o una
/// Asignación de Cartera de una operación externa quedan cerrados si lo está su Tenant beneficiario o el Tenant de origen
/// de su Operador CAE. Un Tenant sin marcador no lee la línea, así que el caso normal no cambia.
///
/// <para>
/// shortcut: la línea Outbound del Operador CAE se lee bajo la RLS de <c>ProductosContratados</c> del Tenant activo. Cuando
/// el Operador CAE no es el Tenant activo (lo nombra el Tenant propietario que delega, o el Actor de Plataforma TALVEG), un
/// Tenant TEC marcado con línea Vigente se lee como «sin línea» y se cierra en falso (fallo cerrado aceptado, como en la vía
/// de la delegación ajena). Hoy no tiene efecto: no hay escritor de la línea P en producción (F-1). Se resolverá con el
/// contrato de lectura entre Tenants de B-08.
/// </para>
/// </summary>
public static class LimiteTenantTecDelOperadorCae
{
    /// <summary>Si está cerrado para <paramref name="tenantBeneficiarioId"/> o para <paramref name="operadorCaeTenantId"/>.</summary>
    public static async Task<bool> EstaCerradoConOperadorCaeAsync(
        this IOutboundCerradoTenantTec limite, Guid tenantBeneficiarioId, Guid operadorCaeTenantId,
        CancellationToken cancellationToken = default) =>
        await limite.EstaCerradoAsync(tenantBeneficiarioId, cancellationToken)
        || (operadorCaeTenantId != tenantBeneficiarioId
            && await limite.EstaCerradoAsync(operadorCaeTenantId, cancellationToken));
}

/// <summary>
/// El error de D3, el mismo en todos los puntos que cierra. El texto no atribuye la condición a la organización del actor
/// ni dice cuál de las implicadas está cerrada: quien lo recibe puede ser el Administrador del Tenant propietario, el Actor
/// de Plataforma TALVEG o Soporte TALVEG en Sesión Privilegiada, y nombrarla revelaría el estado comercial de otro Tenant.
/// </summary>
public static class ErroresLimiteTenantTec
{
    public static readonly Error OutboundCerrado = Error.Crear(
        "Autorizacion.CapacidadNoIncluidaEnTenantTec",
        "Esta operación no está disponible: una de las organizaciones implicadas no tiene contratada la línea Outbound.");
}
