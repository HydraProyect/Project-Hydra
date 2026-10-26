using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;

/// <summary>
/// Vuelve a activar una delegación revocada, sin perder qué operadores tenía
/// asignados — es la contrapartida de <c>DesactivarDelegacionTenantCommand</c>
/// y la razón de que revocar sea <c>Activa = false</c> y no un borrado
/// (ADR-004 § 5.5).
///
/// <para>
/// Quién reactiva: el equipo del Operador CAE externo con alcance sobre ese Tenant propietario
/// (<see cref="EquipoDelOperadorCaeExterno"/>, decisiones de producto del 2026-10-26), el
/// mismo que revoca. El Administrador del Tenant propietario no.
/// </para>
/// </summary>
public record ReactivarDelegacionTenantCommand(Guid DelegacionTenantId) : ICommand;

public class ReactivarDelegacionTenantCommandValidator : AbstractValidator<ReactivarDelegacionTenantCommand>
{
    public ReactivarDelegacionTenantCommandValidator()
    {
        RuleFor(c => c.DelegacionTenantId).NotEmpty().WithMessage("Indica la delegación que quieres reactivar.");
    }
}

/// <summary>
/// ¿Puede el usuario actual reactivar este acceso ahora? Expone a la vista EXACTAMENTE el
/// predicado de autoridad que <see cref="ReactivarDelegacionTenantCommand"/> exige antes de
/// reactivar, más que esté revocado: el botón «Reactivar» se pinta solo a quien puede usarlo.
/// La guarda de contexto del comando (casa de quien reactiva o Tenant propietario) no forma
/// parte de él: no decide quién puede, solo desde dónde se escribe.
///
/// <para>
/// Comparte handler con el comando a propósito: el mismo
/// <see cref="ReactivarDelegacionTenantCommandHandler.PuedeReactivarAsync"/> decide en los dos.
/// </para>
/// </summary>
/// <param name="Id">La delegación (Asignación de Operación delegada) sobre la que se pregunta.</param>
public record PuedeReactivarQuery(Guid Id) : IRequest<bool>;

