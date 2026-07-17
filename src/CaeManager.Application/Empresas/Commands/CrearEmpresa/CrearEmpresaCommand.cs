using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.CrearEmpresa;

public record CrearEmpresaCommand(string RazonSocial, IReadOnlyList<Guid> ClienteIds) : IRequest<Result<Guid>>;

public class CrearEmpresaCommandValidator : AbstractValidator<CrearEmpresaCommand>
{
    public CrearEmpresaCommandValidator()
    {
        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");
    }
}

public class CrearEmpresaCommandHandler(
    IEmpresaRepository repositorio, IEmpresaClienteRepository empresaClienteRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearEmpresaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearEmpresaCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.RazonSocialDuplicada", "Ya existe una empresa con esta razón social."));

        var empresa = new Empresa(request.RazonSocial);
        repositorio.Agregar(empresa);

        foreach (var clienteId in request.ClienteIds.Distinct())
            empresaClienteRepositorio.Agregar(new EmpresaCliente(empresa.Id, clienteId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(empresa.Id);
    }
}
