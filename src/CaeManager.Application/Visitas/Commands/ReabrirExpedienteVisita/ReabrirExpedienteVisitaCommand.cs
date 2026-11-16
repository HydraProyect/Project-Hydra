using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Visitas.Commands.ReabrirExpedienteVisita;

/// <summary>
/// D-4 (#2005, decisión del 2026-11-15, opción A + (i)): el Gestor CAE revisa y reabre el expediente sellado de una
/// Visita. Reabrir retira el sello (<see cref="Visita.ReabrirExpediente"/>) y reevalúa al momento con la regla de
/// bloqueo común; si sigue completo, se vuelve a sellar con una atribución nueva.
/// <list type="bullet">
/// <item>Quién: <b>solo</b> el Gestor CAE (rol efectivo <c>GestorCae</c>) con Asignación de Cartera sobre el Centro de
/// la Visita (<see cref="AutorizacionReaperturaExpediente"/>). Ni el Administrador del Tenant propietario, ni Dirección
/// CAE, ni Coordinador CAE: su alcance de gestión no es una Asignación de Cartera.</item>
/// <item>Soporte TALVEG no es Gestor CAE: sin Sesión Privilegiada no tiene rol en el Tenant, y bajo una Sesión
/// Privilegiada <c>AutorizacionEscrituraBehavior</c> deniega toda escritura que no sea de Aprovisionamiento. El camino
/// de escritura de la impersonación (autorizar con los planos del Usuario simulado, ADR-011 § 4bis.2) no existe todavía;
/// cuando exista, esta orden lo heredará con el rol efectivo del Usuario simulado.</item>
/// <item>Auditoría: motivo obligatorio y fecha en la Visita. La reapertura y el resello, si lo hay, son dos guardados
/// dentro de una única transacción (<see cref="ITransaccionDeComando"/>, Codex r3 de la #2266): el registro de la
/// reapertura lleva en «antes» el sello retirado completo (fecha, antelaciones nominal y efectiva, tramo y atribución,
/// que <c>AuditoriaInterceptor</c> audita juntos) y el del resello lleva el sello nuevo. Los dos separan el Actor real
/// del Usuario simulado. Si la reevaluación falla, se deshacen los dos.</item>
/// <item>Concurrencia: la versión que el Gestor CAE vio es obligatoria, como en <c>ReactivarVisitaCommand</c>.</item>
/// </list>
/// </summary>
[AreaDelEncargo("Visitas")]
public record ReabrirExpedienteVisitaCommand(Guid Id, Guid VersionEsperada, string Motivo) : ICommand;

public class ReabrirExpedienteVisitaCommandValidator : AbstractValidator<ReabrirExpedienteVisitaCommand>
{
    public ReabrirExpedienteVisitaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        // Guid.Empty en ConcurrenciaOptimista significa «sin comprobación»: aquí no vale.
        RuleFor(c => c.VersionEsperada).NotEmpty();
        RuleFor(c => c.Motivo)
            .NotEmpty().WithMessage("Indica el motivo de la reapertura.")
            .MaximumLength(Visita.LongitudMaximaMotivo)
            .WithMessage($"El motivo no puede superar los {Visita.LongitudMaximaMotivo} caracteres.");
    }
}

/// <summary>
/// Punto único de autorización de la reapertura del expediente de una Visita (D-4). Rol efectivo Gestor CAE y el Centro
/// dentro de su Asignación de Cartera. El rol se pregunta explícitamente porque <see cref="IAlcanceDatosService"/>
/// devuelve «sin restricción» al Administrador y a Dirección CAE: el alcance solo no excluiría al Administrador sin cartera.
/// </summary>
public static class AutorizacionReaperturaExpediente
{
    public const string RolQueReabre = "GestorCae";

    public static Error NoEncontrada => AutorizacionCancelacionVisita.NoEncontrada;

    public static Error SoloGestorCae => Error.Crear(
        "Visita.ReaperturaSoloGestorCae",
        "Solo el Gestor CAE con el Centro de la visita en su cartera puede reabrir el expediente.");

