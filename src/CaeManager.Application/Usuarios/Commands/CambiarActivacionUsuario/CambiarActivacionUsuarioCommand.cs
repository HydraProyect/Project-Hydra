using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Usuarios.Commands.CambiarActivacionUsuario;

/// <summary>
/// Desactiva o reactiva una cuenta del Tenant activo (P1-I2: antes lo hacía
/// <c>Usuarios.razor.cs</c> contra <c>UserManager</c>). Desactivar bloquea la
/// cuenta y renueva su sello de seguridad en la misma escritura, para que la
/// cookie y el circuito ya abiertos dejen de valer.
///
/// <para>
/// Nadie cambia la activación de su propia cuenta. Una cuenta que ya no existe y
/// una que existe pero es de otra organización (la fila de un Operador CAE externo
/// delegado) dan errores distintos: la primera pide recargar la lista; la
/// segunda es una fila legítima que no se puede tocar desde aquí.
/// </para>
///
/// <para>
/// No se desactiva al último Administrador del Tenant (decisión del 2026-10-30,
/// <see cref="AutoridadSobreCuentas.VerificarQueNoEsElUltimoAdministradorAsync"/>), dentro de
/// la misma transacción y antes del candado de cartera.
/// </para>
///
/// <para>
/// Desactivar toma el candado exclusivo de cartera de la cuenta dentro de una transacción
/// (<see cref="IBloqueoCarteraUsuario"/>, revisión Codex de FS-25): una reasignación o el
/// alta de un Cliente empresarial en curso para esa cuenta termina antes, y los que lleguen
/// durante esperan y ven la cuenta ya desactivada. Sin esto, la cuenta podía quedar
/// desactivada con un Cliente empresarial recién puesto en su cartera.
/// </para>
///
/// <para>
/// <b>Cartera de un Gestor CAE</b> (decisión de Chris del 2026-11-14, «Baja de un Usuario frente a
/// baja del Tenant», que sustituye para el Gestor CAE a la opción C del 2026-09-24): desactivar a
/// un Gestor CAE con Asignaciones de Cartera vigentes de Tenants beneficiarios enteros
/// (<see cref="ICatalogoIncorporacionCartera.ObtenerCarteraUniversalAsync"/>) las retira todas en
/// la misma transacción; ninguna se queda a nombre de una cuenta que ya no entra. Con
/// <see cref="CambiarActivacionUsuarioCommand.GestorCaeDestinoId"/>, cada Tenant beneficiario que
/// el Operador CAE puede asignar hoy pasa a una Asignación de Cartera nueva de ese Gestor CAE
/// (cerrar y abrir otra, nunca reescribir la que había: el usuario de una cartera es inmutable), y
/// la marca de principal va con ella. Sin destino, o donde la operación ya no admite cartera nueva,
/// el Tenant beneficiario queda sin Gestor CAE y, ya confirmada la transacción, se avisa a los
/// Administradores del Tenant propietario de la cuenta (<see cref="IAvisoDeCarteraSinGestorCae"/>).
/// Quien desactiva necesita, además de autoridad sobre cuentas, autoridad sobre esa cartera
/// (<see cref="AutoridadSobreCarteraDeGestorCae"/>), y el destino tiene que ser un Gestor CAE activo
/// de su mismo Operador CAE sobre el que también la tenga. Desactivar una cuenta nunca suspende el
/// Tenant (<c>Tenant.Suspender</c> es otra operación). Reactivar no devuelve ninguna cartera.
/// </para>
///
/// <para>
/// <b>Relevo del principal</b> (ADR-011 § 2.7, enmienda 2026-10-08, punto 3): donde la marca de
/// principal de la cuenta desactivada no pasa a un destino elegido, en la misma transacción pasa a
/// su Coordinador CAE (<see cref="RelevoDePrincipalDeCartera"/>). Sin Coordinador CAE a quien
/// relevar se escala al único Coordinador CAE, Dirección CAE o Administrador del Operador CAE
/// (<see cref="EscaladoDePrincipalDeCartera"/>, punto 4); si no hay uno solo, la operación queda sin
/// principal. Una cuenta que no es Gestor CAE (por ejemplo un Coordinador CAE principal) conserva su
/// cartera y solo pierde la marca, como hasta ahora. Reactivar la cuenta no le devuelve la marca.
/// </para>
///
/// <para>
/// <b>Primer usuario elegible</b> (mismo punto 4): si la cuenta reactivada es la única activa
/// del Operador CAE con perfil Coordinador CAE, Dirección CAE o Administrador, recibe en la
/// misma transacción la cartera principal de cada operación que siga sin principal.
/// </para>
/// </summary>
/// <param name="GestorCaeDestinoId">
/// Solo al desactivar a un Gestor CAE: a quién se reasigna su cartera. <c>null</c> = a nadie (sus
/// Tenants beneficiarios quedan sin Gestor CAE y se avisa a los Administradores).
/// </param>
[AreaDelEncargo("Usuarios")]
public record CambiarActivacionUsuarioCommand(Guid UsuarioId, bool Activar, Guid? GestorCaeDestinoId = null) : ICommand;

