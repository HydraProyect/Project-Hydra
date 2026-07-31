using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;

public record EditarSubcontrataCommand(
    Guid Id, string RazonSocial, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds,
    Guid Version = default) : IRequest<Result>;

public class EditarSubcontrataCommandValidator : AbstractValidator<EditarSubcontrataCommand>
{
    public EditarSubcontrataCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Subcontrata.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Subcontrata.LongitudMaximaRazonSocial} caracteres.");
    }
}

public class EditarSubcontrataCommandHandler(
    ISubcontrataRepository repositorio,
    ISubcontrataClienteRepository subcontrataClienteRepositorio,
    ISubcontrataEmpresaRepository subcontrataEmpresaRepositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EditarSubcontrataCommand, Result>
{
    public async Task<Result> Handle(EditarSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (subcontrata is null)
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        if (ConcurrenciaOptimista.Verificar(subcontrata, request.Version, "esta subcontrata") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.RazonSocialDuplicada", "Ya existe una subcontrata con esta razón social."));

        subcontrata.Actualizar(request.RazonSocial);

        var clientesActuales = await subcontrataClienteRepositorio.ObtenerPorSubcontrataAsync(subcontrata.Id, cancellationToken);
        var clienteIdsDeseados = request.ClienteIds.Distinct().ToHashSet();
        var clienteIdsActuales = clientesActuales.Select(sc => sc.ClienteId).ToHashSet();

        foreach (var sc in clientesActuales.Where(sc => !clienteIdsDeseados.Contains(sc.ClienteId)))
            subcontrataClienteRepositorio.Eliminar(sc);

        foreach (var clienteId in clienteIdsDeseados.Except(clienteIdsActuales))
            subcontrataClienteRepositorio.Agregar(new SubcontrataCliente(subcontrata.Id, clienteId));

        var empresasActuales = await subcontrataEmpresaRepositorio.ObtenerPorSubcontrataAsync(subcontrata.Id, cancellationToken);
        var empresaIdsDeseados = request.EmpresaIds.Distinct().ToHashSet();
        var empresaIdsActuales = empresasActuales.Select(se => se.EmpresaId).ToHashSet();

        foreach (var se in empresasActuales.Where(se => !empresaIdsDeseados.Contains(se.EmpresaId)))
            subcontrataEmpresaRepositorio.Eliminar(se);

        foreach (var empresaId in empresaIdsDeseados.Except(empresaIdsActuales))
            subcontrataEmpresaRepositorio.Agregar(new SubcontrataEmpresa(subcontrata.Id, empresaId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
