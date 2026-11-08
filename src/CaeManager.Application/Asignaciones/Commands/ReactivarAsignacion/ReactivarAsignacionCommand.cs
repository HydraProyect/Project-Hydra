using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;

/// <summary>
/// Reabre una asignación dada de baja: vuelve a dejar <c>FechaBaja</c> vacía
/// y conserva su <c>FechaAlta</c>. Es el «Deshacer» del aviso de baja
/// (FS-13, auditoría UX de flujos sin salida, 2026-09-24): antes, una baja
/// equivocada solo se corregía con un alta nueva, que partía la historia del
/// trabajador en ese centro en dos filas.
/// </summary>
public record ReactivarAsignacionCommand(Guid Id) : ICommand;

public class ReactivarAsignacionCommandValidator : AbstractValidator<ReactivarAsignacionCommand>
{
    public ReactivarAsignacionCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class ReactivarAsignacionCommandHandler(
    IAsignacionRepository repositorio, IAutoridadAsignacionesService autoridad,
    IAltaAcreditacionesPlataformaService altaAcreditaciones, IUnitOfWork unitOfWork,
    ISerializacionAltasAcreditacionEmpresa serializacionAltas)
    : IRequestHandler<ReactivarAsignacionCommand, Result>
{
    public async Task<Result> Handle(ReactivarAsignacionCommand request, CancellationToken cancellationToken)
    {
        var noEncontrada = Error.Crear("Asignacion.NoEncontrada", "No encontramos esta asignación.");

        var asignacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (asignacion is null)
            return Result.Fallo(noEncontrada);

        // Reabrir es volver a dar de alta, así que exige la autoridad del alta
        // (CrearAsignacionCommand): sobre el trabajador y sobre el centro, no
        // solo sobre el centro como la baja. Sin la del trabajador, reabrir una
        // asignación vieja devolvería a la cartera del actor a un trabajador
        // que ya no está en ella. Mismo error que «no encontrada»: distinguirlo
        // confirmaría la existencia de una asignación ajena.
        if (!await autoridad.PuedeModificarAsignacionesDelTrabajadorAsync(asignacion.TrabajadorId, cancellationToken)
            || !await autoridad.PuedeModificarAsignacionesDelCentroAsync(asignacion.CentroId, cancellationToken))
            return Result.Fallo(noEncontrada);

        // Reabierta, vuelve a estar de alta en la plataforma del Centro, igual
        // que tras CrearAsignacionCommand: lo que el Centro empezó a exigir, o
        // lo que se subió, mientras estaba cerrada nace pendiente de acreditar.
        // Idempotente: lo ya acreditado antes de la baja no se duplica. Bajo la
        // misma serialización que CrearAsignacionCommand (revisión Codex de la
        // PR #2074, ronda 3, hallazgo 4). Fuera queda solo la autoridad, que
        // depende del Trabajador y del Centro, que una Asignación no cambia.
        return await serializacionAltas.EjecutarAsync(async ct =>
        {
            // Bajo el candado, lo confirmado en la base: la de arriba, o la que el contexto (uno por circuito)
            // rastreara antes, puede ser la de antes de esperar. Con ella, una baja o una reapertura ajenas
            // confirmadas mientras tanto no se verían (revisión Codex de la PR #2074, ronda 4). En el reintento, que
            // empieza con el contexto vacío, la lectura consulta la base igualmente.
            var vigente = await repositorio.ObtenerPorIdActualizadoAsync(asignacion.Id, ct);
            if (vigente is null)
                return Result.Fallo(noEncontrada);
            if (vigente.EstaActiva)
                return Result.Fallo(Error.Crear("Asignacion.YaActiva", "Esta asignación ya está activa."));

            if (await repositorio.ExisteActivaAsync(vigente.TrabajadorId, vigente.CentroId, ct))
                return Result.Fallo(Error.Crear(
                    "Asignacion.YaActiva", "Este trabajador ya está dado de alta en este centro."));

            // DEC-19: reabierta, la asignación ocupa [FechaAlta, ∞). Si otra fila
            // del mismo trío cae en ese rango —una alta posterior ya cerrada—, se
            // rechaza igual que en el alta. La propia fila no cuenta: su rango
            // cerrado está dentro del que se comprueba. Bajo el candado, como el
            // alta: otra alta o reapertura del mismo trío confirmada mientras esta
            // esperaba se ve.
            if (await repositorio.ExisteSolapeConOtraAsync(
                    vigente.Id, vigente.TrabajadorId, vigente.CentroId, vigente.FechaAlta, null, ct))
                return Result.Fallo(Error.Crear(
                    "Asignacion.SolapaConOtra",
                    "Este trabajador tiene otra asignación a este centro posterior a esta; no se puede reabrir."));

            vigente.ReactivarAlta();
            await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Asignaciones = [vigente] }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }
}
