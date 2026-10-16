using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EliminarDocumentacionRequeridaCentro;

/// <summary>
/// Quita la posición explícita de un Centro sobre un TipoDocumento — vuelve a
/// seguir el criterio global de <c>TipoDocumento.EsObligatorio</c>
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4). Baja física, sin ciclo de vida propio, mismo
/// criterio que el resto de altas/bajas de <see cref="TipoDocumentoCentro"/>.
/// </summary>
public record EliminarDocumentacionRequeridaCentroCommand(Guid CentroId, Guid TipoDocumentoId) : ICommand;

public class EliminarDocumentacionRequeridaCentroCommandValidator : AbstractValidator<EliminarDocumentacionRequeridaCentroCommand>
{
    public EliminarDocumentacionRequeridaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
    }
}

public class EliminarDocumentacionRequeridaCentroCommandHandler(
    ITipoDocumentoCentroRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarDocumentacionRequeridaCentroCommand, Result>
{
    public async Task<Result> Handle(EliminarDocumentacionRequeridaCentroCommand request, CancellationToken cancellationToken)
    {
        // Asignación de Cartera: la Documentación requerida es configuración DEL Centro; fuera del
        // alcance de gestión el Centro «no existe» (mismo criterio que EditarCentroCommand).
        if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.CentroNoEncontrado", "No encontramos este centro."));

        var fila = await repositorio.ObtenerPorParAsync(request.TipoDocumentoId, request.CentroId, cancellationToken);
        if (fila is null)
            return Result.Exito();

        repositorio.Eliminar(fila);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
