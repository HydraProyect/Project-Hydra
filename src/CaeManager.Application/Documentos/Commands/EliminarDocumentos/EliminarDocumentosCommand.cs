using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.EliminarDocumentos;

/// <summary>Borrado en lote (P3-31) — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarDocumentosCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarDocumentosCommandValidator : AbstractValidator<EliminarDocumentosCommand>
{
    public EliminarDocumentosCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarDocumentosCommandHandler(
    IDocumentoRepository repositorio, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<EliminarDocumentosCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarDocumentosCommand request, CancellationToken cancellationToken)
    {
        // Ver EliminarDocumentoCommand: la identidad no viaja en el contrato.
        // Se comprueba antes del bucle porque sin ella no hay ni un borrado
        // que atribuir, y un éxito parcial sin autor no es un éxito parcial.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(
                Error.Crear("Documento.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var documento = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            {
                errores.Add("Un documento ya no existía.");
                continue;
            }

            documento.MarcarComoEliminado(usuarioId.Value);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