public class CambiarActivacionUsuarioCommandHandler(
    IGestionCuentasUsuario cuentas,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera,
    ICatalogoIncorporacionCartera catalogo,
    IDirectorioDestinosCartera directorioDestinos,
    IDirectorioUsuariosService directorioUsuarios,
    IAdministradoresDelTenant administradores,
    IAvisoDeCarteraSinGestorCae avisoSinGestorCae,
    IEncargoDeAdministracionActual? encargoActual = null)
    : IRequestHandler<CambiarActivacionUsuarioCommand, Result>
{
    private const string RolGestorCae = "GestorCae";

    public static readonly Error PrincipalNoRelevado = Error.Crear(
        "Usuarios.PrincipalNoRelevado",
        "Su cartera cambió mientras tanto. No se ha cambiado nada; vuelve a intentarlo.");

    public static readonly Error DestinoSoloAlDesactivar = Error.Crear(
        "Usuarios.DestinoSoloAlDesactivar", "La cartera solo se reasigna al desactivar a un Gestor CAE.");

    public static readonly Error DestinoEsElDesactivado = Error.Crear(
        "Usuarios.DestinoEsElDesactivado", "Elige a otro Gestor CAE: la cartera no puede quedarse con quien desactivas.");

    public static readonly Error DestinoDesactivado = Error.Crear(
        "Usuarios.DestinoDesactivado", "Ese Gestor CAE tiene la cuenta desactivada: elige otro.");

    public async Task<Result> Handle(CambiarActivacionUsuarioCommand request, CancellationToken cancellationToken)
    {
        if (!await AutoridadSobreCuentas.PuedeGestionarCuentasAsync(currentUserService))
            return Result.Fallo(AutoridadSobreCuentas.SinAutoridad);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() == request.UsuarioId)
            return Result.Fallo(Error.Crear("Usuarios.PropiaCuenta", "No puedes desactivar tu propia cuenta."));

        var cuenta = await cuentas.ObtenerAsync(request.UsuarioId, cancellationToken);
        if (cuenta is null)
            return Result.Fallo(AutoridadSobreCuentas.CuentaInexistente);
        if (!cuenta.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);

        // La cuenta destino tiene rol de Propiedad: solo la toca quien actúa en su propio
        // Tenant de origen (primer acto excluido del Encargo de administración, D-8).
        var destinoIntocable = CuentasConRolDePropiedad.VerificarDestino(
            cuenta.Roles, await currentUserService.ObtenerTenantOrigenIdAsync(), tenantActual.TenantId,
            await RolesReservadosAlTenantDeOrigen.ActuaConAutoridadDePropiedadAsync(encargoActual));
        if (destinoIntocable.EsFallido)
            return destinoIntocable;

        if (request.GestorCaeDestinoId is { } destinoPedido)
        {
            if (request.Activar)
                return Result.Fallo(DestinoSoloAlDesactivar);
            if (destinoPedido == request.UsuarioId)
                return Result.Fallo(DestinoEsElDesactivado);
        }

        var esGestorCae = cuenta.Roles.Contains(RolGestorCae);
        var sinGestorCae = new List<TenantEnCarteraDeGestor>();
        Guid? operadorDelAviso = null;

        var resultado = await transaccion.EjecutarAsync(async ct =>
        {
            sinGestorCae.Clear();
            if (!request.Activar)
            {
                var quedaAdministrador = await AutoridadSobreCuentas.VerificarQueNoEsElUltimoAdministradorAsync(
                    request.UsuarioId, tenantActual.TenantId, administradores, ct);
                if (quedaAdministrador.EsFallido)
                    return quedaAdministrador;

                await bloqueoCartera.BloquearExclusivoAsync(request.UsuarioId, ct);
            }
            var cambio = await cuentas.CambiarActivacionAsync(request.UsuarioId, request.Activar, ct);
            if (cambio.EsFallido)
                return cambio;

            if (request.Activar)
                return await AsignarSiEsElPrimerElegibleAsync(request.UsuarioId, ct);

            if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } operadorTenantId)
                return request.GestorCaeDestinoId is null ? Result.Exito() : Result.Fallo(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
            operadorDelAviso = operadorTenantId;

            return await RetirarOReasignarCarteraAsync(
                request.UsuarioId, esGestorCae, request.GestorCaeDestinoId, operadorTenantId, sinGestorCae, ct);
        }, cancellationToken);

        if (resultado.EsExitoso)
        {
            if (sinGestorCae.Count > 0 && operadorDelAviso is { } operador)
                await avisoSinGestorCae.AvisarAsync(operador, cuenta.NombreCompleto, sinGestorCae, cancellationToken);
            return resultado;
        }

        return resultado.Error.Codigo == AutoridadSobreCuentas.NoEncontrado.Codigo
            ? Result.Fallo(AutoridadSobreCuentas.CuentaInexistente)
            : Result.Fallo(Error.Crear(
                resultado.Error.Codigo,
                $"No pudimos {(request.Activar ? "reactivar" : "desactivar")} esta cuenta. {resultado.Error.Mensaje}"));
    }

    /// <summary>
    /// La cuenta recién reactivada recibe las operaciones sin principal de su Operador CAE solo
    /// si es su única cuenta elegible. El Operador CAE es el Tenant de origen de quien reactiva,
    /// por lo mismo que en <see cref="CederMarcaDePrincipalAsync"/>; una cuenta de otro Tenant
    /// no aparece entre las elegibles de ese y no recibe nada.
    /// </summary>
    private async Task<Result> AsignarSiEsElPrimerElegibleAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } operadorTenantId)
            return Result.Exito();

        return await EscaladoDePrincipalDeCartera.AsignarAlPrimerElegibleAsync(
                catalogo, directorioUsuarios, bloqueoCartera, usuarioId, operadorTenantId, cancellationToken)
            ? Result.Exito()
            : Result.Fallo(PrincipalNoRelevado);
    }

    /// <summary>
    /// Apaga la marca de principal de la cuenta recién desactivada en cada operación externa de
    /// su Operador CAE y la pasa a su Coordinador CAE o, si no lo hay, a quien corresponda por
    /// escalado. El Operador CAE es el Tenant de origen de
    /// quien desactiva: la cuenta es de su mismo Tenant (se comprobó arriba) y la política RLS
    /// de las carteras solo deja leer las del operador de la sesión.
    /// </summary>
    private async Task<Result> CederMarcaDePrincipalAsync(
        Guid usuarioId, Guid operadorTenantId, IReadOnlyCollection<OperacionConPrincipal> principales,
        CancellationToken cancellationToken)
    {
        if (principales.Count == 0)
            return Result.Exito();

        var relevo = await RelevoDePrincipalDeCartera.ResolverRelevoAsync(
            usuarioId, operadorTenantId, directorioDestinos, directorioUsuarios, bloqueoCartera, cancellationToken);

        return await RelevoDePrincipalDeCartera.ApagarYRelevarAsync(
                catalogo, principales, operadorTenantId, usuarioId, relevo, cancellationToken)
            ? Result.Exito()
            : Result.Fallo(PrincipalNoRelevado);
    }

    /// <summary>
    /// La cartera de la cuenta recién desactivada (ver el resumen de la clase). Sin Tenants
    /// beneficiarios enteros en su cartera, o si no es Gestor CAE, solo cede la marca de principal
    /// como antes. Si no, retira cada uno y, con destino, abre la cartera del destino; lo que no pasa
    /// al destino se añade a <paramref name="sinGestorCae"/> para el aviso. Se llama dentro de la
    /// transacción y con el candado exclusivo de la cuenta ya tomado.
    /// </summary>
    private async Task<Result> RetirarOReasignarCarteraAsync(
        Guid usuarioId, bool esGestorCae, Guid? destinoId, Guid operadorTenantId,
        List<TenantEnCarteraDeGestor> sinGestorCae, CancellationToken ct)
    {
        // El destino se valida siempre que se pide, haya cartera o no: un Guid de otro Tenant, de una
        // cuenta desactivada o fuera del equipo de quien actúa no pasa nunca en silencio.
        if (destinoId is { } destinoAValidar)
        {
            var destino = await ValidarDestinoAsync(destinoAValidar, operadorTenantId, ct);
            if (destino.EsFallido)
                return destino;
        }

        var principales = await catalogo.ObtenerOperacionesDondeEsPrincipalAsync(operadorTenantId, usuarioId, ct);
        IReadOnlyList<TenantEnCarteraDeGestor> enCartera = esGestorCae
            ? await catalogo.ObtenerCarteraUniversalAsync(operadorTenantId, usuarioId, ct)
            : [];
        if (enCartera.Count == 0)
            return await CederMarcaDePrincipalAsync(usuarioId, operadorTenantId, principales, ct);

        // Mover o retirar una cartera exige autoridad sobre ella, no solo sobre la cuenta. Ya con el
        // candado de la cuenta: nadie la cambia de equipo mientras tanto.
        var autoridad = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(
            usuarioId, currentUserService, directorioUsuarios, directorioDestinos, ct);
        if (autoridad.EsFallido)
            return Result.Fallo(autoridad.Error);
        if (autoridad.Valor.OperadorTenantId != operadorTenantId)
            return Result.Fallo(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
        var actorUsuarioId = autoridad.Valor.ActorUsuarioId;

        var asignables = destinoId is null
            ? new Dictionary<Guid, TenantCandidatoIncorporacion>()
            : (await catalogo.ObtenerAsignablesAsync(operadorTenantId, ct))
                .GroupBy(a => a.PropietarioTenantId)
                .ToDictionary(g => g.Key, g => g.First());

        // Primero se cierran todas (el cierre apaga la marca y el índice único de principal no es
        // diferible): así la cartera del destino puede nacer principal.
        foreach (var tenant in enCartera)
        {
            var retirada = await EnPropietarioAsync(tenant.PropietarioTenantId, async () =>
                await catalogo.RetirarCarteraUniversalAsync(
                    tenant.PropietarioTenantId, operadorTenantId, usuarioId, actorUsuarioId, ct)
                    ? Result.Exito()
                    : Result.Fallo(PrincipalNoRelevado), ct);
            if (retirada.EsFallido)
                return retirada;
        }

        var aRelevar = new List<OperacionConPrincipal>();
        foreach (var tenant in enCartera)
        {
            var principalesDelTenant = principales.Where(p => p.PropietarioTenantId == tenant.PropietarioTenantId).ToList();
            if (destinoId is not { } destinoUsuarioId
                || !asignables.TryGetValue(tenant.PropietarioTenantId, out var asignable))
            {
                sinGestorCae.Add(tenant);
                aRelevar.AddRange(principalesDelTenant);
                continue;
            }

            Guid? principalYaCubierto = null;
            var incorporada = await EnPropietarioAsync(tenant.PropietarioTenantId, async () =>
            {
                var r = await catalogo.IncorporarAsync(
                    tenant.PropietarioTenantId, operadorTenantId, asignable.AsignacionOperacionId, destinoUsuarioId, ct);
                if (r.Cartera is { EsPrincipal: true } nueva)
                    principalYaCubierto = nueva.AsignacionOperacionId;
                // YaEnCartera: el destino ya tenía ese Tenant beneficiario entero; no se le duplica.
                return r.MotivoAnulacion is null or MotivoAnulacionSolicitudCartera.YaEnCartera
                    ? Result.Exito()
                    : Result.Fallo(PrincipalNoRelevado);
            }, ct);
            if (incorporada.EsFallido)
                return incorporada;

            foreach (var principal in principalesDelTenant.Where(p => p.AsignacionOperacionId != principalYaCubierto))
            {
                using (AmbitoTenantExplicito.Establecer(principal.PropietarioTenantId))
                {
                    if (!await catalogo.EncenderPrincipalAsync(operadorTenantId, principal.AsignacionOperacionId, destinoUsuarioId, ct))
                    {
                        aRelevar.Add(principal);
                        continue;
                    }
                    if (!await catalogo.GuardarDetectandoCarreraAsync(ct))
                        return Result.Fallo(PrincipalNoRelevado);
                }
            }
        }

        if (aRelevar.Count > 0)
        {
            var relevo = await RelevoDePrincipalDeCartera.ResolverRelevoAsync(
                usuarioId, operadorTenantId, directorioDestinos, directorioUsuarios, bloqueoCartera, ct);
            if (!await RelevoDePrincipalDeCartera.RelevarAsync(catalogo, aRelevar, operadorTenantId, relevo, ct))
                return Result.Fallo(PrincipalNoRelevado);
        }

        // Una marca que no colgaba de una cartera entera de Gestor CAE (no debería existir: el CHECK de
        // principal exige el Tenant entero) se cede como antes.
        var enCarteraIds = enCartera.Select(t => t.PropietarioTenantId).ToHashSet();
        return await CederMarcaDePrincipalAsync(
            usuarioId, operadorTenantId, principales.Where(p => !enCarteraIds.Contains(p.PropietarioTenantId)).ToList(), ct);
    }

    /// <summary>
    /// El destino de la reasignación: un Gestor CAE activo del mismo Operador CAE sobre cuya cartera
    /// quien actúa tiene autoridad (<see cref="AutoridadSobreCarteraDeGestorCae"/>: una cuenta de otro
    /// Tenant no es alcanzable, una de un Operador CAE externo delegado tampoco). Toma antes el candado
    /// compartido de su cartera, como el relevo: una desactivación del destino en curso termina antes
    /// y aquí se la ve desactivada.
    /// </summary>
    private async Task<Result> ValidarDestinoAsync(Guid destinoUsuarioId, Guid operadorTenantId, CancellationToken ct)
    {
        await bloqueoCartera.BloquearCompartidoAsync([destinoUsuarioId], ct);

        var destino = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(
            destinoUsuarioId, currentUserService, directorioUsuarios, directorioDestinos, ct);
        if (destino.EsFallido)
            return Result.Fallo(destino.Error);
        if (destino.Valor.OperadorTenantId != operadorTenantId)
            return Result.Fallo(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
        return destino.Valor.GestorActivo ? Result.Exito() : Result.Fallo(DestinoDesactivado);
    }

    /// <summary>
    /// Ejecuta <paramref name="escribir"/> con el Tenant propietario como Tenant activo (la política
    /// RLS de las carteras solo deja escribir sobre el propietario contextual) y guarda; el mismo
    /// patrón que <c>AsignarCarteraGestorCaeCommand</c>. El Guid sale del catálogo, no del cliente.
    /// </summary>
    private async Task<Result> EnPropietarioAsync(Guid propietarioTenantId, Func<Task<Result>> escribir, CancellationToken ct)
    {
        using (AmbitoTenantExplicito.Establecer(propietarioTenantId))
        {
            var resultado = await escribir();
            if (resultado.EsFallido)
                return resultado;

            return await catalogo.GuardarDetectandoCarreraAsync(ct)
                ? Result.Exito()
                : Result.Fallo(PrincipalNoRelevado);
        }
    }
}
