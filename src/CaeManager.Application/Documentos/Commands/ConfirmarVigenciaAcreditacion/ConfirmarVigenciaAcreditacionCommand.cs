using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;

/// <summary>
/// Anota hasta cuándo vale un documento en una plataforma, sin tocar el estado
/// de la acreditación.
///
/// <para>
/// Hace falta porque confirmar la vigencia al aceptar no basta. El Gestor CAE
/// puede aceptar sin saber la fecha —«todavía no lo sé» es una respuesta
/// válida—, y sobre todo: la migración deja SIN CONFIRMAR todas las
/// acreditaciones que ya existían, incluidas las aceptadas hace meses. Sin este
/// comando ese estado sería un callejón sin salida, con la vigencia de medio
/// sistema imposible de completar desde el producto.
/// </para>
/// </summary>
public record ConfirmarVigenciaAcreditacionCommand(Guid AcreditacionId, VigenciaEnPlataforma Vigencia) : ICommand;

public class ConfirmarVigenciaAcreditacionCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarVigenciaAcreditacionCommand, Result>
{
    public async Task<Result> Handle(ConfirmarVigenciaAcreditacionCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await acreditacionRepositorio.ObtenerPorIdAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        // Misma comprobación de alcance que el resto de comandos de
        // acreditación: la vigencia en una plataforma es un dato del Documento,
        // y quien no puede ver el Documento tampoco puede decidir hasta cuándo
        // vale.
        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        acreditacion.ConfirmarVigencia(request.Vigencia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
