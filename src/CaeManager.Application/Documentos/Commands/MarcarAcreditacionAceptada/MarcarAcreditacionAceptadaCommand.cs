using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;

/// <summary>
/// "Marcar aceptado" del drill-down por plataforma — ver
/// MarcarAcreditacionSubidaCommand, mismo motivo, misma forma.
///
/// <para>
/// Lleva la vigencia porque aceptar es el momento en que el Gestor CAE la sabe:
/// acaba de mirar la plataforma. El tipo <see cref="VigenciaEnPlataforma"/> no
/// admite una combinación incoherente, así que el handler no tiene nada que
/// validar aquí; si el Gestor no la sabe, viaja SinConfirmar, que es un dato y
/// no un hueco.
/// </para>
/// </summary>
public record MarcarAcreditacionAceptadaCommand(Guid AcreditacionId, VigenciaEnPlataforma Vigencia) : ICommand;

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

        acreditacion.MarcarAceptada(request.Vigencia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
