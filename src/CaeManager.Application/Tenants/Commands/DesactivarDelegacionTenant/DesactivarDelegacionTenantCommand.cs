using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using CaeManager.Application.Tenants.Encargo;

namespace CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;

/// <summary>
/// Revoca un Delegated Workspace (ADR-004 § 5.5, Escenario 4): la Consultora
/// deja de poder operar sobre el Cliente Delegante. No mueve ni borra una sola
/// fila de datos operativos — nunca fueron suyas.
///
/// Desactiva, no borra, siguiendo la regla explícita de <see cref="DelegacionTenant"/>:
/// conserva el histórico de qué Consultora operó sobre qué tenant y cuándo.
///
/// Sin esto la delegación era irrevocable por cualquier camino del producto
/// (hallazgo N-4 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md), que es justo lo contrario de lo
/// que promete el titular del ADR — un modelo de delegación reversible — y de
/// lo que un responsable del tratamiento debe poder garantizar.
///
/// <para>
/// Quién revoca la de un Operador CAE externo: su propio equipo con alcance sobre ese Tenant
/// propietario (<see cref="EquipoDelOperadorCaeExterno"/>, decisiones de producto del
/// 2026-10-26). El Administrador del Tenant propietario no: su salida la hace Soporte TALVEG
/// con Sesión Privilegiada.
/// </para>
/// </summary>
[AreaDelEncargo("Tenants")]
public record DesactivarDelegacionTenantCommand(Guid DelegacionTenantId) : ICommand;

/// <summary>
/// ¿Puede el usuario actual revocar este acceso ahora? Expone a la pantalla EXACTAMENTE la
/// autoridad que <see cref="DesactivarDelegacionTenantCommand"/> exige, más que siga activo:
/// el botón «Revocar acceso» se pinta solo a quien puede usarlo. Solo responde por las de
/// Operador CAE externo; las de Soporte tienen su propio botón y su propio criterio.
/// </summary>
/// <param name="Id">La delegación (Asignación de Operación delegada) sobre la que se pregunta.</param>
[AreaDelEncargo("Tenants")]
public record PuedeRevocarQuery(Guid Id) : IRequest<bool>;

public class DesactivarDelegacionTenantCommandValidator : AbstractValidator<DesactivarDelegacionTenantCommand>
{
    public DesactivarDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres revocar.");
    }
}

