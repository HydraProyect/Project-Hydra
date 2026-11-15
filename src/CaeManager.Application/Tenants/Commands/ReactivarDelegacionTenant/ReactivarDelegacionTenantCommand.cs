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
/// Quién reactiva: solo el Administrador del Tenant propietario
/// (<see cref="AdministradorQueReactiva"/>, decisión SEG-7 del 2026-10-31), revocara quien
/// revocara. El Operador CAE externo nunca, aunque sea quien revoca.
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
    ITransaccionDeComando transaccion, ITenantActual tenantActual,
    IAutorizacionDelegacionTenant autorizacion)
    : IRequestHandler<ReactivarDelegacionTenantCommand, Result>,
      IRequestHandler<PuedeReactivarQuery, bool>
{
    /// <summary>
    /// Único punto de verdad — ver el doc-comment de <see cref="PuedeReactivarQuery"/>.
    ///
    /// <para>
    /// Solo la de un Operador CAE externo, y solo el Administrador del Tenant propietario
    /// (<see cref="AdministradorQueReactiva"/>). Una de Soporte no se reactiva por aquí:
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
    ///
    /// <para>
    /// Vigencia: quien recibe el acceso tiene que seguir siendo un Operador CAE externo elegible
    /// (<see cref="OperadorCaeExternoElegible"/>), el mismo requisito que al autorizarlo por
    /// primera vez. La fila revocada no prueba que lo siga siendo.
    /// </para>
    /// </summary>
    internal async Task<bool> PuedeReactivarAsync(
        Guid operadorTenantId, Guid tenantPropietarioId, bool esDeOperadorExterno,
        CancellationToken cancellationToken)
    {
        if (!esDeOperadorExterno
            || !await AdministradorQueReactiva.PuedeAsync(
                tenantPropietarioId, currentUserService, tenantActual, autorizacion, cancellationToken))
            return false;

        var esPlataforma = await tenantsContext.Tenants
            .Where(t => t.Id == tenantPropietarioId)
            .Select(t => t.EsPlataforma)
            .SingleOrDefaultAsync(cancellationToken);

        var operadorElegible = await tenantsContext.Tenants
            .Where(t => t.Id == operadorTenantId)
            .AnyAsync(OperadorCaeExternoElegible.Predicado, cancellationToken);

        return EstadoDeAccesoDelegado.PermiteReactivar(esDeOperadorExterno, esPlataforma, operadorElegible);
    }

    public async Task<bool> Handle(PuedeReactivarQuery request, CancellationToken cancellationToken)
    {
        var acceso = await EstadoDeAccesoDelegado.LeerAsync(tenantsContext, request.Id, cancellationToken);

        return acceso is { Activa: false }
               && await PuedeReactivarAsync(
                   acceso.OperadorTenantId, acceso.TenantPropietarioId, acceso.EsDeOperadorExterno, cancellationToken);
    }

    private static readonly Error NoEncontrada =
        Error.Crear("DelegacionTenant.NoEncontrada", "No encontramos esa delegación.");

    /// <summary>
    /// Autoridad y contexto primero, fuera de la transacción; después, en una transacción
    /// abierta con el Tenant propietario, la delegación reactivada y su operación nueva se
    /// guardan juntas.
    /// </summary>
    public async Task<Result> Handle(ReactivarDelegacionTenantCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var delegacion = await repositorio.ObtenerPorIdAsync(request.DelegacionTenantId, cancellationToken);

        // Reactivar RESTAURA capacidad —reabre la operación del Operador CAE externo, aunque sin
        // ninguna Asignación de Cartera—,
        // así que no se autoriza como revocar: es del Administrador del Tenant propietario, desde
        // su propia casa (decisión SEG-7 del 2026-10-31), y nunca del Operador CAE externo.
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

        var tenantPropietarioId = delegacion.TenantClienteId;

        // La operación y las carteras pertenecen al Tenant propietario, que la autoridad de
        // arriba ya exige que sea el Tenant activo. TenantSelladoInterceptor solo realinea la
        // conexión durante cada SaveChanges; las lecturas de dentro de la transacción (otro
        // Operador CAE externo vigente, operación, carteras, principal) se hacen con el Tenant
        // firmado al abrirla. Por eso el ámbito del Tenant propietario se abre ANTES de la
        // transacción, no dentro: la comprobación de otro Operador CAE externo vigente no queda
        // ciega por RLS a los demás. No amplía la autoridad, que ya se decidió arriba.
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
        // externo). Quien llega aquí es el Administrador del Tenant propietario: el vínculo es
        // suyo y el mensaje no le revela nada de terceros.
        if (await tenantsContext.DelegacionesTenant.AnyAsync(
                d => d.TenantClienteId == tenantPropietarioId
                     && d.Id != delegacion.Id
                     && d.Activa
                     && d.Proposito == PropositoDelegacion.OperadorExterno,
                cancellationToken))
            return Result.Fallo(Error.Crear(
                "DelegacionTenant.NoReactivable",
                "No se puede reactivar este acceso: tu organización ya tiene otro Operador CAE externo con acceso vigente."));

        delegacion.Reactivar();

        // La reactivación se guarda antes de abrir la operación, dentro de la misma transacción: el trigger
        // asignacion_operacion_sella_origen exige la delegación ya activa en la base y su contador real, y EF no ordena
        // el UPDATE de la delegación por delante del INSERT de la operación (ronda 2 de Codex en la PR #2237, alta 1).
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Append-only: no se reabre la operación cerrada, se abre una nueva.
        // El histórico conserva las dos etapas por separado, que es lo que
        // permite responder quién operaba en cada momento. Siempre es la de un Operador CAE
        // externo: la autoridad de arriba (PuedeReactivarAsync) ya lo exigió, y el propósito no
        // cambia nunca en una DelegacionTenant.
        var desde = DateTime.UtcNow;
        var operacion = await asignacionesWriter.AbrirOperacionDelegadaAsync(
            tenantPropietarioId, delegacion.TenantConsultoraId,
            desde, vigenciaHasta: null, cancellationToken);

        // La delegación se leyó inactiva dentro de esta transacción, así que ninguna operación vigente puede estar
        // ya amparada por ella: si el writer devuelve una existente (no la que nace con este «desde»), otra
        // reactivación simultánea ganó entre la lectura y el guardado de arriba, que esperó a su bloqueo de fila.
        // Esta pierde sin escribir: el fallo revierte la transacción, también su UPDATE de la delegación (ronda 3
        // de la PR #2237; sin los cerrojos consultivos ya no lo decidía la base).
        if (operacion.VigenciaDesde != desde)
            return Result.Fallo(Error.Crear("DelegacionTenant.YaActiva", "Esta delegación ya estaba activa."));

        // Sin carteras (decisión de Chris del 2026-11-14, opción A en la PR #2223): reactivar la
        // delegación no repone ninguna Asignación de Cartera, ni la de Consulta desde la fila
        // heredada ni las de Gestor CAE o Coordinador CAE que la revocación cerró, ni la marca de
        // principal (D-9 deja de aplicarse aquí). Cada cartera vuelve por un acto explícito
        // (incorporación aceptada, asignación en Usuarios, alta del operador delegado), y el
        // arranque tampoco la emite desde la fila heredada si la persona ya tuvo alguna en el par.
        // Antes se reponían las que había cerrado la cascada, y reconocerlas por fecha o por una
        // marca dejaba volver lo caducado o lo suspendido (rondas 1 a 3 de Codex).
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
