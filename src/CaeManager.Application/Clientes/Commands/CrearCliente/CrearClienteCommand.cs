using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.CrearCliente;

public record CrearClienteCommand(string RazonSocial, string Cif, bool EsCritico, string? Notas) : ICommand<Guid>;

public class CrearClienteCommandValidator : AbstractValidator<CrearClienteCommand>
{
    public CrearClienteCommandValidator()
    {
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

public class CrearClienteCommandHandler(
    IClienteRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<CrearClienteCommand, Result<Guid>>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo
    // motivo que en AutorizacionEscrituraBehavior.
    private const string RolGestorCae = "GestorCae";

    public async Task<Result<Guid>> Handle(CrearClienteCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.RazonSocialDuplicada", "Ya existe un cliente con esta razón social."));

        if (await repositorio.ExisteConCifAsync(request.Cif, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.CifDuplicado", "Ya existe un cliente con este CIF."));

        // Un Cliente creado por un Gestor CAE queda automáticamente en su
        // cartera — "creados o asignados" (ver Roles.cs). El resto de roles
        // que pueden crear Clientes (Administrador, DireccionCae,
        // CoordinadorCae) lo dejan sin gestor hasta asignarlo explícitamente.
        var rol = await currentUserService.ObtenerRolActualAsync();
        var ejecutivoUsuarioId = rol == RolGestorCae ? await currentUserService.ObtenerUsuarioActualIdAsync() : null;

        var cliente = new Cliente(request.RazonSocial, request.Cif, request.EsCritico, request.Notas, ejecutivoUsuarioId);
        repositorio.Agregar(cliente);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(cliente.Id);
    }
}
