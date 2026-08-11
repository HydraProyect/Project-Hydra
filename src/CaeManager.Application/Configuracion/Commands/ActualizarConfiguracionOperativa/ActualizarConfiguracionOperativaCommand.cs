using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.ActualizarConfiguracionOperativa;

/// <summary>
/// Jornada de referencia del tenant y encendido de la medición de tiempo de
/// enfoque. Va aparte de <c>ActualizarParametroSistemaCommand</c> (que solo
/// toca los umbrales documentales) porque son dos secciones distintas de
/// <c>/configuracion</c> y no tiene sentido que guardar una arrastre la otra.
/// </summary>
public record ActualizarConfiguracionOperativaCommand(
    TimeOnly HoraInicioJornada,
    TimeOnly HoraFinJornada,
    int HorasJornadaMensualGestor,
    bool MedicionTiempoActiva,
    int SegundosInactividadPausa,
    bool ExcluirFueraDeJornadaEnMetricas) : ICommand;

public class ActualizarConfiguracionOperativaCommandValidator : AbstractValidator<ActualizarConfiguracionOperativaCommand>
{
    public ActualizarConfiguracionOperativaCommandValidator()
    {
        RuleFor(c => c.HoraFinJornada)
            .GreaterThan(c => c.HoraInicioJornada)
            .WithMessage("La hora de fin de jornada debe ser posterior a la de inicio.");
        RuleFor(c => c.HorasJornadaMensualGestor)
            .InclusiveBetween(1, 744)
            .WithMessage("Las horas de jornada mensual deben estar entre 1 y 744.");
        RuleFor(c => c.SegundosInactividadPausa)
            .InclusiveBetween(30, 600)
            .WithMessage("Los segundos de inactividad deben estar entre 30 y 600.");
    }
}

public class ActualizarConfiguracionOperativaCommandHandler(IParametroSistemaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarConfiguracionOperativaCommand, Result>
{
    public async Task<Result> Handle(ActualizarConfiguracionOperativaCommand request, CancellationToken cancellationToken)
    {
        var parametros = await repositorio.ObtenerAsync(cancellationToken);

        try
        {
            parametros.ActualizarConfiguracionOperativa(
                request.HoraInicioJornada,
                request.HoraFinJornada,
                request.HorasJornadaMensualGestor,
                request.MedicionTiempoActiva,
                request.SegundosInactividadPausa,
                request.ExcluirFueraDeJornadaEnMetricas);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ParametroSistema.Invalido", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
