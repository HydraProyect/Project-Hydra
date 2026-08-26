using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Commands.RegistrarVerificacionExterna;

/// <summary>
/// Registra una comprobación manual del cumplimiento de una Subcontrata en la
/// plataforma del titular de un Centro (ADR-005 § 2.3). Siempre crea un
/// registro nuevo — las verificaciones son hechos, no un estado editable; el
/// semáforo lo deriva la consulta de supervisión de la última no eliminada.
/// </summary>
public record RegistrarVerificacionExternaSubcontrataCommand(
    Guid SubcontrataId,
    Guid CentroId,
    Guid TipoDocumentoId,
    DateOnly FechaVerificacion,
    ResultadoVerificacionExterna Resultado,
    DateOnly? ValidoHasta,
    string? Observaciones,
    byte[]? EvidenciaContenido = null,
    string? EvidenciaNombreArchivo = null) : ICommand;

public class RegistrarVerificacionExternaSubcontrataCommandValidator
    : AbstractValidator<RegistrarVerificacionExternaSubcontrataCommand>
{
    public const int TamanoMaximoEvidenciaBytes = 10 * 1024 * 1024;

    public RegistrarVerificacionExternaSubcontrataCommandValidator()
    {
        RuleFor(c => c.SubcontrataId).NotEmpty();
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.Resultado).IsInEnum();

        RuleFor(c => c.FechaVerificacion)
            .Must(f => f <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de verificación no puede ser futura.");

        RuleFor(c => c.Observaciones)
            .MaximumLength(VerificacionExternaSubcontrata.LongitudMaximaObservaciones)
            .WithMessage($"Las observaciones no pueden superar {VerificacionExternaSubcontrata.LongitudMaximaObservaciones} caracteres.");

        RuleFor(c => c.EvidenciaContenido)
            .Must(e => e is null or { Length: > 0 and <= TamanoMaximoEvidenciaBytes })
            .WithMessage("La evidencia no puede estar vacía ni superar los 10 MB.");

        RuleFor(c => c.EvidenciaNombreArchivo)
            .NotEmpty()
            .When(c => c.EvidenciaContenido is not null)
            .WithMessage("La evidencia necesita el nombre del archivo.");
    }
}

public class RegistrarVerificacionExternaSubcontrataCommandHandler(
    IEmpresaRepository subcontrataRepositorio,
    IVerificacionExternaSubcontrataRepository verificacionRepositorio,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IFileStorageService fileStorage,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarVerificacionExternaSubcontrataCommand, Result>
{
    public async Task<Result> Handle(
        RegistrarVerificacionExternaSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await subcontrataRepositorio.ObtenerPorIdAsync(request.SubcontrataId, cancellationToken);
        if (subcontrata is null || !await alcanceDatos.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        // Ids ajenos bajo el filtro de tenant = no encontrados (regla global del repo).
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("VerificacionExterna.CentroNoEncontrado", "No encontramos este centro."));

        if (!await tiposDocumentoContext.TiposDocumento.AnyAsync(t => t.Id == request.TipoDocumentoId, cancellationToken))
            return Result.Fallo(Error.Crear("VerificacionExterna.TipoNoEncontrado", "No encontramos este tipo de documento."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("VerificacionExterna.SinUsuario", "No pudimos identificar al usuario verificador."));

        var verificacion = new VerificacionExternaSubcontrata(
            request.SubcontrataId,
            request.CentroId,
            request.TipoDocumentoId,
            request.FechaVerificacion,
            request.Resultado,
            usuarioId.Value,
            request.ValidoHasta,
            request.Observaciones);

        if (request.EvidenciaContenido is not null)
        {
            using var contenido = new MemoryStream(request.EvidenciaContenido);
            var ruta = await fileStorage.GuardarAsync(contenido, request.EvidenciaNombreArchivo!, cancellationToken);
            verificacion.AdjuntarEvidencia(ruta, request.EvidenciaNombreArchivo!);
        }

        verificacionRepositorio.Agregar(verificacion);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
