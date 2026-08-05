using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;

/// <summary>
/// Versión en lote de <see cref="DarDeBajaAsignacion.DarDeBajaAsignacionCommand"/>
/// — la usa "Dar de baja seleccionados" en el acordeón de asignaciones de
/// <c>/centros</c> (Centro 360, <c>PLAN-EJECUCION-UX.md</c> § 0.1). No es un
/// borrado (ver el comentario del Command singular): fija
/// <c>FechaBaja</c>, conserva el historial. Por eso el DTO de resultado no
/// reutiliza <c>ResultadoEliminacionLoteDto</c> — esa palabra es de
/// Eliminar/soft-delete, y una Asignación nunca se elimina.
/// </summary>
public record DarDeBajaAsignacionesCommand(IReadOnlyList<Guid> Ids, DateOnly FechaBaja) : ICommand<ResultadoBajaLoteDto>;

public record ResultadoBajaLoteDto(int DadasDeBaja, IReadOnlyList<string> Errores);

public class DarDeBajaAsignacionesCommandValidator : AbstractValidator<DarDeBajaAsignacionesCommand>
{
    public DarDeBajaAsignacionesCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class DarDeBajaAsignacionesCommandHandler(IAsignacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<DarDeBajaAsignacionesCommand, Result<ResultadoBajaLoteDto>>
{
    public async Task<Result<ResultadoBajaLoteDto>> Handle(DarDeBajaAsignacionesCommand request, CancellationToken cancellationToken)
    {
        var dadasDeBaja = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var asignacion = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (asignacion is null)
            {
                errores.Add("Una asignación ya no existía.");
                continue;
            }

            try
            {
                asignacion.DarDeBaja(request.FechaBaja);
                dadasDeBaja++;
            }
            catch (ArgumentException ex)
            {
                errores.Add(ex.Message);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoBajaLoteDto(dadasDeBaja, errores));
    }
}
