using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.EditarCliente;

public record EditarClienteCommand(Guid Id, string RazonSocial, string Cif, bool EsCritico, string? Notas) : IRequest<Result>;

public class EditarClienteCommandValidator : AbstractValidator<EditarClienteCommand>
{
    public EditarClienteCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Cliente.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Cliente.LongitudMaximaRazonSocial} caracteres.");

        RuleFor(c => c.Cif)
            .NotEmpty().WithMessage("El CIF es obligatorio.")
            .Must(EsCifValido).WithMessage("El CIF no es válido.");

        RuleFor(c => c.Notas)
            .MaximumLength(Cliente.LongitudMaximaNotas).WithMessage($"Las notas no pueden superar {Cliente.LongitudMaximaNotas} caracteres.");
    }

    private static bool EsCifValido(string cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif);
        return resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;
    }
}

public class EditarClienteCommandHandler(IClienteRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarClienteCommand, Result>
{
    public async Task<Result> Handle(EditarClienteCommand request, CancellationToken cancellationToken)
    {
        var cliente = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (cliente is null)
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.RazonSocialDuplicada", "Ya existe un cliente con esta razón social."));

        if (await repositorio.ExisteConCifAsync(request.Cif, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.CifDuplicado", "Ya existe un cliente con este CIF."));

        cliente.Actualizar(request.RazonSocial, request.Cif, request.EsCritico, request.Notas);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
