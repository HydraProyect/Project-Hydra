using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;

/// <summary>
/// Expone al Web si el tenant de <b>origen</b> del usuario actual es el
/// tenant marcado como plataforma — mismo criterio de autorización que
/// <c>CrearClienteDeleganteCommand</c> y <c>AbrirAccesoSoporteCommand</c>,
/// aquí solo para decidir si Delegaciones.razor muestra el botón
/// "Nueva delegación" (v1 de ADR-004 § 12.2: solo administrador de plataforma).
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
