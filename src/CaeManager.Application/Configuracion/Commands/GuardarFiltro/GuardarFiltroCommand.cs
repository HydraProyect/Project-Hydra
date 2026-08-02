using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.GuardarFiltro;

/// <summary>
/// Guarda una combinación de filtros con nombre, para el usuario actual, en
/// una pantalla concreta (P3-31). <see cref="ValoresJson"/> lo serializa y
/// entiende la propia pantalla — este Command no conoce la forma de los
/// filtros de cada feature.
/// </summary>
public record GuardarFiltroCommand(string Pantalla, string Nombre, string ValoresJson) : ICommand<Guid>;

public static class PantallasConFiltrosGuardados
{
    public const string Clientes = "Clientes";
    public const string Documentos = "Documentos";
    public const string Trabajadores = "Trabajadores";

    public static readonly string[] Admitidas = [Clientes, Documentos, Trabajadores];
}

public class GuardarFiltroCommandValidator : AbstractValidator<GuardarFiltroCommand>
{
    public GuardarFiltroCommandValidator()
    {
        RuleFor(c => c.Pantalla).Must(p => PantallasConFiltrosGuardados.Admitidas.Contains(p))
            .WithMessage("Esa pantalla no admite filtros guardados.");
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("Ponle un nombre a este filtro.").MaximumLength(100);
        RuleFor(c => c.ValoresJson).NotEmpty();
    }
}

public class GuardarFiltroCommandHandler(
    ICurrentUserService currentUserService, IFiltroGuardadoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarFiltroCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarFiltroCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("FiltroGuardado.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var filtro = new FiltroGuardado(usuarioId.Value, request.Pantalla, request.Nombre, request.ValoresJson);
        repositorio.Agregar(filtro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(filtro.Id);
    }
}
