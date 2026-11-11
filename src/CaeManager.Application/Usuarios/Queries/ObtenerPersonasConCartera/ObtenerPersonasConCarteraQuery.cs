using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;

/// <summary>
/// Una persona del Operador CAE con cartera viva bajo una Asignación de Operación.
/// <paramref name="Rol"/> es el de su cartera (Gestor CAE o Coordinador CAE), que decide el
/// rótulo: «Gestor CAE principal» o «Coordinador CAE principal». <paramref name="VigenciaHasta"/>
/// solo la lleva una cartera de apoyo con fecha de fin. <paramref name="Avatar"/> es la clave del
/// avatar que eligió esa persona (<see cref="CatalogoAvatares"/>), o <c>null</c> si no eligió:
/// quien la pinta muestra entonces sus iniciales.
/// </summary>
public record PersonaConCartera(Guid UsuarioId, string Nombre, string Rol, DateTime? VigenciaHasta, string? Avatar = null);

/// <summary>
/// Quién responde de un Tenant propietario bajo una Asignación de Operación: el principal —o
/// nadie, que es un estado válido— y las personas con cartera de apoyo. Principal y apoyo
/// tienen el mismo ámbito efectivo. <paramref name="NombreOperador"/> es el Operador CAE externo
/// (organización) de esa operación; solo lo lleva la lectura hecha desde el Tenant propietario
/// (<c>ObtenerOperadoresCaeDeMiTenantQuery</c>): a quien pregunta desde el Operador CAE no hace
/// falta decirle cuál es el suyo.
/// </summary>
public record CarterasDeOperacion(
    Guid AsignacionOperacionId, Guid TenantId, string NombreTenant,
    PersonaConCartera? Principal, IReadOnlyList<PersonaConCartera> Apoyos,
    string? NombreOperador = null);

/// <summary>
/// Las personas con cartera viva en los Tenants que opera el Operador CAE de quien pregunta,
/// por Asignación de Operación (ADR-011 § 2.7, enmienda 2026-10-08); con
/// <paramref name="TenantId"/>, solo las de ese Tenant propietario. Acotada al propio Operador
/// CAE: el Tenant de origen de la sesión, nunca un dato de la petición. Vacía si quien pregunta
/// no tiene, como rol efectivo de la sesión en el Tenant de origen, un rol de gestión CAE que
/// Identity confirme sobre la cuenta activa del Operador CAE
/// (<see cref="AutoridadSobreCarteraDeGestorCae.RolDeAutoridadAsync"/>); en particular, vacía para un usuario del propio Tenant propietario, cuya
/// lectura es <c>ObtenerOperadoresCaeDeMiTenantQuery</c>. Es lectura: quién puede cambiar el
/// principal lo decide <c>DesignarGestorCaePrincipalCommand</c>.
/// </summary>
public record ObtenerPersonasConCarteraQuery(Guid? TenantId = null) : IRequest<IReadOnlyList<CarterasDeOperacion>>;

public class ObtenerPersonasConCarteraQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo)
    : IRequestHandler<ObtenerPersonasConCarteraQuery, IReadOnlyList<CarterasDeOperacion>>
{
    private static readonly string[] RolesQueLeen = ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<IReadOnlyList<CarterasDeOperacion>> Handle(
        ObtenerPersonasConCarteraQuery request, CancellationToken cancellationToken)
    {
        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return [];

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            // Rol efectivo de la sesión en el Tenant de origen, confirmado en Identity. Sin rol de
            // negocio falla cerrado: Soporte TALVEG nunca es Operador CAE.
            if (await AutoridadSobreCarteraDeGestorCae.RolDeAutoridadAsync(
                    actorId.Value, origen.Value, RolesQueLeen, currentUserService, directorioUsuarios, cancellationToken) is null)
                return [];

            var carteras = await catalogo.ObtenerCarterasVivasAsync(origen.Value, request.TenantId, cancellationToken);
            if (carteras.Count == 0)
                return [];

            var usuarioIds = carteras.Select(c => c.UsuarioId).Distinct().ToList();
            var nombres = await directorioUsuarios.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);
            var avatares = await directorioUsuarios.ObtenerAvataresVisiblesAsync(usuarioIds, cancellationToken);

            PersonaConCartera Persona(CarteraVivaDeOperacion c) =>
                new(c.UsuarioId, nombres.GetValueOrDefault(c.UsuarioId) ?? string.Empty, c.Rol, c.VigenciaHasta,
                    avatares.GetValueOrDefault(c.UsuarioId));

            return carteras
                .GroupBy(c => c.AsignacionOperacionId)
                .Select(g => new CarterasDeOperacion(
                    g.Key, g.First().PropietarioTenantId, g.First().NombreTenant,
                    g.Where(c => c.EsPrincipal).Select(Persona).FirstOrDefault(),
                    g.Where(c => !c.EsPrincipal).Select(Persona).OrderBy(p => p.Nombre).ToList()))
                .OrderBy(o => o.NombreTenant)
                .ToList();
        }
    }
}
