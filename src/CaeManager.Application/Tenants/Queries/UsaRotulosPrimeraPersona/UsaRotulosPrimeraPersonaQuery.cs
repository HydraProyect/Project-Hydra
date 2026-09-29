using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;

/// <summary>
/// Si la interfaz habla en primera persona («Mi empresa», «Mis trabajadores») — DDL-072, con la
/// decisión del propietario del 2026-09-28. Fuente única del rótulo para el menú lateral y para
/// los títulos de página: no pueden divergir porque preguntan lo mismo.
///
/// <para>
/// Solo es verdadero si se cumplen las dos condiciones: el perfil de vocabulario del Tenant activo
/// es <see cref="PerfilVocabularioTenant.ClienteDirecto"/> <b>y</b> el usuario pertenece a ese
/// Tenant propietario de los datos (su Tenant de origen es el Tenant activo). Quien trabaja en un
/// Tenant beneficiario ajeno —Operador CAE externo por Asignación de Cartera u Operación,
/// delegación heredada, o Actor de Plataforma TALVEG en Sesión Privilegiada— ve «Empresas» y
/// «Trabajadores»: la lista es de Empresas contraparte de otro Tenant, no de su organización.
/// Sin Tenant de origen o sin Tenant activo, falla al rótulo neutro.
/// </para>
///
/// Capa de presentación pura: no autoriza nada ni cambia ninguna consulta de dominio.
/// </summary>
public record UsaRotulosPrimeraPersonaQuery : IRequest<bool>;

public class UsaRotulosPrimeraPersonaQueryHandler(
    ISender mediator, ITenantActual tenantActual, ICurrentUserService currentUser)
    : IRequestHandler<UsaRotulosPrimeraPersonaQuery, bool>
{
    public async Task<bool> Handle(UsaRotulosPrimeraPersonaQuery request, CancellationToken cancellationToken)
    {
        var perfil = await mediator.Send(new ObtenerPerfilVocabularioActualQuery(), cancellationToken);
        if (perfil != PerfilVocabularioTenant.ClienteDirecto) return false;

        var origen = await currentUser.ObtenerTenantOrigenIdAsync();
        return origen is { } o && tenantActual.TenantId is { } activo && o == activo;
    }
}
