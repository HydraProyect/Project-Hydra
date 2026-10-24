using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.ActualizarVerificacionExterna;

/// <summary>
/// Corrige una verificación externa ya registrada: fecha, resultado, «válido hasta» y observaciones, y
/// opcionalmente sustituye la evidencia. El Centro y el tipo documental no se tocan (ver
/// <see cref="VerificacionExternaSubcontrata.Actualizar"/>). Sin evidencia nueva se conserva la que había.
/// La evidencia sustituida no se borra del almacenamiento, igual que la de una verificación eliminada.
/// </summary>
public record ActualizarVerificacionExternaSubcontrataCommand(
    Guid Id,
    DateOnly FechaVerificacion,
    ResultadoVerificacionExterna Resultado,
    DateOnly? ValidoHasta,
    string? Observaciones,
    byte[]? EvidenciaContenido = null,
    string? EvidenciaNombreArchivo = null) : ICommand;

public class ActualizarVerificacionExternaSubcontrataCommandValidator
    : AbstractValidator<ActualizarVerificacionExternaSubcontrataCommand>
{
    public ActualizarVerificacionExternaSubcontrataCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Resultado).IsInEnum();

        RuleFor(c => c.FechaVerificacion)
            .Must(f => f <= DiaDeNegocio.Hoy())
            .WithMessage("La fecha de verificación no puede ser futura.");

        RuleFor(c => c.Observaciones)
            .MaximumLength(VerificacionExternaSubcontrata.LongitudMaximaObservaciones)
            .WithMessage($"Las observaciones no pueden superar {VerificacionExternaSubcontrata.LongitudMaximaObservaciones} caracteres.");

        RuleFor(c => c.EvidenciaContenido)
            .Must(e => e is null or { Length: > 0 and <= LimitesArchivoSubido.TamanoMaximoBytes })
            .WithMessage($"La evidencia no puede estar vacía ni superar los {LimitesArchivoSubido.TamanoMaximoMb} MB.");

        RuleFor(c => c.EvidenciaNombreArchivo)
            .NotEmpty()
            .When(c => c.EvidenciaContenido is not null)
            .WithMessage("La evidencia necesita el nombre del archivo.");
    }
}

public class ActualizarVerificacionExternaSubcontrataCommandHandler(
    IVerificacionExternaSubcontrataRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IFileStorageService fileStorage,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarVerificacionExternaSubcontrataCommand, Result>
{
    public async Task<Result> Handle(
        ActualizarVerificacionExternaSubcontrataCommand request, CancellationToken cancellationToken)
    {
        // La verificación es de la Subcontrata EN un Centro: hacen falta las dos gestiones, igual que para
        // eliminarla, y la misma respuesta que «no existe».
        var verificacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (verificacion is null
            || !await alcanceDatos.SubcontrataParaGestionVisibleAsync(verificacion.SubcontrataId, cancellationToken)
            || !await alcanceDatos.CentroParaGestionVisibleAsync(verificacion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("VerificacionExterna.NoEncontrada", "No encontramos esta verificación."));

        if (request.EvidenciaContenido is not null)
        {
            var validacion = ValidacionEvidenciaVerificacionExterna.Validar(request.EvidenciaContenido, request.EvidenciaNombreArchivo);
            if (validacion.EsFallido)
                return validacion;
        }

        verificacion.Actualizar(request.FechaVerificacion, request.Resultado, request.ValidoHasta, request.Observaciones);

        if (request.EvidenciaContenido is not null)
        {
            using var contenido = new MemoryStream(request.EvidenciaContenido);
            var ruta = await fileStorage.GuardarAsync(contenido, request.EvidenciaNombreArchivo!, cancellationToken);
            verificacion.AdjuntarEvidencia(ruta, request.EvidenciaNombreArchivo!);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
