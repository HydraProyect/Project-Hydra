using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.GuardarOrdenCajasFicha;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.RestablecerOrdenCajasFicha;

/// <summary>
/// «Restablecer orden»: borra el orden de cajas que el usuario actual guardó
/// para ese tipo de ficha en el Tenant actual, y la ficha vuelve al orden
/// automático. Si no tenía ninguno, no hace nada y termina bien.
/// </summary>
public record RestablecerOrdenCajasFichaCommand(string TipoFicha) : ICommand, IComandoDeAutoservicio;

public class RestablecerOrdenCajasFichaCommandValidator : AbstractValidator<RestablecerOrdenCajasFichaCommand>
{
    public RestablecerOrdenCajasFichaCommandValidator() =>
        RuleFor(c => c.TipoFicha).Must(t => TiposDeFicha360.Admitidos.Contains(t))
            .WithMessage("Ese tipo de ficha no admite un orden de cajas.");
}

public class RestablecerOrdenCajasFichaCommandHandler(
    ICurrentUserService currentUserService, IOrdenCajasFichaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<RestablecerOrdenCajasFichaCommand, Result>
{
    public async Task<Result> Handle(RestablecerOrdenCajasFichaCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("OrdenCajasFicha.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Se busca por el usuario actual, nunca por un Id del request: no hay
        // forma de nombrar el orden de otro usuario.
        var orden = await repositorio.ObtenerAsync(usuarioId.Value, request.TipoFicha, cancellationToken);
        if (orden is null)
            return Result.Exito();

        repositorio.Eliminar(orden);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
