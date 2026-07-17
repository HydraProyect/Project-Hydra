using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EditarEmpresa;

public record EditarEmpresaCommand(Guid Id, string RazonSocial, IReadOnlyList<Guid> ClienteIds) : IRequest<Result>;

public class EditarEmpresaCommandValidator : AbstractValidator<EditarEmpresaCommand>
{
    public EditarEmpresaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");
    }
}

public class EditarEmpresaCommandHandler(
    IEmpresaRepository repositorio, IEmpresaClienteRepository empresaClienteRepositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarEmpresaCommand, Result>
{
    public async Task<Result> Handle(EditarEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null)
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.RazonSocialDuplicada", "Ya existe una empresa con esta razón social."));

        empresa.Actualizar(request.RazonSocial);

        var actuales = await empresaClienteRepositorio.ObtenerPorEmpresaAsync(empresa.Id, cancellationToken);
        var deseados = request.ClienteIds.Distinct().ToHashSet();
        var actualesClienteIds = actuales.Select(ec => ec.ClienteId).ToHashSet();

        foreach (var ec in actuales.Where(ec => !deseados.Contains(ec.ClienteId)))
            empresaClienteRepositorio.Eliminar(ec);

        foreach (var clienteId in deseados.Except(actualesClienteIds))
            empresaClienteRepositorio.Agregar(new EmpresaCliente(empresa.Id, clienteId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
