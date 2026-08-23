using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;

/// <summary>
/// Expone al Web si el usuario actual tiene una concesión <b>global</b> de
/// <c>AdminPlataforma</c> vigente — mismo criterio de autorización que
/// <c>CrearClienteDeleganteCommand</c>, que es lo que decide si
/// Delegaciones.razor muestra el botón "Nueva delegación"
/// (v1 de ADR-004 § 12.2: solo administrador de plataforma).
///
/// <para>
/// Hasta A3 el criterio era pertenecer al tenant marcado como plataforma, y
/// <c>AbrirAccesoSoporteCommand</c> lo compartía. Ya no: la autoridad salió
/// del rasgo del tenant y pasó a la capacidad concedida, pero solo para las
/// operaciones que A3 migró. El acceso de soporte sigue resolviéndose con
/// <c>Tenant.EsPlataforma</c> hasta el bloque que lo traslade, así que
/// <b>ya no hay paridad con él</b> y no debe citarse aquí como equivalente.
/// </para>
/// </summary>
public record EsAdministradorPlataformaQuery : IRequest<bool>;

public class EsAdministradorPlataformaQueryHandler(IAutorizacionAdminPlataforma autorizacion, ICurrentUserService currentUserService)
    : IRequestHandler<EsAdministradorPlataformaQuery, bool>
{
    public async Task<bool> Handle(EsAdministradorPlataformaQuery request, CancellationToken cancellationToken)
    {
        // EXACTAMENTE el mismo predicado que CrearClienteDeleganteCommand. Si
        // divergieran, el botón diría una cosa y el comando otra: o aparece para
        // quien no puede usarlo, o se esconde a quien sí.
        //
        // Es UX, no enforcement. La seguridad vive en el comando; que alguien
        // vea la ruta y el comando la rechace no es una vulnerabilidad.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return false;

        return await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken);
    }
}
