using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;

/// <summary>
/// Perfil de vocabulario (DDL-072) del tenant <b>activo</b> — a propósito
/// <see cref="ITenantActual"/> y no el tenant de origen: en un Delegated
/// Workspace, la interfaz debe hablar el idioma del tenant cuyos datos se
/// están mirando, no el del operador. Capa de presentación pura: nada aquí
/// cambia una consulta de dominio, solo qué etiqueta usa la pantalla.
/// </summary>
public record ObtenerPerfilVocabularioActualQuery : IRequest<PerfilVocabularioTenant>;

/// <remarks>
/// Con <see cref="MemoPerfilVocabularioActual"/> y <see cref="ICurrentUserService"/> (la composición real los
/// registra) el perfil real se lee una vez por ámbito y contexto (<see cref="ClaveContextoMemo"/>) dentro de su
/// caducidad: el layout lo pide varias veces por visita (menú, migas, cabecera). Sin ellos (tests que construyen
/// el handler a mano) se lee en cada llamada, como antes.
/// </remarks>
public class ObtenerPerfilVocabularioActualQueryHandler(
    ITenantsQueryContext tenantsContext, ITenantActual tenantActual, IVistaVocabularioPreviewService vistaPreview,
    ICurrentUserService? currentUser = null, MemoPerfilVocabularioActual? memo = null)
    : IRequestHandler<ObtenerPerfilVocabularioActualQuery, PerfilVocabularioTenant>
{
    public async Task<PerfilVocabularioTenant> Handle(ObtenerPerfilVocabularioActualQuery request, CancellationToken cancellationToken)
    {
        // Vista previa de Administrador (ver IVistaVocabularioPreviewService):
        // gana siempre que esté activa, sin tocar el perfil real del tenant.
        // Va antes del memo: la vista previa no entra en la clave.
        if (vistaPreview.PerfilForzado is { } perfilForzado) return perfilForzado;

        if (tenantActual.TenantId is not { } tenantId) return PerfilVocabularioTenant.ClienteDirecto;

        if (memo is not null && currentUser is not null
            && await ClaveContextoMemo.ResolverAsync(currentUser, tenantActual) is { } clave
            && clave.TenantEfectivo == tenantId)
            return await memo.ObtenerOCalcularAsync(clave, ct => LeerAsync(tenantId, ct), cancellationToken);

        return await LeerAsync(tenantId, cancellationToken);
    }

    private Task<PerfilVocabularioTenant> LeerAsync(Guid tenantId, CancellationToken cancellationToken) =>
        tenantsContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.PerfilVocabulario)
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// Memoización Scoped del perfil de vocabulario real del Tenant activo (<see cref="MemoPorContexto{TValor}"/>). La
/// vista previa de Administrador no se memoiza: se aplica antes, en cada llamada.
/// </summary>
public sealed class MemoPerfilVocabularioActual(TimeProvider? reloj = null)
    : MemoPorContexto<PerfilVocabularioTenant>(reloj);
