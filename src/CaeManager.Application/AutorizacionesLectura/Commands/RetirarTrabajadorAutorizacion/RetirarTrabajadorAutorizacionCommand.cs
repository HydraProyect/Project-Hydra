using CaeManager.Application.Common;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AutorizacionesLectura.Commands.RetirarTrabajadorAutorizacion;

/// <summary>Retira un Trabajador de una autorización sin revocar: borra su fila, que no tiene UPDATE.</summary>
public record RetirarTrabajadorAutorizacionCommand(Guid AutorizacionId, Guid TrabajadorId, Guid Version = default) : ICommand;

public class RetirarTrabajadorAutorizacionCommandValidator : AbstractValidator<RetirarTrabajadorAutorizacionCommand>
{
    public RetirarTrabajadorAutorizacionCommandValidator()
    {
        RuleFor(c => c.AutorizacionId).NotEmpty();
        RuleFor(c => c.TrabajadorId).NotEmpty();
    }
}

public class RetirarTrabajadorAutorizacionCommandHandler(
    IAutorizacionLecturaRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<RetirarTrabajadorAutorizacionCommand, Result>
{
    public async Task<Result> Handle(RetirarTrabajadorAutorizacionCommand request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService))
            return Result.Fallo(AutorizacionLecturaAutoridad.SoloAdministrador);

        var autorizacion = await repositorio.ObtenerPorIdAsync(request.AutorizacionId, cancellationToken);
        if (autorizacion is null)
            return Result.Fallo(AutorizacionLecturaAutoridad.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(autorizacion, request.Version, "esta autorización de lectura") is { } conflicto)
            return Result.Fallo(conflicto);

        if (autorizacion.EstaRevocada)
            return Result.Fallo(AutorizacionLecturaAutoridad.Revocada);

        var fila = (await repositorio.ObtenerTrabajadoresAsync(autorizacion.Id, cancellationToken))
            .FirstOrDefault(t => t.TrabajadorId == request.TrabajadorId);
        if (fila is null)
            return Result.Fallo(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado);

        autorizacion.RetirarTrabajador(fila);
        repositorio.RetirarTrabajador(fila);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
