using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;

public record CrearSubcontrataCommand(
    string RazonSocial, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds) : ICommand<Guid>;

public class CrearSubcontrataCommandValidator : AbstractValidator<CrearSubcontrataCommand>
{
    public CrearSubcontrataCommandValidator()
    {
        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Subcontrata.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Subcontrata.LongitudMaximaRazonSocial} caracteres.");
    }
}

public class CrearSubcontrataCommandHandler(
    ISubcontrataRepository repositorio,
    ISubcontrataClienteRepository subcontrataClienteRepositorio,
    ISubcontrataEmpresaRepository subcontrataEmpresaRepositorio,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearSubcontrataCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearSubcontrataCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Subcontrata.RazonSocialDuplicada", "Ya existe una subcontrata con esta razón social."));

        var subcontrata = new Subcontrata(request.RazonSocial);
        repositorio.Agregar(subcontrata);

        foreach (var clienteId in request.ClienteIds.Distinct())
            subcontrataClienteRepositorio.Agregar(new SubcontrataCliente(subcontrata.Id, clienteId));

        foreach (var empresaId in request.EmpresaIds.Distinct())
            subcontrataEmpresaRepositorio.Agregar(new SubcontrataEmpresa(subcontrata.Id, empresaId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(subcontrata.Id);
    }
}
