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
///
/// <para>
/// Por defecto la respuesta sale de <see cref="MemoAdministradorPlataforma"/> (hasta
/// <see cref="MemoPorContexto.Caducidad"/> de antigüedad dentro del ámbito): basta para mostrar u ocultar
/// controles, porque cada operación de plataforma vuelve a comprobar la concesión. Quien decide con ella una
/// lectura que no autoriza por sí misma pide <paramref name="LecturaVigente"/>: lee la concesión vigente y no
/// toca el memo.
/// </para>
/// </summary>
/// <param name="LecturaVigente">Leer la concesión de la base aunque el ámbito tenga la respuesta memoizada.</param>
public record EsAdministradorPlataformaQuery(bool LecturaVigente = false) : IRequest<bool>;

public class EsAdministradorPlataformaQueryHandler(IAutorizacionAdminPlataforma autorizacion, ICurrentUserService currentUserService,
    ITenantActual? tenantActual = null, MemoAdministradorPlataforma? memo = null)
    : IRequestHandler<EsAdministradorPlataformaQuery, bool>
{
    public async Task<bool> Handle(EsAdministradorPlataformaQuery request, CancellationToken cancellationToken)
    {
        if (memo is not null && tenantActual is not null && !request.LecturaVigente
            && await ClaveContextoMemo.ResolverAsync(currentUserService, tenantActual) is { } clave)
            return await memo.ObtenerOCalcularAsync(clave, CalcularAsync, cancellationToken);

        return await CalcularAsync(cancellationToken);
    }

    private async Task<bool> CalcularAsync(CancellationToken cancellationToken)
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

/// <summary>
/// Señal de interfaz Scoped, con la caducidad e invalidación de las lecturas del layout.
/// No sustituye la comprobación vigente de capacidad que realiza cada operación de plataforma.
/// La concesión global no depende de la Sesión Privilegiada (IAutorizacionAdminPlataforma).
/// </summary>
public sealed class MemoAdministradorPlataforma(TimeProvider? reloj = null) : MemoPorContexto<bool>(reloj);
