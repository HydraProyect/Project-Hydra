using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.EliminarDocumentos;

/// <summary>Borrado en lote (P3-31) — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarDocumentosCommand(IReadOnlyList<Guid> Ids, Guid UsuarioId) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarDocumentosCommandValidator : AbstractValidator<EliminarDocumentosCommand>
{
    public EliminarDocumentosCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarDocumentosCommandHandler(
    IDocumentoRepository repositorio, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarDocumentosCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarDocumentosCommand request, CancellationToken cancellationToken)
    {
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

            documento.MarcarComoEliminado(request.UsuarioId);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