public class ReactivarDelegacionTenantCommandHandler(
    IDelegacionTenantRepository repositorio,
    ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter, IUnitOfWork unitOfWork,
    ITenantsQueryContext tenantsContext,
    ITransaccionDeComando transaccion, ICatalogoIncorporacionCartera catalogo,
    IDirectorioDestinosCartera directorioDestinos, IDirectorioUsuariosService directorioUsuarios,
    IBloqueoCarteraUsuario bloqueoCartera, ITenantActual tenantActual)
    : IRequestHandler<ReactivarDelegacionTenantCommand, Result>,
      IRequestHandler<PuedeReactivarQuery, bool>
{
    /// <summary>
    /// Único punto de verdad — ver el doc-comment de <see cref="PuedeReactivarQuery"/>.
    ///
    /// <para>
    /// Solo la de un Operador CAE externo, y solo su equipo con alcance sobre el Tenant propietario
    /// (<see cref="EquipoDelOperadorCaeExterno"/>). Una de Soporte no se reactiva por aquí:
    /// se abre con motivo y ventana (<c>DelegacionTenant.Reactivar</c> lo rechaza).
    /// </para>
    ///
    /// <para>
    /// Y nunca sobre el Tenant de plataforma (hallazgo de Codex, Alto, segunda pasada sobre el
    /// incremento 1b): una <c>DelegacionTenant</c> heredada con <c>TenantClienteId</c> =
    /// Tenant de plataforma (creada antes de que <c>CrearDelegacionTenantCommand</c> excluyera
    /// ese caso, o por cualquier otra vía) podría reactivarse y reabrir operación externa sobre
    /// TALVEG — exactamente lo que ADR-011 § 1 prohíbe. Se corta DESPUÉS de confirmar la
    /// autoridad (mismo orden que el resto de la cadena: autoridad antes que catálogo).
    /// </para>
    /// </summary>
    internal async Task<bool> PuedeReactivarAsync(
        Guid operadorTenantId, Guid tenantPropietarioId, bool esDeOperadorExterno,
        CancellationToken cancellationToken)
    {
        if (!esDeOperadorExterno
            || !await EquipoDelOperadorCaeExterno.PuedeAsync(
                operadorTenantId, tenantPropietarioId,
                currentUserService, directorioUsuarios, catalogo, cancellationToken))
            return false;

        var esPlataforma = await tenantsContext.Tenants
            .Where(t => t.Id == tenantPropietarioId)
            .Select(t => t.EsPlataforma)
            .SingleOrDefaultAsync(cancellationToken);

        return !esPlataforma;
    }

    public async Task<bool> Handle(PuedeReactivarQuery request, CancellationToken cancellationToken)
    {
        var acceso = await EstadoDeAccesoDelegado.LeerAsync(tenantsContext, request.Id, cancellationToken);

        return acceso is { Activa: false }
               && await PuedeReactivarAsync(
                   acceso.OperadorTenantId, acceso.TenantPropietarioId, acceso.EsDeOperadorExterno, cancellationToken);
    }

    internal static readonly Error CambioMientrasReactivabas = Error.Crear(
        "DelegacionTenant.CambioMientrasReactivabas",
        "Alguien cambió las asignaciones de esta delegación mientras la reactivabas. Vuelve a intentarlo.");

    private static readonly Error NoEncontrada =
        Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación.");

    /// <summary>
    /// Autoridad y contexto primero, fuera de la transacción; después, en una transacción
    /// abierta con el Tenant propietario: devolver la marca de principal a quien la llevaba es
    /// un segundo guardado (el índice único de principal no es diferible y las carteras
    /// repuestas tienen que existir antes), y no puede quedar la delegación reactivada con el
    /// principal a medias.
    /// </summary>
    public async Task<Result> Handle(ReactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        // Reactivar RESTAURA capacidad —reabre la operación y las carteras de todo el equipo—,
        // así que se autoriza con la misma regla que revocar: el equipo del Operador CAE externo
        // con alcance sobre el Tenant propietario (decisiones del 2026-10-26). El Administrador
        // del Tenant propietario no.
        //
        // Se comprueba ANTES del estado de la delegación: si no, denegar con
        // "ya estaba activa" frente a "no encontrada" contaría a un tercero si
        // esa delegación está revocada ahora mismo.
        if (delegacion is null ||
            !await PuedeReactivarAsync(
                delegacion.TenantConsultoraId, delegacion.TenantClienteId,
                delegacion.Proposito == PropositoDelegacion.OperadorExterno, cancellationToken))
            // "No encontrada" y no "no autorizado", igual que revocar: confirmar
            // la existencia de la fila ya revelaría qué consultoras operan sobre
            // qué clientes.
            return Result.Fallo(NoEncontrada);

        // Contexto desde el que se reactiva, mismo criterio que la revocación: la
        // casa de quien reactiva (su Tenant de origen, que la autoridad de arriba ya exige
        // que sea el Operador CAE externo) o el Tenant propietario. Desde un Workspace
        // operativo derivado de un tercero se rechaza aquí, con un error propio y antes de
        // escribir. Esta guarda solo restringe: la autoridad ya se decidió arriba por la
        // pertenencia y el rol en origen, nunca por el Tenant activo.
        var tenantPropietarioId = delegacion.TenantClienteId;
        var tenantActivo = tenantActual.TenantId;
        if (tenantActivo is null
            || (tenantActivo != tenantPropietarioId
                && tenantActivo != await currentUserService.ObtenerTenantOrigenIdAsync()))
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.NoAutorizado",
                "Para reactivar el acceso, vuelve a tu organización."));

        // La operación y las carteras pertenecen al Tenant propietario, y quien reactiva lo
        // hace desde la casa del Operador CAE externo. TenantSelladoInterceptor solo realinea
        // la conexión durante cada SaveChanges; las lecturas de dentro de la transacción
        // (otro Operador CAE externo vigente, operación, carteras, principal) se hacen con el
        // Tenant firmado al abrirla. Por eso el ámbito del Tenant propietario se abre ANTES de
        // la transacción, no dentro: así la transacción entera ve lo mismo que veía cuando
        // reactivaba desde el propio Tenant propietario, y la comprobación de otro Operador CAE
        // externo vigente no queda ciega por RLS a los demás. No
        // amplía la autoridad, que ya se decidió arriba.
        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            return await transaccion.EjecutarAsync(
                ct => ReactivarAsync(delegacion.Id, tenantPropietarioId, ct), cancellationToken);
        }
    }

    private async Task<Result> ReactivarAsync(Guid id, Guid tenantPropietarioId, CancellationToken cancellationToken)
    {
        // Se vuelve a pedir dentro de la transacción porque un reintento de la estrategia de
        // ejecución empieza con el contexto limpio. En el primer intento el contexto ya rastrea la
        // fila leída arriba y devuelve esa misma instancia: no es una relectura del estado.
        // Lo que la autoridad miró (Operador CAE externo, Tenant propietario y propósito) no
        // cambia nunca en una DelegacionTenant (private set, solo al crearla).
        var delegacion = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
        if (delegacion is null || delegacion.TenantClienteId != tenantPropietarioId)
            return Result.Fallo(NoEncontrada);

        if (delegacion.Activa)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaActiva", "Esta delegación ya estaba activa."));

        // Misma regla que CrearDelegacionTenantCommand (un solo Operador CAE externo vigente): si,
        // entre revocar a este Operador CAE externo y reactivarlo, el Tenant
        // propietario autorizó a otro, reabrir aquí la operación completa chocaría
        // con el índice único IX_AsignacionesOperacion_DelegacionTotalVigente y
        // saldría como DbUpdateException sin traducir (hallazgo de Codex, P1, al
        // integrar origin/main en el incremento 1b). Se rechaza antes de escribir,
        // después de la autoridad y del estado (la autoridad ya exigió que sea de Operador CAE
        // externo). Con un error genérico (decisión del 2026-10-26): quien reactiva es el Operador
        // CAE externo, y no debe saber si el Tenant propietario trabaja con otro.
        if (await tenantsContext.DelegacionesTenant.AnyAsync(
                d => d.TenantClienteId == tenantPropietarioId
                     && d.Id != delegacion.Id
                     && d.Activa
                     && d.Proposito == PropositoDelegacion.OperadorExterno,
                cancellationToken))
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.NoReactivable",
                "No se puede reactivar esta Delegación; contacta con el Tenant propietario."));

        delegacion.Reactivar();

        // Append-only: no se reabre la operación cerrada, se abre una nueva.
        // El histórico conserva las dos etapas por separado, que es lo que
        // permite responder quién operaba en cada momento. Siempre es la de un Operador CAE
        // externo: la autoridad de arriba (PuedeReactivarAsync) ya lo exigió, y el propósito no
        // cambia nunca en una DelegacionTenant.
        var operacion = await asignacionesWriter.AbrirOperacionDelegadaAsync(
            tenantPropietarioId, delegacion.TenantConsultoraId,
            DateTime.UtcNow, vigenciaHasta: null, cancellationToken);

        // Y sus carteras. Desactivar las cerró en cascada pero no borró las
        // filas de operador delegado, así que reactivar sin esto dejaría una
        // operación vigente con cero carteras: el operador entraría al
        // workspace sin ver un solo dato hasta el siguiente arranque.
        var anteriorPrincipal = await asignacionesWriter.ReabrirCarterasDeOperadoresAsync(
            operacion, delegacion.Id, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // «Al reactivar vuelve a ser principal quien lo era antes» (D-9, 2026-10-08). Con las
        // carteras ya guardadas: la marca se enciende sobre la cartera repuesta, o se releva
        // al Coordinador CAE de esa persona si ya no puede llevarla.
        if (anteriorPrincipal is { } anteriorPrincipalId
            && !await RelevoDePrincipalDeCartera.RestaurarAlReactivarAsync(
                catalogo,
                new OperacionConPrincipal(operacion.PropietarioTenantId, operacion.Id),
                operacion.OperadorTenantId, anteriorPrincipalId,
                directorioDestinos, directorioUsuarios, bloqueoCartera, cancellationToken))
            return Result.Fallo(CambioMientrasReactivabas);

        return Result.Exito();
    }
}
