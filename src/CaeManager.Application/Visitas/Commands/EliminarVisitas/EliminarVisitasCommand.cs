using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.EliminarVisitas;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarVisitasCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarVisitasCommandValidator : AbstractValidator<EliminarVisitasCommand>
{
    public EliminarVisitasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarVisitasCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<EliminarVisitasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarVisitasCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(Error.Crear("Visita.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        // Alcance de GESTIÓN sobre el Centro de cada Visita, como en
        // EliminarVisitaCommand; se resuelve una vez para todo el lote. Una Visita
        // fuera de la Asignación de Cartera cuenta como una que ya no existía, sin
        // revelar qué hay fuera del alcance. null = sin restricción.
        var centroIdsParaGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);

        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var visita = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (visita is null || (centroIdsParaGestion is not null && !centroIdsParaGestion.Contains(visita.CentroId)))
            {
                errores.Add("Una visita ya no existía.");
                continue;
            }

            visita.MarcarComoEliminado(usuarioId.Value);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
