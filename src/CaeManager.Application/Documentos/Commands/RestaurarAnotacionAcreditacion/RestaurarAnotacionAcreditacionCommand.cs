using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;

/// <summary>
/// «Deshacer» tras anotar la vigencia de un Documento en una plataforma (ficha 09).
/// Restaurar es una escritura, con estas garantías:
/// <list type="bullet">
/// <item>Misma autorización y alcance de datos que las dos anotaciones que deshace.</item>
/// <item>Devuelve los valores exactos previos: el estado y la vigencia, incluido
/// «Sin confirmar», que no equivale a una vigencia anotada.</item>
/// <item>Rechaza si la acreditación cambió desde la anotación (la versión es obligatoria y
/// se compara): otra persona o el propio Gestor CAE ya la tocaron, y deshacer pisaría ese cambio.</item>
/// <item>Idempotente: si ya está en los valores previos, no escribe ni audita de nuevo.</item>
/// <item>La auditoría registra una fila propia «Restaurado» (ver
/// <c>IAccionAuditoriaPropia</c>) con Actor real separado del Usuario simulado.</item>
/// </list>
/// </summary>
public record RestaurarAnotacionAcreditacionCommand(
    Guid AcreditacionId, EstadoAcreditacion EstadoPrevio, VigenciaEnPlataforma VigenciaPrevia, Guid VersionEsperada) : ICommand;

public class RestaurarAnotacionAcreditacionCommandValidator : AbstractValidator<RestaurarAnotacionAcreditacionCommand>
{
    public RestaurarAnotacionAcreditacionCommandValidator()
    {
        RuleFor(c => c.AcreditacionId).NotEmpty();
        RuleFor(c => c.EstadoPrevio).IsInEnum();
        // Guid.Empty en ConcurrenciaOptimista significa «sin comprobación»: aquí no vale.
        RuleFor(c => c.VersionEsperada).NotEmpty();
    }
}

public class RestaurarAnotacionAcreditacionCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarAnotacionAcreditacionCommand, Result>
{
    public async Task<Result> Handle(RestaurarAnotacionAcreditacionCommand request, CancellationToken cancellationToken)
    {
        var noEncontrada = Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación.");

        var acreditacion = await acreditacionRepositorio.ObtenerPorIdAsync(request.AcreditacionId, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(noEncontrada);

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(noEncontrada);

        if (acreditacion.Estado == request.EstadoPrevio && acreditacion.Vigencia == request.VigenciaPrevia)
            return Result.Exito();

        if (ConcurrenciaOptimista.Verificar(acreditacion, request.VersionEsperada, "esta acreditación") is not null)
            return Result.Fallo(Error.Crear(
                ConcurrenciaOptimista.CodigoConflicto,
                "No se puede deshacer: esta acreditación cambió después de anotarla. Abre el documento y corrige la vigencia a mano."));

        acreditacion.Restaurar(request.EstadoPrevio, request.VigenciaPrevia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
