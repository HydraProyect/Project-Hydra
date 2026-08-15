using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.ActualizarPresupuestoIa;

/// <summary>
/// Va aparte de <c>ActualizarParametroSistemaCommand</c>/<c>ActualizarConfiguracionOperativaCommand</c>
/// (mismo criterio: son secciones distintas de <c>/configuracion</c>) — límite blando de
/// gasto mensual en IA documental (Horizonte 2.7 del plan). <paramref name="PresupuestoMensualIaUsd"/>
/// en <c>null</c> desactiva el aviso.
/// </summary>
public record ActualizarPresupuestoIaCommand(decimal? PresupuestoMensualIaUsd) : ICommand;

public class ActualizarPresupuestoIaCommandValidator : AbstractValidator<ActualizarPresupuestoIaCommand>
{
    public ActualizarPresupuestoIaCommandValidator()
    {
        RuleFor(c => c.PresupuestoMensualIaUsd)
            .GreaterThanOrEqualTo(0)
            .When(c => c.PresupuestoMensualIaUsd is not null)
            .WithMessage("El presupuesto mensual de IA no puede ser negativo.");
    }
}

public class ActualizarPresupuestoIaCommandHandler(IParametroSistemaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarPresupuestoIaCommand, Result>
{
    public async Task<Result> Handle(ActualizarPresupuestoIaCommand request, CancellationToken cancellationToken)
    {
        var parametros = await repositorio.ObtenerAsync(cancellationToken);

        try
        {
            parametros.ActualizarPresupuestoIa(request.PresupuestoMensualIaUsd);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ParametroSistema.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
