using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AutorizacionesLectura.Commands.AsignarTrabajadoresAutorizacion;

/// <summary>Añade Trabajadores a una autorización sin revocar. Los que ya estaban asignados se ignoran.</summary>
public record AsignarTrabajadoresAutorizacionCommand(Guid AutorizacionId, IReadOnlyList<Guid> TrabajadorIds, Guid Version = default)
    : ICommand;

public class AsignarTrabajadoresAutorizacionCommandValidator : AbstractValidator<AsignarTrabajadoresAutorizacionCommand>
{
    public AsignarTrabajadoresAutorizacionCommandValidator()
    {
        RuleFor(c => c.AutorizacionId).NotEmpty();
        RuleFor(c => c.TrabajadorIds).NotEmpty();
        RuleForEach(c => c.TrabajadorIds).NotEmpty();
    }
}

public class AsignarTrabajadoresAutorizacionCommandHandler(
    IAutorizacionLecturaRepository repositorio,
    ITrabajadoresQueryContext trabajadoresContext,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarTrabajadoresAutorizacionCommand, Result>
{
    public async Task<Result> Handle(AsignarTrabajadoresAutorizacionCommand request, CancellationToken cancellationToken)
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

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();
        if (!await TrabajadoresDelTenant.ExistenTodosAsync(trabajadoresContext, trabajadorIds, cancellationToken))
            return Result.Fallo(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado);

        var yaAsignados = (await repositorio.ObtenerTrabajadoresAsync(autorizacion.Id, cancellationToken))
            .Select(t => t.TrabajadorId)
            .ToHashSet();
        var nuevos = trabajadorIds.Where(id => !yaAsignados.Contains(id)).ToList();
        if (nuevos.Count == 0)
            return Result.Exito();

        foreach (var trabajadorId in nuevos)
            repositorio.AgregarTrabajador(autorizacion.AsignarTrabajador(trabajadorId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
