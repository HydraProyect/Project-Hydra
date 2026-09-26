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
    IAltaAcreditacionesPlataformaService altaAcreditaciones, IUnitOfWork unitOfWork)
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

        if (asignacion.EstaActiva)
            return Result.Fallo(Error.Crear("Asignacion.YaActiva", "Esta asignación ya está activa."));

        if (await repositorio.ExisteActivaAsync(asignacion.TrabajadorId, asignacion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Asignacion.YaActiva", "Este trabajador ya está dado de alta en este centro."));

        // DEC-19: reabierta, la asignación ocupa [FechaAlta, ∞). Si otra fila
        // del mismo trío cae en ese rango —una alta posterior ya cerrada—, se
        // rechaza igual que en el alta. La propia fila no cuenta: su rango
        // cerrado está dentro del que se comprueba.
        if (await repositorio.ExisteSolapeConOtraAsync(
                asignacion.Id, asignacion.TrabajadorId, asignacion.CentroId, asignacion.FechaAlta, null, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Asignacion.SolapaConOtra",
                "Este trabajador tiene otra asignación a este centro posterior a esta; no se puede reabrir."));

        asignacion.ReactivarAlta();

        // Reabierta, vuelve a estar de alta en la plataforma del Centro, igual
        // que tras CrearAsignacionCommand: lo que el Centro empezó a exigir, o
        // lo que se subió, mientras estaba cerrada nace pendiente de acreditar.
        // Idempotente: lo ya acreditado antes de la baja no se duplica.
        await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Asignaciones = [asignacion] }, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