    /// <summary>null si puede reabrir; si no, el error que se devuelve.</summary>
    public static async Task<Error?> ComprobarAsync(
        ICurrentUserService currentUser, IAlcanceDatosService alcanceDatos, Visita visita, CancellationToken cancellationToken)
    {
        // Fuera del alcance de gestión se responde igual que «no existe», sin revelar qué hay fuera.
        if (!await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return NoEncontrada;

        if (await currentUser.ObtenerRolEfectivoAsync() != RolQueReabre)
            return SoloGestorCae;

        // Defensa en profundidad: el Gestor CAE siempre tiene alcance acotado. Uno «sin restricción» no es una
        // Asignación de Cartera, así que no basta.
        var cartera = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        return cartera is not null && cartera.Contains(visita.CentroId) ? null : SoloGestorCae;
    }
}

public class ReabrirExpedienteVisitaCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, ITransaccionDeComando transaccion, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser, IEvaluadorExpedienteVisitaService evaluadorExpediente,
    ILogger<ReabrirExpedienteVisitaCommandHandler> logger)
    : IRequestHandler<ReabrirExpedienteVisitaCommand, Result>
{
    public async Task<Result> Handle(ReabrirExpedienteVisitaCommand request, CancellationToken cancellationToken)
    {
        if (request.VersionEsperada == Guid.Empty)
            return Result.Fallo(Error.Crear("Visita.VersionRequerida", "Falta la versión de la visita que se vio."));
        if (string.IsNullOrWhiteSpace(request.Motivo))
            return Result.Fallo(Error.Crear("Visita.MotivoReaperturaRequerido", "Indica el motivo de la reapertura."));

        // La transacción puede reintentar la operación entera: todo lo que se lee y autoriza va dentro.
        return await transaccion.EjecutarAsync(ct => ReabrirAsync(request, ct), cancellationToken);
    }

    private async Task<Result> ReabrirAsync(ReabrirExpedienteVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdActualizadoAsync(request.Id, cancellationToken);
        if (visita is null)
            return Result.Fallo(AutorizacionReaperturaExpediente.NoEncontrada);

        if (await AutorizacionReaperturaExpediente.ComprobarAsync(currentUser, alcanceDatos, visita, cancellationToken) is { } denegado)
            return Result.Fallo(denegado);

        if (ConcurrenciaOptimista.Verificar(visita, request.VersionEsperada, "esta visita") is not null)
            return Result.Fallo(Error.Crear(
                ConcurrenciaOptimista.CodigoConflicto,
                "Esta visita cambió desde que la viste: otra persona la modificó. Hemos recargado la visita; revísala y reabre el expediente de nuevo si sigue sellado."));

        if (visita.FechaHoraExpedienteCompletoUtc is null)
            return Result.Fallo(Error.Crear("Visita.ExpedienteNoSellado", "El expediente de esta visita no está sellado: no hay nada que reabrir."));
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada: reactívala antes de reabrir su expediente."));

        // Solo antes de que la Visita empiece (revisor-puente, #2266): resellar con «ahora» una Visita en curso o ya
        // ocurrida daría una antelación efectiva negativa, tramo Exprés y una atribución imputada al Cliente empresarial
        // que no describe lo que pasó, y movería el sello de un mes ya cerrado a los KPI del mes en curso.
        if (visita.FechaInicio <= DiaDeNegocio.Hoy())
            return Result.Fallo(Error.Crear(
                "Visita.ReaperturaFueraDePlazo",
                "Esta visita ya ha empezado o ha pasado: su expediente solo se puede reabrir antes del día de la visita."));

        // Primer guardado: la reapertura, con su propio registro de auditoría (el sello retirado entero en «antes»).
        visita.ReabrirExpediente(DateTime.UtcNow, request.Motivo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Reevaluar al momento con la regla común (opción A), en la misma transacción. El evaluador recibe esta misma
        // instancia rastreada (el repositorio resuelve la identidad sin recargarla) y, si sella, guarda él: segundo
        // registro de auditoría, con el sello nuevo. Si la reevaluación falla, lanza o se cancela, la transacción
        // deshace también la reapertura y la Visita sigue sellada como estaba: nunca queda sin sello por un fallo
        // transitorio (Codex r2 y r3, #2266).
        try
        {
            await evaluadorExpediente.EvaluarAsync(visita.Id, cancellationToken);
        }
        // Un conflicto de concurrencia en el guardado del evaluador sigue su camino común (ConcurrenciaBehavior: «esta
        // visita cambió», con recarga), igual que si lo hubiera guardado este handler.
        catch (Exception ex) when (ex is not OperationCanceledException and not DbUpdateConcurrencyException)
        {
            logger.LogWarning(ex, "No se pudo reevaluar el expediente de la visita {VisitaId}; la reapertura no se guarda.", visita.Id);
            return Result.Fallo(Error.Crear(
                "Visita.ReaperturaNoEvaluada",
                "No se pudo reevaluar el expediente ahora mismo, así que no se ha reabierto. Inténtalo de nuevo."));
        }

        return Result.Exito();
    }
}
