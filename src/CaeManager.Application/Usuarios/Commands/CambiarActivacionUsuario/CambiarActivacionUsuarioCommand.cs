using CaeManager.Application.Clientes;
using CaeManager.Application.Comercial.LineaOutbound;
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
/// la misma transacción; ninguna se queda a nombre de una cuenta que ya no entra. Exige una decisión
/// explícita: <see cref="CambiarActivacionUsuarioCommand.GestorCaeDestinoId"/> o
/// <see cref="CambiarActivacionUsuarioCommand.SinDestinoConfirmado"/>. Sin ninguna de las dos, si
/// hay cartera, no se desactiva (<see cref="CambiarActivacionUsuarioCommandHandler.DecisionDeCarteraRequerida"/>):
/// la cartera pudo aparecer después de abrir el diálogo.
/// </para>
///
/// <list type="bullet">
/// <item><b>Con destino</b>: cada Tenant beneficiario que el Operador CAE puede asignar hoy pasa a una
/// Asignación de Cartera nueva del destino en la misma Asignación de Operación (cerrar y abrir otra,
/// nunca reescribir la que había: el usuario de una cartera es inmutable), y la marca de principal va
/// con ella. Si el destino ya tenía ese Tenant beneficiario, se reutiliza su cartera solo si es del
/// Tenant entero, sin fecha de fin y bajo esa misma Asignación de Operación; si no (cartera parcial,
/// fila heredada sola o apoyo temporal), la transacción entera se deshace
/// (<see cref="CambiarActivacionUsuarioCommandHandler.DestinoConCarteraIncompleta"/>).</item>
/// <item><b>Sin operación propia que la admita</b>: una cartera del desactivado cuya Asignación de Operación ya no
/// autoriza, o que no es la que hoy admite cartera nueva en ese Tenant beneficiario, se retira sin pasarla a
/// otra operación, y el Tenant beneficiario entra en el aviso si se queda sin Gestor CAE.</item>
/// <item><b>Cambio de rol previo</b>: el rol se vuelve a leer bajo el candado. Una cuenta que ya no es Gestor CAE
/// pero conserva carteras de Gestor CAE no se desactiva
/// (<see cref="CambiarActivacionUsuarioCommandHandler.CarteraDeOtroRol"/>): no hay sobre quién decidir con la
/// autoridad de cartera de Gestor CAE, y desactivarla las dejaría vivas.</item>
/// <item><b>Apoyos temporales</b> del desactivado (Asignación de Cartera con fecha de fin): se retiran
/// sin reasignarlos (decisión de Chris del 2026-11-16).</item>
/// <item><b>Aviso</b>: ya confirmada la transacción, se avisa a los Administradores del Tenant
/// propietario de la cuenta (<see cref="IAvisoDeCarteraSinGestorCae"/>) de los Tenants beneficiarios
/// que quedaron de verdad sin ningún Gestor CAE: ninguna Asignación de Cartera de Gestor CAE del Tenant
/// entero y sin fecha de fin. Un Tenant beneficiario que sigue con otro Gestor CAE no entra.</item>
/// <item><b>Fuera del Tenant de origen</b> (Encargo de administración, DelegacionTenant o Sesión
/// Privilegiada): desactivar a un Gestor CAE falla cerrado
/// (<see cref="CambiarActivacionUsuarioCommandHandler.CarteraFueraDelTenantDeOrigen"/>). Su cartera es
/// del Tenant activo como Operador CAE, y la política RLS de las carteras solo deja verla desde ese
/// Tenant como origen; desde fuera no se puede saber si tiene cartera, y se trata como si la tuviera.</item>
/// <item><b>Cartera interna</b> (la de la operación raíz del propio Tenant): se conserva (decisión de
/// Chris del 2026-11-16); no es de ningún Tenant beneficiario.</item>
/// </list>
///
/// <para>
/// Quien desactiva necesita, además de autoridad sobre cuentas, autoridad sobre esa cartera
/// (<see cref="AutoridadSobreCarteraDeGestorCae"/>), y el destino tiene que ser un Gestor CAE activo, ya
/// activado (no pendiente de activación), de su mismo Operador CAE y sobre el que también la tenga.
/// Desactivar una cuenta nunca suspende el Tenant (<c>Tenant.Suspender</c> es otra operación).
/// Reactivar no devuelve ninguna cartera.
/// </para>
///
/// <para>
/// <b>Relevo del principal</b> (ADR-011 § 2.7, enmienda 2026-10-08, punto 3): donde la marca de
/// principal de la cuenta desactivada no pasa a un destino elegido, en la misma transacción pasa a
/// su Coordinador CAE (<see cref="RelevoDePrincipalDeCartera"/>). Sin Coordinador CAE a quien
/// relevar se escala al único Coordinador CAE, Dirección CAE o Administrador del Operador CAE
/// (<see cref="EscaladoDePrincipalDeCartera"/>, punto 4); si no hay uno solo, la operación queda sin
/// principal. Una cuenta que no es Gestor CAE (por ejemplo un Coordinador CAE principal) conserva su
/// cartera y solo pierde la marca. Reactivar la cuenta no le devuelve la marca.
/// </para>
///
/// <para>
/// <b>Primer usuario elegible</b> (mismo punto 4): si la cuenta reactivada es la única activa
/// del Operador CAE con perfil Coordinador CAE, Dirección CAE o Administrador, recibe en la
/// misma transacción la cartera principal de cada operación que siga sin principal.
/// </para>
/// </summary>
/// <param name="GestorCaeDestinoId">
/// Solo al desactivar a un Gestor CAE: a quién se reasigna su cartera.
/// </param>
/// <param name="SinDestinoConfirmado">
/// Solo al desactivar a un Gestor CAE: quien desactiva decidió que su cartera no pasa a nadie («A nadie
/// por ahora»); sus Tenants beneficiarios sin otro Gestor CAE quedan sin Gestor CAE y se avisa a los
/// Administradores. No se combina con <paramref name="GestorCaeDestinoId"/>.
/// </param>
[AreaDelEncargo("Usuarios")]
public record CambiarActivacionUsuarioCommand(
    Guid UsuarioId, bool Activar, Guid? GestorCaeDestinoId = null, bool SinDestinoConfirmado = false) : ICommand;

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
    IOutboundCerradoTenantTec outboundCerrado,
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

    public static readonly Error DestinoPendienteDeActivar = Error.Crear(
        "Usuarios.DestinoPendienteDeActivar", "Ese Gestor CAE aún no ha activado su cuenta: elige otro.");

    public static readonly Error DecisionDeCarteraAmbigua = Error.Crear(
        "Usuarios.DecisionDeCarteraAmbigua", "Elige a quién pasa su cartera o confirma que no pasa a nadie, no las dos cosas.");

    public static readonly Error DecisionDeCarteraRequerida = Error.Crear(
        "Usuarios.DecisionDeCarteraRequerida",
        "Tiene organizaciones en su cartera: elige a quién pasan o confirma que se quedan sin Gestor CAE. No se ha cambiado nada.");

    public static readonly Error DestinoConCarteraIncompleta = Error.Crear(
        "Usuarios.DestinoConCarteraIncompleta",
        "El Gestor CAE elegido ya tiene una parte o un apoyo temporal de alguna de esas organizaciones. No se ha cambiado nada; elige otro o resuélvelo antes.");

    public static readonly Error CarteraDeOtroRol = Error.Crear(
        "Usuarios.CarteraDeOtroRol",
        "Conserva organizaciones en su cartera de cuando era Gestor CAE. Devuélvele el rol Gestor CAE para decidir a quién pasan y desactívala después. No se ha cambiado nada.");

    public static readonly Error CarteraFueraDelTenantDeOrigen = Error.Crear(
        "Usuarios.CarteraFueraDelTenantDeOrigen",
        "Un Gestor CAE solo se desactiva desde su propia organización, porque hay que decidir a quién pasa su cartera. No se ha cambiado nada.");

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

        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        // La cuenta destino tiene rol de Propiedad: solo la toca quien actúa en su propio
        // Tenant de origen (primer acto excluido del Encargo de administración, D-8).
        var destinoIntocable = CuentasConRolDePropiedad.VerificarDestino(
            cuenta.Roles, tenantOrigenId, tenantActual.TenantId,
            await RolesReservadosAlTenantDeOrigen.ActuaConAutoridadDePropiedadAsync(encargoActual));
        if (destinoIntocable.EsFallido)
            return destinoIntocable;

        var esGestorCae = cuenta.Roles.Contains(RolGestorCae);
        if (request.GestorCaeDestinoId is { } destinoPedido)
        {
            if (request.Activar || !esGestorCae)
                return Result.Fallo(DestinoSoloAlDesactivar);
            if (request.SinDestinoConfirmado)
                return Result.Fallo(DecisionDeCarteraAmbigua);
            if (destinoPedido == request.UsuarioId)
                return Result.Fallo(DestinoEsElDesactivado);
        }
        else if (request.Activar && request.SinDestinoConfirmado)
            return Result.Fallo(DestinoSoloAlDesactivar);

        // Falla cerrado (decidido por el módulo Outbound el 2026-11-16, sin Chris; DECISION_LOG): fuera del Tenant de origen la cartera del Gestor CAE
        // no es visible (RLS: su Operador CAE es el Tenant activo, no el de origen), y desactivar no puede
        // dejarla viva a nombre de una cuenta que ya no entra ni retirarla sin verla.
        if (!request.Activar && esGestorCae && (tenantOrigenId is null || tenantOrigenId != tenantActual.TenantId))
            return Result.Fallo(CarteraFueraDelTenantDeOrigen);

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

            // El rol se vuelve a leer con el candado tomado: el leído al empezar pudo cambiar después, y es
            // el de ahora el que dice si la cartera se retira y si la guarda del Tenant de origen aplica.
            var esGestorCaeAhora = esGestorCae;
            if (!request.Activar)
            {
                var cuentaAhora = await cuentas.ObtenerAsync(request.UsuarioId, ct);
                if (cuentaAhora is null)
                    return Result.Fallo(AutoridadSobreCuentas.NoEncontrado);
                esGestorCaeAhora = cuentaAhora.Roles.Contains(RolGestorCae);
                if (esGestorCaeAhora && (tenantOrigenId is null || tenantOrigenId != tenantActual.TenantId))
                    return Result.Fallo(CarteraFueraDelTenantDeOrigen);
            }

            var cambio = await cuentas.CambiarActivacionAsync(request.UsuarioId, request.Activar, ct);
            if (cambio.EsFallido)
                return cambio;

            if (request.Activar)
                return await AsignarSiEsElPrimerElegibleAsync(request.UsuarioId, ct);

            if (tenantOrigenId is not { } operadorTenantId)
                return Result.Exito();
            operadorDelAviso = operadorTenantId;

            return await RetirarOReasignarCarteraAsync(
                request.UsuarioId, esGestorCaeAhora, request.GestorCaeDestinoId, request.SinDestinoConfirmado,
                operadorTenantId, sinGestorCae, ct);
        }, cancellationToken);

        if (resultado.EsExitoso)
        {
            // La transacción ya está confirmada: el aviso no depende de que la petición siga viva.
            if (sinGestorCae.Count > 0 && operadorDelAviso is { } operador)
                await avisoSinGestorCae.AvisarAsync(operador, cuenta.NombreCompleto, sinGestorCae, CancellationToken.None);
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
    /// beneficiarios enteros en su cartera, o si no es Gestor CAE, solo cede la marca de principal.
    /// Si no, exige la decisión, retira cada uno y, con destino, abre o reutiliza la cartera del
    /// destino; al final anota en <paramref name="sinGestorCae"/> los que quedaron sin ningún Gestor
    /// CAE. Se llama dentro de la transacción y con el candado exclusivo de la cuenta ya tomado.
    /// </summary>
    private async Task<Result> RetirarOReasignarCarteraAsync(
        Guid usuarioId, bool esGestorCae, Guid? destinoId, bool sinDestinoConfirmado, Guid operadorTenantId,
        List<TenantEnCarteraDeGestor> sinGestorCae, CancellationToken ct)
    {
        // El destino se valida siempre que se pide, haya cartera o no: un Guid de otro Tenant, de una
        // cuenta desactivada o pendiente, o fuera del equipo de quien actúa no pasa nunca en silencio.
        if (destinoId is { } destinoAValidar)
        {
            var destino = await ValidarDestinoAsync(destinoAValidar, operadorTenantId, ct);
            if (destino.EsFallido)
                return destino;
        }

        var principales = await catalogo.ObtenerOperacionesDondeEsPrincipalAsync(operadorTenantId, usuarioId, ct);
        // Se lee con cualquier rol: una cuenta a la que le cambiaron el rol puede conservar carteras de
        // Gestor CAE (el cambio de rol no las cierra), y desactivarla no puede dejarlas vivas en silencio.
        var enCartera = await catalogo.ObtenerCarteraUniversalAsync(operadorTenantId, usuarioId, ct);
        if (enCartera.Count == 0)
            return await CederMarcaDePrincipalAsync(usuarioId, operadorTenantId, principales, ct);
        if (!esGestorCae)
            return Result.Fallo(CarteraDeOtroRol);

        // Ya con el candado: la cartera que se ve ahora es la que se retira. Si apareció después de
        // abrir el diálogo, quien desactiva no decidió sobre ella.
        if (destinoId is null && !sinDestinoConfirmado)
            return Result.Fallo(DecisionDeCarteraRequerida);

        // Mover o retirar una cartera exige autoridad sobre ella, no solo sobre la cuenta. Ya con el
        // candado de la cuenta: nadie la cambia de equipo mientras tanto.
        var autoridad = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(
            usuarioId, currentUserService, directorioUsuarios, directorioDestinos, ct);
        if (autoridad.EsFallido)
            return Result.Fallo(autoridad.Error);
        if (autoridad.Valor.OperadorTenantId != operadorTenantId)
            return Result.Fallo(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
        var actorUsuarioId = autoridad.Valor.ActorUsuarioId;

        // Lo que se puede reasignar de cada Tenant beneficiario: las Asignaciones de Operación bajo las que el
        // desactivado tiene una cartera del Tenant entero, sin fecha de fin, que autoriza hoy. Un apoyo temporal
        // se retira sin reasignarlo, y una cartera bajo una operación que ya no autoriza (no aparece aquí) se
        // retira sin pasarla a otra operación del mismo Tenant beneficiario.
        var reasignables = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var tenant in enCartera)
        {
            var vivas = await catalogo.ObtenerCarterasVivasAsync(operadorTenantId, tenant.PropietarioTenantId, ct);
            reasignables[tenant.PropietarioTenantId] = vivas
                .Where(c => c.UsuarioId == usuarioId && c.Rol == RolGestorCae && c.VigenciaHasta is null)
                .Select(c => c.AsignacionOperacionId)
                .ToHashSet();
        }

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

        // D3 (EP-4: sin línea Outbound, el Tenant TEC participante no es Operador CAE de nadie): el traspaso también se
        // cierra si lo está el Operador CAE, que es el Tenant de origen de quien desactiva y el Tenant activo aquí, fuera
        // del ámbito de cada Tenant beneficiario. Solo se lee si hay a quién traspasar.
        var operadorCaeCerrado = destinoId is not null && await outboundCerrado.EstaCerradoAsync(operadorTenantId, ct);

        var aRelevar = new List<OperacionConPrincipal>();
        foreach (var tenant in enCartera)
        {
            var principalesDelTenant = principales.Where(p => p.PropietarioTenantId == tenant.PropietarioTenantId).ToList();
            if (destinoId is not { } destinoUsuarioId
                || !asignables.TryGetValue(tenant.PropietarioTenantId, out var asignable)
                || !reasignables[tenant.PropietarioTenantId].Contains(asignable.AsignacionOperacionId))
            {
                aRelevar.AddRange(principalesDelTenant);
                continue;
            }

            // D3 (DN-7): la desactivación es una baja y sigue adelante, pero el traspaso es una Asignación de Cartera nueva
            // para el destino en el Tenant beneficiario. Si Outbound está cerrado para él, la cartera se retira sin
            // traspasarla, como la de una operación que ya no autoriza. Se lee con el Tenant beneficiario como Tenant activo.
            bool beneficiarioCerrado;
            using (AmbitoTenantExplicito.Establecer(tenant.PropietarioTenantId))
                beneficiarioCerrado = await outboundCerrado.EstaCerradoAsync(tenant.PropietarioTenantId, ct);
            if (operadorCaeCerrado || beneficiarioCerrado)
            {
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
                if (r.MotivoAnulacion is null)
                    return Result.Exito();
                if (r.MotivoAnulacion != MotivoAnulacionSolicitudCartera.YaEnCartera)
                    return Result.Fallo(PrincipalNoRelevado);

                // YaEnCartera: se reutiliza solo una cartera del Tenant entero, sin fecha de fin y bajo
                // esta misma Asignación de Operación. Una parcial, una fila heredada sola o un apoyo
                // temporal dejarían al Tenant beneficiario sin Gestor CAE entero sin que nadie lo decida.
                var vivas = await catalogo.ObtenerCarterasVivasAsync(operadorTenantId, tenant.PropietarioTenantId, ct);
                return vivas.Any(c => c.UsuarioId == destinoUsuarioId
                                      && c.AsignacionOperacionId == asignable.AsignacionOperacionId
                                      && c.Rol == RolGestorCae
                                      && c.VigenciaHasta is null)
                    ? Result.Exito()
                    : Result.Fallo(DestinoConCarteraIncompleta);
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
        var cedida = await CederMarcaDePrincipalAsync(
            usuarioId, operadorTenantId, principales.Where(p => !enCarteraIds.Contains(p.PropietarioTenantId)).ToList(), ct);
        if (cedida.EsFallido)
            return cedida;

        // El aviso: solo los Tenants beneficiarios que, ya escrito todo, no tienen ningún Gestor CAE con
        // una Asignación de Cartera del Tenant entero y sin fecha de fin.
        foreach (var tenant in enCartera)
        {
            var vivas = await catalogo.ObtenerCarterasVivasAsync(operadorTenantId, tenant.PropietarioTenantId, ct);
            if (!vivas.Any(c => c.UsuarioId != usuarioId && c.Rol == RolGestorCae && c.VigenciaHasta is null))
                sinGestorCae.Add(tenant);
        }
        return Result.Exito();
    }

    /// <summary>
    /// El destino de la reasignación: un Gestor CAE activo y ya activado del mismo Operador CAE sobre
    /// cuya cartera quien actúa tiene autoridad (<see cref="AutoridadSobreCarteraDeGestorCae"/>: una
    /// cuenta de otro Tenant no es alcanzable, una de un Operador CAE externo delegado tampoco). Toma
    /// antes el candado compartido de su cartera, como el relevo: una desactivación del destino en
    /// curso termina antes y aquí se la ve desactivada.
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
        if (!destino.Valor.GestorActivo)
            return Result.Fallo(DestinoDesactivado);

        // Una cuenta pendiente de activación no ha entrado nunca: nadie atendería la cartera.
        var cuentaDestino = await cuentas.ObtenerAsync(destinoUsuarioId, ct);
        if (cuentaDestino is null || !cuentaDestino.EsPropiaDelTenantActual)
            return Result.Fallo(AutoridadSobreCarteraDeGestorCae.GestorNoAlcanzable);
        return cuentaDestino.PendienteActivacion ? Result.Fallo(DestinoPendienteDeActivar) : Result.Exito();
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
