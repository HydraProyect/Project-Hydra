using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
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
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

        RuleFor(c => c.Cif)
            .NotEmpty().WithMessage("El CIF es obligatorio.")
            .Must(EsCifValido).WithMessage("El CIF no es válido.");

        RuleFor(c => c.Notas)
            .MaximumLength(Empresa.LongitudMaximaNotas).WithMessage($"Las notas no pueden superar {Empresa.LongitudMaximaNotas} caracteres.");
    }

    private static bool EsCifValido(string cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif);
        return resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;
    }
}

/// <summary>
/// F3b — reemplaza <c>IClienteRepository</c> por <c>IEmpresaRepository</c>:
/// desde la congelación, "crear un Cliente" es crear una Empresa
/// contraparte (<see cref="Empresa.CrearComoCliente"/>). La comprobación
/// de unicidad de RazonSocial/Cif pasa a ser global (contra todas las
/// Empresas, no solo contra los antiguos Clientes) porque el índice único
/// de la base ya es global desde F3a — un mensaje que dijera "ya existe un
/// Cliente" sería inexacto si la colisión es con una Empresa propia o una
/// ex-Subcontrata.
/// </summary>
public class CrearClienteCommandHandler(
    IEmpresaRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService,
    IAsignacionesOperativasWriter asignacionesWriter)
    : IRequestHandler<CrearClienteCommand, Result<Guid>>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo
    // motivo que en AutorizacionEscrituraBehavior.
    private const string RolGestorCae = "GestorCae";

    public async Task<Result<Guid>> Handle(CrearClienteCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.RazonSocialDuplicada", "Ya existe una organización con esta razón social."));

        if (await repositorio.ExisteConCifAsync(request.Cif, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.CifDuplicado", "Ya existe una organización con este CIF."));

        // Un Cliente creado por un Gestor CAE queda automáticamente en su
        // cartera — "creados o asignados" (ver Roles.cs). El resto de roles
        // que pueden crear Clientes (Administrador, DireccionCae,
        // CoordinadorCae) lo dejan sin gestor hasta asignarlo explícitamente.
        var rol = await currentUserService.ObtenerRolActualAsync();
        var ejecutivoUsuarioId = rol == RolGestorCae ? await currentUserService.ObtenerUsuarioActualIdAsync() : null;

        var empresa = Empresa.CrearComoCliente(request.RazonSocial, request.Cif, request.EsCritico, request.Notas, ejecutivoUsuarioId);
        repositorio.Agregar(empresa);

        // Doble escritura también aquí, y no solo al reasignar: sin esto, el
        // Gestor CAE que crea un cliente se quedaría con la proyección puesta
        // pero sin cartera, y al conmutar la autorización perdería de vista el
        // cliente que acaba de crear. La Empresa todavía no tiene TenantId
        // (lo sella el interceptor al guardar), así que el propietario se
        // resuelve del contexto, no de la entidad.
        if (ejecutivoUsuarioId is not null)
            await asignacionesWriter.ReasignarCarteraClienteAsync(empresa.Id, ejecutivoUsuarioId, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(empresa.Id);
    }
}
