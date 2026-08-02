using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.CrearCentro;

public record CrearCentroCommand(
    Guid ClienteId,
    Guid EmpresaId,
    string Nombre,
    string? CodigoCentro,
    string? Direccion,
    string? Contacto,
    DateOnly? ContratoVigenteHasta) : ICommand<Guid>;

public class CrearCentroCommandValidator : AbstractValidator<CrearCentroCommand>
{
    public CrearCentroCommandValidator()
    {
        RuleFor(c => c.ClienteId).NotEmpty().WithMessage("Selecciona un cliente.");
        RuleFor(c => c.EmpresaId).NotEmpty().WithMessage("Selecciona una empresa.");

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(Centro.LongitudMaximaNombre).WithMessage($"El nombre no puede superar {Centro.LongitudMaximaNombre} caracteres.");

        RuleFor(c => c.CodigoCentro).MaximumLength(Centro.LongitudMaximaCodigo);
        RuleFor(c => c.Direccion).MaximumLength(Centro.LongitudMaximaDireccion);
        RuleFor(c => c.Contacto).MaximumLength(Centro.LongitudMaximaContacto);
    }
}

public class CrearCentroCommandHandler(
    ICentroRepository repositorio, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearCentroCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearCentroCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (!await clientesContext.Clientes.AnyAsync(c => c.Id == request.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.ClienteNoEncontrado", "No encontramos este cliente."));

        if (!await empresasContext.Empresas.AnyAsync(e => e.Id == request.EmpresaId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.EmpresaNoEncontrada", "No encontramos esta empresa."));

        if (await repositorio.ExisteConNombreEnClienteAsync(request.ClienteId, request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Centro.NombreDuplicado", "Este cliente ya tiene un centro con este nombre."));

        var centro = new Centro(
            request.ClienteId, request.EmpresaId, request.Nombre,
            request.CodigoCentro, request.Direccion, request.Contacto, request.ContratoVigenteHasta);

        repositorio.Agregar(centro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(centro.Id);
    }
}
