using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Incidencias.Commands.EliminarIncidencias;

/// <summary>Borrado en lote — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarIncidenciasCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarIncidenciasCommandValidator : AbstractValidator<EliminarIncidenciasCommand>
{
    public EliminarIncidenciasCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarIncidenciasCommandHandler(
    IIncidenciaRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<EliminarIncidenciasCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarIncidenciasCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(Error.Crear("Incidencia.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        // Asignación de Cartera: una Incidencia de un Centro fuera del alcance de gestión cuenta como
        // inexistente, con el mismo mensaje que una que ya no existía. Una sola consulta de alcance.
        var centrosDeGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var incidencia = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (incidencia is null || (centrosDeGestion is not null && !centrosDeGestion.Contains(incidencia.CentroId)))
            {
                errores.Add("Una incidencia ya no existía.");
                continue;
            }

            incidencia.MarcarComoEliminado(usuarioId.Value);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