public class DesactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio, ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter, IUnitOfWork unitOfWork, ITenantActual tenantActual,
    IDirectorioUsuariosService directorioUsuarios, ITenantsQueryContext tenantsContext,
    ICatalogoIncorporacionCartera catalogo, ITransaccionDeComando transaccion)
    : IRequestHandler<DesactivarDelegacionTenantCommand, Result>,
      IRequestHandler<PuedeRevocarQuery, bool>
{
    public async Task<bool> Handle(PuedeRevocarQuery request, CancellationToken cancellationToken)
    {
        var acceso = await EstadoDeAccesoDelegado.LeerAsync(tenantsContext, request.Id, cancellationToken);

        return acceso is { EsDeOperadorExterno: true, Activa: true }
               && await EquipoDelOperadorCaeExterno.PuedeAsync(
                   acceso.OperadorTenantId, acceso.TenantPropietarioId,
                   currentUserService, directorioUsuarios, catalogo, cancellationToken);
    }

    /// <summary>Motivo con el que la revocación de la delegación revoca sus filas heredadas (el mismo que escribe el trigger).</summary>
    internal const string MotivoRevocacionFilaHeredada = "Revocada su DelegacionTenant: reactivarla no la repone.";

    private static readonly Error NoEncontrada =
        Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación.");

    public async Task<Result> Handle(DesactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        // "No encontrada" y no "no autorizado": confirmar la existencia de la fila ya revelaría
        // qué Operadores CAE externos operan sobre qué Tenants propietarios.
        if (delegacion is null)
            return Result.Fallo(NoEncontrada);

        // El propósito se mira una sola vez y decide las dos cosas: quién revoca y si hay
        // operación que cerrar. La de un Operador CAE externo, su equipo con alcance sobre el
        // Tenant propietario (EquipoDelOperadorCaeExterno, decisiones del 2026-10-26). La de Soporte
        // TALVEG (plano 3) conserva su criterio anterior, las dos partes
        // (DelegacionAdministrablePorElUsuarioAsync): la decisión habla del Operador CAE
        // externo, y el acceso de Soporte se cierra por su propio camino (CerrarAccesoSoporteCommand).
        var tenantPropietarioId = delegacion.TenantClienteId;
        var deOperadorExterno = delegacion.Proposito == PropositoDelegacion.OperadorExterno;
        var autorizado = deOperadorExterno
            ? await EquipoDelOperadorCaeExterno.PuedeAsync(
                delegacion.TenantConsultoraId, tenantPropietarioId,
                currentUserService, directorioUsuarios, catalogo, cancellationToken)
            : await DelegacionAdministrablePorElUsuarioAsync(delegacion, currentUserService);
        if (!autorizado)
            return Result.Fallo(NoEncontrada);

        // Contexto desde el que se revoca: la casa de uno (el Tenant de origen, que
        // la autoridad de arriba ya exige que sea una de las dos partes) o el
        // Tenant propietario. Desde un Workspace operativo derivado de un tercero
        // se rechaza aquí, con un error propio, antes de tocar nada. Esta guarda
        // solo restringe: la autoridad ya se decidió arriba por el Tenant de
        // origen, nunca por el Tenant activo.
        var tenantActivo = tenantActual.TenantId;
        if (tenantActivo is null
            || (tenantActivo != tenantPropietarioId
                && tenantActivo != await currentUserService.ObtenerTenantOrigenIdAsync()))
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.NoAutorizado",
                "Para revocar el acceso, vuelve a tu organización."));

        // La revocación escribe datos del Tenant propietario: cerrar su
        // Asignación de Operación y sus carteras. La política WITH CHECK de esa
        // tabla exige que el Tenant activo sea el propietario, y quien revoca desde la casa
        // del Operador CAE externo tiene activo el suyo. El
        // guardado se hace por eso en el ámbito del Tenant propietario, mismo
        // mecanismo que CrearClienteDeleganteCommand. No amplía la autoridad, que
        // ya se decidió arriba, y revocar solo reduce capacidad. El ámbito se abre ANTES
        // de la transacción, como en ReactivarDelegacionTenantCommand: las lecturas de
        // dentro (la operación y las carteras que se cierran) se hacen con el Tenant
        // firmado al abrirla.
        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            return await transaccion.EjecutarAsync(
                ct => RevocarAsync(delegacion.Id, tenantPropietarioId, deOperadorExterno, ct), cancellationToken);
        }
    }

    /// <summary>
    /// Bloquea la DelegacionTenant y relee su estado ANTES de leer lo que cierra, en el orden de la revocación por
    /// Soporte TALVEG (migración <c>SoporteRevocaOperacionesOutboundAcotadas</c>): primero la delegación y después la
    /// operación y sus carteras. Es lo que ordena esta revocación frente al <c>FOR SHARE</c> con que el catálogo
    /// (<c>CatalogoIncorporacionCartera.BloquearVinculosDelParAsync</c>) bloquea las delegaciones del par antes de
    /// emitir una cartera y su fila heredada. Si el catálogo va delante, la revocación espera a que confirme y cierra
    /// también la cartera que emitió; si va detrás, es el catálogo el que espera y decide sobre la delegación ya
    /// revocada. Sin el bloqueo, la revocación leía las carteras antes de que el catálogo confirmara la suya, y esa
    /// cartera quedaba vigente bajo una operación cerrada (ronda 3 de la PR #2253).
    ///
    /// <para>
    /// El estado (Activa) se decide aquí, con la fila bloqueada, y no con la lectura de autoridad hecha fuera de la
    /// transacción: dos revocaciones a la vez no dan éxito las dos. Lo que la autoridad miró (Operador CAE externo,
    /// Tenant propietario y propósito) no cambia nunca en una DelegacionTenant, así que la relectura no la invalida.
    /// </para>
    /// </summary>
    private async Task<Result> RevocarAsync(
        Guid id, Guid tenantPropietarioId, bool deOperadorExterno, CancellationToken cancellationToken)
    {
        if (!await repositorio.BloquearYReleerAsync(id, cancellationToken))
            return Result.Fallo(NoEncontrada);
        var delegacion = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
        if (delegacion is null)
            return Result.Fallo(NoEncontrada);

        if (!delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaInactiva", "Esta delegación ya estaba revocada."));

        delegacion.Desactivar();

        // Las filas heredadas (AsignacionOperadorDelegado) de una delegación de Operador CAE externo no sobreviven a
        // su revocación: autorizan por sí solas (vía heredada de TenantsBeneficiariosAutorizados) y, vivas, volverían a
        // autorizar al reactivarla, que no repone nada (opción A de Chris del 2026-11-14; ronda 1 de Codex en la PR
        // #2237, alta 1). El trigger delegacion_tenant_revoca_filas_heredadas hace lo mismo en la base de datos para
        // cualquier escritor (la ranura anterior, la función SQL de Soporte); aquí se hace también para que el dominio
        // y lo que se guarda coincidan. Se leen con la delegación ya bloqueada: una fila emitida por el catálogo antes
        // del bloqueo ya está confirmada y se revoca aquí. Las de Soporte conservan su mecánica (plano 3, ADR-011 § 8.6).
        if (deOperadorExterno)
        {
            var ahoraRevocacion = DateTime.UtcNow;
            var filasHeredadas = await tenantsContext.AsignacionesOperadorDelegado
                .Where(a => a.DelegacionTenantId == delegacion.Id)
                .ToListAsync(cancellationToken);
            foreach (var fila in filasHeredadas)
                fila.Revocar(MotivoRevocacionFilaHeredada, ahoraRevocacion);
        }

        // Solo las de Operador externo tienen operación que cerrar: las de
        // Soporte son plano 3 y conservan su mecánica propia hasta su fase
        // (ADR-011 § 8.6). Se cierran todas las Asignaciones de Operación Outbound
        // del par, del Tenant entero y acotadas, y sus carteras (decisión de Chris
        // del 2026-11-14).
        if (deOperadorExterno)
            await asignacionesWriter.CerrarOperacionDelegadaAsync(
                tenantPropietarioId, delegacion.TenantConsultoraId,
                MotivoCierreAsignacion.Revocada, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    /// <summary>
    /// Las dos partes: el Tenant de origen del usuario es el que presta el servicio o el
    /// dueño de los datos, sin mirar rol. <b>No autoriza por sí solo nada de la Asignación de
    /// Operación de un Operador CAE externo</b>: revocar es de su equipo
    /// (<see cref="EquipoDelOperadorCaeExterno"/>, decisiones del 2026-10-26), y reactivar y retirar
    /// a una persona concreta, del Administrador del Tenant propietario
    /// (<see cref="AdministradorQueReactiva"/>). Queda para las de Soporte (revocarlas y retirar a
    /// una persona) y, en <c>RevocarAsignacionOperadorDelegadoCommand</c>, como primer filtro de
    /// que la fila no es ajena.
    ///
    /// <para>
    /// Se decide con el tenant de <b>origen</b>, nunca con
    /// <c>ITenantActual.TenantId</c>: ese refleja el Delegated Workspace
    /// activo, así que un Operador Delegado que estuviera operando como el
    /// cliente pasaría por parte de ese cliente y podría tocar
    /// delegaciones de terceros sobre él. El Tenant activo no concede
    /// autoridad; el comando solo lo usa después para rechazar el
    /// Workspace operativo derivado de un tercero.
    /// </para>
    /// </summary>
    internal static async Task<bool> DelegacionAdministrablePorElUsuarioAsync(
        DelegacionTenant delegacion, ICurrentUserService currentUserService)
    {
        var tenantOrigenId = await currentUserService.ObtenerTenantOrigenIdAsync();

        return tenantOrigenId is not null &&
               (delegacion.TenantConsultoraId == tenantOrigenId.Value || delegacion.TenantClienteId == tenantOrigenId.Value);
    }
}
