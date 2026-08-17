using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;

/// <summary>
/// "Marcar subido" del drill-down por plataforma (mockup Documentos TALVEG,
/// pestaña Plataforma — cierra el hallazgo P-04): el domino ya sabía pasar
/// una AcreditacionDocumentoPlataforma a Subida (Lote 2-C), pero ningún
/// Command de Application la disparaba todavía (Lote 2-D, "todavía sin
/// construir" según el propio comentario de la entidad).
/// </summary>
public record MarcarAcreditacionSubidaCommand(Guid AcreditacionId) : ICommand;

public class MarcarAcreditacionSubidaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionSubidaCommand, Result>
{
    public async Task<Result> Handle(MarcarAcreditacionSubidaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await acreditacionRepositorio.ObtenerPorIdAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        acreditacion.MarcarSubida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
