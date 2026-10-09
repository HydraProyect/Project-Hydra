using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerOperadoresCaeDeMiTenant;

/// <summary>
/// Quién gestiona el Tenant propietario de quien pregunta: por cada Asignación de Operación
/// externa viva sobre él, el Operador CAE externo (organización), su Gestor CAE principal
/// (persona) —o nadie, que es un estado válido y se devuelve como tal— y las personas con
/// cartera de apoyo (ADR-011 § 2.7, enmienda 2026-10-08; decisión 2026-10-09).
///
/// <para>
/// Es una lectura del plano de Operación hecha desde el plano de Propiedad. Solo la recibe el
/// <b>Administrador del Tenant propietario</b>: una cuenta activa propia de ese Tenant, con ese
/// rol en Identity, que lo tiene como Tenant activo y como rol efectivo. Vacía para cualquier
/// otro: los demás roles del Tenant propietario (no decidido), una cuenta de un Operador CAE
/// externo —aunque gestione este Tenant con un Encargo de administración; su lectura es
/// <see cref="ObtenerPersonasConCarteraQuery"/>— y Soporte TALVEG. Sin parámetros: el Tenant
/// sale de la sesión, nunca de la petición.
/// </para>
///
/// <para>
/// No concede nada sobre la cartera: designar principal, revocar o dar apoyo siguen siendo del
/// Gestor CAE principal y del Coordinador CAE del Operador CAE. De cada persona de la otra
/// organización salen el nombre y el avatar, que el Administrador ya ve en <c>/usuarios</c>.
/// Vacía también en un Tenant de operación interna: no hay Operador CAE externo que nombrar.
/// </para>
/// </summary>
public record ObtenerOperadoresCaeDeMiTenantQuery : IRequest<IReadOnlyList<CarterasDeOperacion>>;

public class ObtenerOperadoresCaeDeMiTenantQueryHandler(
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo)
    : IRequestHandler<ObtenerOperadoresCaeDeMiTenantQuery, IReadOnlyList<CarterasDeOperacion>>
{
    private const string RolQueLee = "Administrador";

    public async Task<IReadOnlyList<CarterasDeOperacion>> Handle(
        ObtenerOperadoresCaeDeMiTenantQuery request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return [];

        // Plano de Propiedad: el Tenant activo es el de la propia cuenta. Quien opera este Tenant
        // desde otra organización no entra por aquí, tenga el rol efectivo que tenga.
        if (tenantActual.TenantId != origen)
            return [];

        // Falla cerrado sin rol de negocio en la sesión (Soporte TALVEG) y con una sesión
        // restringida a menos que Administrador.
        if (await currentUserService.ObtenerRolEfectivoAsync() != RolQueLee)
            return [];

        if (!await directorioUsuarios.EsCuentaActivaConRolAsync(actorId.Value, origen.Value, RolQueLee, cancellationToken))
            return [];

        var operaciones = await catalogo.ObtenerOperacionesExternasSobreTenantAsync(origen.Value, cancellationToken);
        if (operaciones.Count == 0)
            return [];

        // Solo se piden las personas que el catálogo devolvió con cartera viva sobre este Tenant,
        // y el directorio las vuelve a acotar al Tenant activo.
        var usuarioIds = operaciones.SelectMany(o => o.Carteras).Select(c => c.UsuarioId).Distinct().ToList();
        var nombres = await directorioUsuarios.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);
        var avatares = await directorioUsuarios.ObtenerAvataresVisiblesAsync(usuarioIds, cancellationToken);

        PersonaConCartera Persona(CarteraVivaDeOperacion c) =>
            new(c.UsuarioId, nombres.GetValueOrDefault(c.UsuarioId) ?? string.Empty, c.Rol, c.VigenciaHasta,
                avatares.GetValueOrDefault(c.UsuarioId));

        return operaciones
            .Select(o => new CarterasDeOperacion(
                o.AsignacionOperacionId, origen.Value, string.Empty,
                o.Carteras.Where(c => c.EsPrincipal).Select(Persona).FirstOrDefault(),
                o.Carteras.Where(c => !c.EsPrincipal).Select(Persona).OrderBy(p => p.Nombre).ToList(),
                o.NombreOperador))
            .OrderBy(o => o.NombreOperador)
            .ThenBy(o => o.AsignacionOperacionId)
            .ToList();
    }
}
