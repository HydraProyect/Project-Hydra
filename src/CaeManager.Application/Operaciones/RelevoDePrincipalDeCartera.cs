using CaeManager.Application.Clientes;
using CaeManager.Application.Common;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Relevo automático del principal (ADR-011 § 2.7, enmienda 2026-10-08, punto 3): cuando la
/// cartera principal se cierra por retirada, o su titular es desactivado, la marca pasa en la
/// misma transacción al Coordinador CAE de esa persona. Una sola definición para los comandos
/// que dejan una operación sin principal.
///
/// <para>
/// Si esa persona no tiene Coordinador CAE, o el suyo no es hoy una cuenta activa con rol
/// Coordinador CAE del mismo Operador CAE, se escala (punto 4 de la misma enmienda,
/// <see cref="EscaladoDePrincipalDeCartera"/>): recibe la marca la única cuenta activa del
/// primer perfil del Operador CAE que tenga alguna. Con varias en ese perfil, o sin nadie en
/// ninguno, <b>la operación queda sin principal</b> y la lista la alerta. El cierre en cascada
/// de la operación entera no pasa por aquí.
/// </para>
/// </summary>
public static class RelevoDePrincipalDeCartera
{
    private const string CoordinadorCae = "CoordinadorCae";

    /// <summary>
    /// El Coordinador CAE al que reporta <paramref name="usuarioId"/>, si hoy es una cuenta
    /// activa con ese rol en <paramref name="operadorTenantId"/>; <c>null</c> si no hay a quién
    /// relevar. El rol se lee en Identity sobre el Tenant del Operador CAE, no en el claim.
    ///
    /// <para>
    /// Antes de leer su cuenta toma el candado compartido de cartera sobre él
    /// (<see cref="IBloqueoCarteraUsuario"/>): desactivar una cuenta toma el exclusivo, así que
    /// una desactivación del Coordinador CAE en curso termina antes de que aquí se decida, y
    /// una que llegue después espera a que el relevo confirme y encuentra ya su marca para
    /// cederla. Sin él, las dos transacciones confirmarían y la marca quedaría en una cuenta
    /// desactivada (revisión Codex de I2). Se llama dentro de la transacción del comando.
    /// </para>
    /// </summary>
    public static async Task<Guid?> ResolverCoordinadorAsync(
        Guid usuarioId,
        Guid operadorTenantId,
        IDirectorioDestinosCartera directorioDestinos,
        IDirectorioUsuariosService directorioUsuarios,
        IBloqueoCarteraUsuario bloqueoCartera,
        CancellationToken cancellationToken)
    {
        using (AmbitoTenantExplicito.Establecer(operadorTenantId))
        {
            var cuenta = await directorioDestinos.ObtenerAsync(usuarioId, cancellationToken);
            if (cuenta is null || cuenta.EsOperadorDelegado
                || cuenta.CoordinadorUsuarioId is not { } coordinadorId || coordinadorId == usuarioId)
                return null;

            await bloqueoCartera.BloquearCompartidoAsync([coordinadorId], cancellationToken);

            return await directorioUsuarios.EsCuentaActivaConRolAsync(
                coordinadorId, operadorTenantId, CoordinadorCae, cancellationToken)
                ? coordinadorId
                : null;
        }
    }

    /// <summary>
    /// A quién pasa la marca que suelta <paramref name="usuarioId"/>: su Coordinador CAE
    /// (<see cref="ResolverCoordinadorAsync"/>) y, si no hay a quién relevar, la única cuenta
    /// del primer perfil con alguien (<see cref="EscaladoDePrincipalDeCartera.ResolverUnicoAsync"/>),
    /// sin contarle a él. <c>null</c> si la operación tiene que quedar sin principal.
    /// </summary>
    public static async Task<Guid?> ResolverRelevoAsync(
        Guid usuarioId,
        Guid operadorTenantId,
        IDirectorioDestinosCartera directorioDestinos,
        IDirectorioUsuariosService directorioUsuarios,
        IBloqueoCarteraUsuario bloqueoCartera,
        CancellationToken cancellationToken) =>
        await ResolverCoordinadorAsync(
            usuarioId, operadorTenantId, directorioDestinos, directorioUsuarios, bloqueoCartera, cancellationToken)
        ?? await EscaladoDePrincipalDeCartera.ResolverUnicoAsync(
            operadorTenantId, usuarioId, directorioUsuarios, bloqueoCartera, cancellationToken);

    /// <summary>
    /// Pasa la marca a quien se releva en cada operación que se quedó sin principal, con el
    /// Tenant propietario de cada una como Tenant activo, y guarda. Se llama con el cierre o
    /// el apagado ya guardados (el índice único de principal no es diferible) y dentro de la
    /// transacción del comando. Devuelve <c>false</c> si un guardado perdió una carrera: el
    /// comando debe fallar para que la transacción se deshaga entera.
    /// </summary>
    public static async Task<bool> RelevarAsync(
        ICatalogoIncorporacionCartera catalogo,
        IEnumerable<OperacionConPrincipal> operacionesSinPrincipal,
        Guid operadorTenantId,
        Guid? coordinadorUsuarioId,
        CancellationToken cancellationToken)
    {
        if (coordinadorUsuarioId is not { } coordinadorId)
            return true;

        foreach (var operacion in operacionesSinPrincipal)
        {
            using (AmbitoTenantExplicito.Establecer(operacion.PropietarioTenantId))
            {
                var relevo = await catalogo.RelevarPrincipalAsync(
                    operacion.PropietarioTenantId, operadorTenantId, operacion.AsignacionOperacionId,
                    coordinadorId, cancellationToken);
                if (relevo == ResultadoRelevoPrincipal.SinRelevo)
                    continue;
                if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Para un principal cuya cartera <b>sigue viva</b> (su cuenta se desactiva): apaga su marca
    /// en cada operación, guarda, y releva a su Coordinador CAE. Mismas condiciones y mismo
    /// significado del <c>false</c> que <see cref="RelevarAsync"/>.
    /// </summary>
    public static async Task<bool> ApagarYRelevarAsync(
        ICatalogoIncorporacionCartera catalogo,
        IReadOnlyCollection<OperacionConPrincipal> operaciones,
        Guid operadorTenantId,
        Guid principalUsuarioId,
        Guid? coordinadorUsuarioId,
        CancellationToken cancellationToken)
    {
        foreach (var operacion in operaciones)
        {
            using (AmbitoTenantExplicito.Establecer(operacion.PropietarioTenantId))
            {
                if (!await catalogo.ApagarPrincipalAsync(
                        operadorTenantId, operacion.AsignacionOperacionId, principalUsuarioId, cancellationToken)
                    || !await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                    return false;
            }
        }

        return await RelevarAsync(catalogo, operaciones, operadorTenantId, coordinadorUsuarioId, cancellationToken);
    }
}
