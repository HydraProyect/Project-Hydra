using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;

/// <summary>
/// Asigna y/o retira Tenants beneficiarios enteros de la cartera de un Gestor CAE que ya
/// existe (PR siguiente a la del alta con cartera, petición del propietario del 2026-09-28).
/// La Asignación de Cartera es la misma que crea aceptar una solicitud o dar de alta con
/// cartera —universal, rol Gestor CAE, sin caducidad—, escrita por el mismo
/// <see cref="ICatalogoIncorporacionCartera"/>; el flujo de solicitud de cartera sigue igual.
///
/// <para>
/// <b>Autoridad</b>: <see cref="AutoridadSobreCarteraDeGestorCae"/> (Administrador, Dirección CAE
/// y Coordinador CAE sobre su equipo; ninguno concede roles de Propiedad).
/// <b>Qué se asigna</b>: solo Tenants entre los asignables del Operador CAE
/// (<see cref="ICatalogoIncorporacionCartera.ObtenerAsignablesAsync"/>: Asignación de Operación
/// externa, no raíz, universal y vigente, con su delegación viva). <b>Qué se retira</b>: solo
/// Tenants que el Gestor CAE tiene enteros
/// (<see cref="ICatalogoIncorporacionCartera.ObtenerCarteraUniversalAsync"/>); retirar no exige
/// que la operación siga vigente. FS-25 no exige que un Tenant conserve un Gestor CAE con
/// cartera: la cuenta que lo pierde no queda desactivada ni sin rol.
/// </para>
///
/// <para>
/// <b>Atomicidad y candado</b>: todo va en una transacción (<see cref="ITransaccionDeComando"/>)
/// con el candado compartido de cartera de FS-25 sobre el Gestor CAE
/// (<see cref="IBloqueoCarteraUsuario"/>): espera a una desactivación con traspaso en curso y,
/// si la cuenta quedó desactivada, no le asigna nada (retirar sí se permite).
/// </para>
///
/// <para>
/// <b>Relevo del principal</b>: si una cartera retirada era la principal de su Asignación de
/// Operación, la marca pasa en la misma transacción al Coordinador CAE de ese Gestor CAE
/// (<see cref="RelevoDePrincipalDeCartera"/>): se marca su cartera viva o se le emite una de
/// Coordinador CAE. Las carteras de apoyo no se tocan. Sin Coordinador CAE, la operación
/// queda sin principal.
/// </para>
/// </summary>
public record AsignarCarteraGestorCaeCommand(
    Guid GestorUsuarioId,
    IReadOnlyCollection<Guid>? TenantsAAsignar,
    IReadOnlyCollection<Guid>? TenantsARetirar) : ICommand;

public class AsignarCarteraGestorCaeCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IDirectorioDestinosCartera directorioDestinos,
    ICatalogoIncorporacionCartera catalogo,
    ITransaccionDeComando transaccion,
    IBloqueoCarteraUsuario bloqueoCartera)
    : IRequestHandler<AsignarCarteraGestorCaeCommand, Result>
{
    public static readonly Error EmpresaNoAsignable = Error.Crear(
        "Cartera.EmpresaNoAsignable",
        "Tu organización ya no gestiona alguna de las empresas marcadas. Revisa la lista y vuelve a guardar.");

    public static readonly Error EmpresaNoEnCartera = Error.Crear(
        "Cartera.EmpresaNoEnCartera",
        "Alguna de las empresas a retirar ya no está entera en la cartera de este Gestor CAE. Revisa la lista y vuelve a guardar.");

    /// <summary>
    /// El Gestor CAE ya tiene esa empresa: entera (cambió mientras decidías) o por otra vía (otra
    /// cartera vigente, por ejemplo de otro rol). Este Command no la sustituye: el writer no
    /// ensancha en silencio el alcance que otro decidió (ver <c>ICatalogoIncorporacionCartera</c>).
    /// </summary>
    public static readonly Error YaTieneCartera = Error.Crear(
        "Cartera.YaTieneCartera",
        "Ese Gestor CAE ya tiene esa empresa en su cartera: cambió mientras decidías, o la tiene por otra vía. Cierra y vuelve a abrir la lista para verla al día.");

    public static readonly Error EmpresaEnAmbasListas = Error.Crear(
        "Cartera.EmpresaEnAmbasListas", "Una empresa no puede asignarse y retirarse a la vez.");

    public static readonly Error GestorDesactivado = Error.Crear(
        "Cartera.GestorDesactivado", "Ese Gestor CAE tiene la cuenta desactivada: no se le pueden asignar empresas.");

    public static readonly Error CarteraNoGuardada = Error.Crear(
        "Cartera.CarteraNoGuardada",
        "No pudimos guardar la cartera. No se ha cambiado nada; vuelve a intentarlo.");

    public async Task<Result> Handle(AsignarCarteraGestorCaeCommand request, CancellationToken cancellationToken)
    {
        var contexto = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(
            request.GestorUsuarioId, currentUserService, directorioUsuarios, directorioDestinos, cancellationToken);
        if (contexto.EsFallido)
            return Result.Fallo(contexto.Error);
        var ctx = contexto.Valor;

        var aAsignar = request.TenantsAAsignar?.Distinct().ToList() ?? [];
        var aRetirar = request.TenantsARetirar?.Distinct().ToList() ?? [];
        if (aAsignar.Count == 0 && aRetirar.Count == 0)
            return Result.Exito();
        if (aAsignar.Intersect(aRetirar).Any())
            return Result.Fallo(EmpresaEnAmbasListas);
        if (aAsignar.Count > 0 && !ctx.GestorActivo)
            return Result.Fallo(GestorDesactivado);

        // Lecturas antes de la transacción, en el Tenant de origen; la operación de cada
        // cartera la decide el servidor, de la petición solo salen los Tenants.
        var operaciones = new List<TenantCandidatoIncorporacion>(aAsignar.Count);
        if (aAsignar.Count > 0)
        {
            var asignables = (await catalogo.ObtenerAsignablesAsync(ctx.OperadorTenantId, cancellationToken))
                .ToDictionary(a => a.PropietarioTenantId);
            foreach (var propietarioTenantId in aAsignar)
            {
                if (!asignables.TryGetValue(propietarioTenantId, out var asignable))
                    return Result.Fallo(EmpresaNoAsignable);
                operaciones.Add(asignable);
            }
        }

        if (aRetirar.Count > 0)
        {
            var enCartera = (await catalogo.ObtenerCarteraUniversalAsync(ctx.OperadorTenantId, ctx.GestorUsuarioId, cancellationToken))
                .Select(t => t.PropietarioTenantId)
                .ToHashSet();
            if (aRetirar.Any(t => !enCartera.Contains(t)))
                return Result.Fallo(EmpresaNoEnCartera);
        }

        try
        {
            return await transaccion.EjecutarAsync(async ct =>
            {
                // Candado de FS-25 sobre el Gestor CAE. Asignar toma el compartido (espera a una
                // desactivación con traspaso en curso y ve su resultado). Retirar toma el EXCLUSIVO:
                // decide si la fila heredada sobra mirando las demás carteras, y una reasignación de
                // Cliente empresarial hacia este Gestor CAE (que toma el compartido) podría confirmar
                // una cartera parcial entre esa lectura y el borrado (Codex, #996, pasada 1).
                if (aRetirar.Count > 0)
                    await bloqueoCartera.BloquearExclusivoAsync(ctx.GestorUsuarioId, ct);
                else
                    await bloqueoCartera.BloquearCompartidoAsync([ctx.GestorUsuarioId], ct);

                // Autoridad completa OTRA VEZ, ya con el candado: la de antes se leyó fuera de la
                // transacción, y otro circuito pudo mover al Gestor CAE a otro equipo, cambiarle el
                // rol, desactivarlo o quitarle el rol al actor (Codex, #996, pasada 1).
                var vigente = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(
                    request.GestorUsuarioId, currentUserService, directorioUsuarios, directorioDestinos, ct);
                if (vigente.EsFallido)
                    return Result.Fallo(vigente.Error);
                if (vigente.Valor.OperadorTenantId != ctx.OperadorTenantId || vigente.Valor.ActorUsuarioId != ctx.ActorUsuarioId)
                    return Result.Fallo(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
                if (aAsignar.Count > 0 && !vigente.Valor.GestorActivo)
                    return Result.Fallo(GestorDesactivado);

                // Qué operaciones se van a quedar sin principal, leído antes de cerrar nada (el
                // cierre apaga la marca), y a quién se releva.
                var sinPrincipal = aRetirar.Count == 0
                    ? []
                    : (await catalogo.ObtenerOperacionesDondeEsPrincipalAsync(ctx.OperadorTenantId, ctx.GestorUsuarioId, ct))
                        .Where(o => aRetirar.Contains(o.PropietarioTenantId))
                        .ToList();
                var coordinadorDeRelevo = sinPrincipal.Count == 0
                    ? null
                    : await RelevoDePrincipalDeCartera.ResolverCoordinadorAsync(
                        ctx.GestorUsuarioId, ctx.OperadorTenantId, directorioDestinos, directorioUsuarios, bloqueoCartera, ct);

                foreach (var propietarioTenantId in aRetirar)
                {
                    var retirada = await EnPropietarioAsync(propietarioTenantId, async () =>
                        await catalogo.RetirarCarteraUniversalAsync(
                            propietarioTenantId, ctx.OperadorTenantId, ctx.GestorUsuarioId, ctx.ActorUsuarioId, ct)
                            ? Result.Exito()
                            : Result.Fallo(EmpresaNoEnCartera), ct);
                    if (retirada.EsFallido)
                        return retirada;
                }

                // Con los cierres ya guardados: el índice único de principal no es diferible.
                if (!await RelevoDePrincipalDeCartera.RelevarAsync(
                        catalogo, sinPrincipal, ctx.OperadorTenantId, coordinadorDeRelevo, ct))
                    return Result.Fallo(CarteraNoGuardada);

                foreach (var operacion in operaciones)
                {
                    var incorporada = await EnPropietarioAsync(operacion.PropietarioTenantId, async () =>
                    {
                        var r = await catalogo.IncorporarAsync(
                            operacion.PropietarioTenantId, ctx.OperadorTenantId, operacion.AsignacionOperacionId,
                            ctx.GestorUsuarioId, ct);
                        return r.MotivoAnulacion switch
                        {
                            null => Result.Exito(),
                            MotivoAnulacionSolicitudCartera.OperacionNoVigente => Result.Fallo(EmpresaNoAsignable),
                            MotivoAnulacionSolicitudCartera.YaEnCartera => Result.Fallo(YaTieneCartera),
                            _ => Result.Fallo(CarteraNoGuardada),
                        };
                    }, ct);
                    if (incorporada.EsFallido)
                        return incorporada;
                }

                return Result.Exito();
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // La transacción ya se deshizo y el contexto quedó vacío (ITransaccionDeComando).
            return Result.Fallo(CarteraNoGuardada);
        }
    }

    /// <summary>
    /// Ejecuta <paramref name="escribir"/> con el Tenant propietario como Tenant activo (la
    /// política RLS de las carteras solo deja escribir sobre el propietario contextual) y
    /// guarda. El Guid sale de listas ya validadas contra el Operador CAE, no del cliente.
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
                : Result.Fallo(CarteraNoGuardada);
        }
    }
}
