using CaeManager.Application.Common;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AutorizacionesLectura.Commands.RevocarAutorizacionLectura;

/// <summary>
/// Revoca la autorización, de forma definitiva: corta desde ese instante la lectura del Tenant destinatario (funciones <c>app_inbound_*</c>), y las copias que
/// ese Tenant ya guardó se quedan. Volver a autorizar es conceder otra.
/// </summary>
public record RevocarAutorizacionLecturaCommand(Guid AutorizacionId, Guid Version = default) : ICommand;

public class RevocarAutorizacionLecturaCommandValidator : AbstractValidator<RevocarAutorizacionLecturaCommand>
{
    public RevocarAutorizacionLecturaCommandValidator()
    {
        RuleFor(c => c.AutorizacionId).NotEmpty();
    }
}

public class RevocarAutorizacionLecturaCommandHandler(
    IAutorizacionLecturaRepository repositorio, ICurrentUserService currentUserService, TimeProvider reloj, IUnitOfWork unitOfWork)
    : IRequestHandler<RevocarAutorizacionLecturaCommand, Result>
{
    public async Task<Result> Handle(RevocarAutorizacionLecturaCommand request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService)
            || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(AutorizacionLecturaAutoridad.SoloAdministrador);

        var autorizacion = await repositorio.ObtenerPorIdAsync(request.AutorizacionId, cancellationToken);
        if (autorizacion is null)
            return Result.Fallo(AutorizacionLecturaAutoridad.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(autorizacion, request.Version, "esta autorización de lectura") is { } conflicto)
            return Result.Fallo(conflicto);

        if (autorizacion.EstaRevocada)
            return Result.Fallo(AutorizacionLecturaAutoridad.Revocada);

        autorizacion.Revocar(usuarioId, reloj.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
