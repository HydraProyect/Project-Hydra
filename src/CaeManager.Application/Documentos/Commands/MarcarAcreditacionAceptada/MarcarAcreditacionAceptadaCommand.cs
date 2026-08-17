using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;

/// <summary>"Marcar aceptado" del drill-down por plataforma — ver MarcarAcreditacionSubidaCommand, mismo motivo, misma forma.</summary>
public record MarcarAcreditacionAceptadaCommand(Guid AcreditacionId) : ICommand;

public class MarcarAcreditacionAceptadaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionAceptadaCommand, Result>
{
    public async Task<Result> Handle(MarcarAcreditacionAceptadaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await acreditacionRepositorio.ObtenerPorIdAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        acreditacion.MarcarAceptada();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
