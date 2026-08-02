using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.ActualizarParametroSistema;

public record ActualizarParametroSistemaCommand(int UmbralAmbarDias, int UmbralRojoDias) : ICommand;

public class ActualizarParametroSistemaCommandValidator : AbstractValidator<ActualizarParametroSistemaCommand>
{
    public ActualizarParametroSistemaCommandValidator()
    {
        RuleFor(c => c.UmbralRojoDias).GreaterThan(0).WithMessage("El umbral rojo debe ser mayor que cero.");
        RuleFor(c => c.UmbralAmbarDias)
            .GreaterThan(c => c.UmbralRojoDias)
            .WithMessage("El umbral ámbar debe ser mayor que el umbral rojo.");
    }
}

public class ActualizarParametroSistemaCommandHandler(IParametroSistemaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarParametroSistemaCommand, Result>
{
    public async Task<Result> Handle(ActualizarParametroSistemaCommand request, CancellationToken cancellationToken)
    {
        var parametros = await repositorio.ObtenerAsync(cancellationToken);

        try
        {
            parametros.Actualizar(request.UmbralAmbarDias, request.UmbralRojoDias);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ParametroSistema.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
