using CaeManager.Application.Common;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Blindaje42.Commands.RegistrarRespuestaCertificacionTgss;

/// <summary>Registra la respuesta de la TGSS a una solicitud ya hecha — o su justificante, si llega en papel/PDF.</summary>
public record RegistrarRespuestaCertificacionTgssCommand(
    Guid SolicitudId,
    ResultadoCertificacionTgss Resultado,
    DateOnly FechaRespuesta,
    byte[]? EvidenciaContenido = null,
    string? EvidenciaNombreArchivo = null) : ICommand;

public class RegistrarRespuestaCertificacionTgssCommandValidator
    : AbstractValidator<RegistrarRespuestaCertificacionTgssCommand>
{
    public const int TamanoMaximoEvidenciaBytes = 10 * 1024 * 1024;

    public RegistrarRespuestaCertificacionTgssCommandValidator()
    {
        RuleFor(c => c.SolicitudId).NotEmpty();
        RuleFor(c => c.Resultado).IsInEnum();

        RuleFor(c => c.FechaRespuesta)
            .Must(f => f <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de respuesta no puede ser futura.");

        RuleFor(c => c.EvidenciaContenido)
            .Must(e => e is null or { Length: > 0 and <= TamanoMaximoEvidenciaBytes })
            .WithMessage("La evidencia no puede estar vacía ni superar los 10 MB.");

        RuleFor(c => c.EvidenciaNombreArchivo)
            .NotEmpty()
            .When(c => c.EvidenciaContenido is not null)
            .WithMessage("La evidencia necesita el nombre del archivo.");
    }
}

public class RegistrarRespuestaCertificacionTgssCommandHandler(
    ISolicitudCertificacionTgssRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IFileStorageService fileStorage,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RegistrarRespuestaCertificacionTgssCommand, Result>
{
    public async Task<Result> Handle(RegistrarRespuestaCertificacionTgssCommand request, CancellationToken cancellationToken)
    {
        var solicitud = await repositorio.ObtenerPorIdAsync(request.SolicitudId, cancellationToken);
        if (solicitud is null || !await alcanceDatos.ClienteVisibleAsync(solicitud.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("CertificacionTgss.NoEncontrada", "No encontramos esta solicitud."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("CertificacionTgss.SinUsuario", "No pudimos identificar al usuario."));

        try
        {
            solicitud.RegistrarRespuesta(request.Resultado, request.FechaRespuesta, usuarioId.Value);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(Error.Crear("CertificacionTgss.YaRespondida", ex.Message));
        }

        if (request.EvidenciaContenido is not null)
        {
            using var contenido = new MemoryStream(request.EvidenciaContenido);
            var ruta = await fileStorage.GuardarAsync(contenido, request.EvidenciaNombreArchivo!, cancellationToken);
            solicitud.AdjuntarEvidencia(ruta, request.EvidenciaNombreArchivo!);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
