using CaeManager.Application.Clientes;
using CaeManager.Application.Common;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Escalado del principal cuando no hay a quién relevar (ADR-011 § 2.7, enmienda 2026-10-08,
/// punto 4): una Asignación de Operación externa sin ninguna cartera, o un principal que se va
/// o es desactivado sin Coordinador CAE. Se sube de perfil dentro del Operador CAE
/// —Coordinador CAE → Dirección CAE → Administrador— hasta el primer nivel con alguna cuenta
/// activa. Con <b>una sola</b> en ese nivel, recibe la cartera principal en la transacción del
/// hecho que lo dispara; con varias no se asigna a nadie: la operación queda en la alerta
/// (<c>ObtenerOperacionesSinPrincipalQuery</c>) y cualquiera de los tres perfiles la toma con
/// <c>AsumirPrincipalDeOperacionCommand</c>.
///
/// <para>
/// La cartera la emite <see cref="ICatalogoIncorporacionCartera.RelevarPrincipalAsync"/>, la
/// misma escritura que el relevo: Tenant entero y <b>rol Coordinador CAE, que no es
/// parámetro</b>, sea cual sea el perfil de quien la recibe. La Operación nunca concede roles
/// de Propiedad; el rol efectivo elevado por un Encargo de administración se calcula aparte.
/// </para>
///
/// <para>
/// El número de cuentas de cada perfil se lee en Identity sobre el Tenant del Operador CAE,
/// nunca en el claim, y antes de decidir se toma el candado compartido de cartera sobre la
/// elegida (<see cref="IBloqueoCarteraUsuario"/>): una desactivación en curso de esa cuenta
/// termina antes, y una posterior espera y encuentra ya la marca para cederla.
/// </para>
/// </summary>
public static class EscaladoDePrincipalDeCartera
{
    public const string CoordinadorCae = "CoordinadorCae";
    public const string DireccionCae = "DireccionCae";
    public const string Administrador = "Administrador";

    /// <summary>Los perfiles del Operador CAE que pueden recibir o asumir un principal, en orden de escalado.</summary>
    public static readonly IReadOnlyList<string> Niveles = [CoordinadorCae, DireccionCae, Administrador];

    /// <summary>
    /// La única cuenta activa del primer nivel que tiene alguna; <c>null</c> si no hay nadie en
    /// ningún nivel o si ese nivel tiene varias. <paramref name="excluidoUsuarioId"/> es quien
    /// deja la operación sin principal: no se le devuelve la marca que acaba de soltar.
    /// </summary>
    public static async Task<Guid?> ResolverUnicoAsync(
        Guid operadorTenantId,
        Guid? excluidoUsuarioId,
        IDirectorioUsuariosService directorioUsuarios,
        IBloqueoCarteraUsuario bloqueoCartera,
        CancellationToken cancellationToken)
    {
        using (AmbitoTenantExplicito.Establecer(operadorTenantId))
        {
            foreach (var nivel in Niveles)
            {
                var activas = (await directorioUsuarios.ObtenerCuentasActivasConRolAsync(operadorTenantId, nivel, cancellationToken))
                    .Where(id => id != excluidoUsuarioId)
                    .ToList();
                if (activas.Count == 0)
                    continue;
                if (activas.Count > 1)
                    return null;

                var unica = activas[0];
                await bloqueoCartera.BloquearCompartidoAsync([unica], cancellationToken);

                return await directorioUsuarios.EsCuentaActivaConRolAsync(unica, operadorTenantId, nivel, cancellationToken)
                    ? unica
                    : null;
            }

            return null;
        }
    }

    /// <summary>
    /// Si <paramref name="usuarioId"/> es hoy la <b>única</b> cuenta activa del Operador CAE
    /// con alguno de los tres perfiles: el «primer usuario elegible» que recibe solo las
    /// operaciones que nacieron sin nadie a quien asignarlas. Un Gestor CAE no lo es.
    /// </summary>
    public static async Task<bool> EsElUnicoElegibleAsync(
        Guid usuarioId,
        Guid operadorTenantId,
        IDirectorioUsuariosService directorioUsuarios,
        IBloqueoCarteraUsuario bloqueoCartera,
        CancellationToken cancellationToken)
    {
        using (AmbitoTenantExplicito.Establecer(operadorTenantId))
        {
            await bloqueoCartera.BloquearCompartidoAsync([usuarioId], cancellationToken);

            var elegibles = new HashSet<Guid>();
            foreach (var nivel in Niveles)
                elegibles.UnionWith(await directorioUsuarios.ObtenerCuentasActivasConRolAsync(operadorTenantId, nivel, cancellationToken));

            return elegibles.Count == 1 && elegibles.Contains(usuarioId);
        }
    }

    /// <summary>
    /// Para una operación que acaba de quedarse sin principal y sin relevo, o que nace sin
    /// carteras: la asigna a la única cuenta del primer nivel con alguien, si la hay. Se llama
    /// con el hecho que lo dispara ya guardado y dentro de su transacción. Devuelve
    /// <c>false</c> si el guardado perdió una carrera: el comando debe fallar entero.
    /// </summary>
    public static async Task<bool> AsignarAlUnicoAsync(
        ICatalogoIncorporacionCartera catalogo,
        IDirectorioUsuariosService directorioUsuarios,
        IBloqueoCarteraUsuario bloqueoCartera,
        IReadOnlyCollection<OperacionConPrincipal> operacionesSinPrincipal,
        Guid operadorTenantId,
        Guid? excluidoUsuarioId,
        CancellationToken cancellationToken)
    {
        if (operacionesSinPrincipal.Count == 0)
            return true;

        var unico = await ResolverUnicoAsync(
            operadorTenantId, excluidoUsuarioId, directorioUsuarios, bloqueoCartera, cancellationToken);

        return await RelevoDePrincipalDeCartera.RelevarAsync(
            catalogo, operacionesSinPrincipal, operadorTenantId, unico, cancellationToken);
    }

    /// <summary>
    /// Para una cuenta que acaba de darse de alta, cambiar de rol o reactivarse: si es la única
    /// elegible del Operador CAE, recibe la cartera principal de cada operación suya que siga
    /// sin principal vivo (las que tienen principal no se tocan: lo comprueba la escritura).
    /// Mismas condiciones y mismo significado del <c>false</c> que <see cref="AsignarAlUnicoAsync"/>.
    /// </summary>
    public static async Task<bool> AsignarAlPrimerElegibleAsync(
        ICatalogoIncorporacionCartera catalogo,
        IDirectorioUsuariosService directorioUsuarios,
        IBloqueoCarteraUsuario bloqueoCartera,
        Guid usuarioId,
        Guid operadorTenantId,
        CancellationToken cancellationToken)
    {
        var operaciones = (await catalogo.ObtenerAsignablesAsync(operadorTenantId, cancellationToken))
            .Select(o => new OperacionConPrincipal(o.PropietarioTenantId, o.AsignacionOperacionId))
            .ToList();
        if (operaciones.Count == 0)
            return true;

        if (!await EsElUnicoElegibleAsync(usuarioId, operadorTenantId, directorioUsuarios, bloqueoCartera, cancellationToken))
            return true;

        return await RelevoDePrincipalDeCartera.RelevarAsync(
            catalogo, operaciones, operadorTenantId, usuarioId, cancellationToken);
    }
}
