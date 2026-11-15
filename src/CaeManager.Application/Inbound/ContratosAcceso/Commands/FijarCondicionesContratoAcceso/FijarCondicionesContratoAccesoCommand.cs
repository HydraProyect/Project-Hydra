using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.Commands.FijarCondicionesContratoAcceso;

/// <summary>
/// Fija de una vez el tipo, la propia actividad, el nivel máximo de subcontrata y los cupos del contrato (B-05). Sustituye
/// las condiciones enteras: lo que llega en null queda sin fijar (o, en un cupo, sin límite). Es un comando aparte de
/// <c>ActualizarContratoAccesoCommand</c> para que una edición del fin o de las notas nunca borre unas condiciones que no
/// conoce. Solo en un contrato Pendiente de confirmar o Activo (<c>ContratoAcceso.FijarCondiciones</c>).
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record FijarCondicionesContratoAccesoCommand(Guid Id, CondicionesContratoAccesoDatos Condiciones, Guid Version = default) : ICommand;

public class FijarCondicionesContratoAccesoCommandValidator : AbstractValidator<FijarCondicionesContratoAccesoCommand>
{
    public FijarCondicionesContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Condiciones).NotNull().SetValidator(new CondicionesContratoAccesoDatosValidator());
    }
}

public class FijarCondicionesContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<FijarCondicionesContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(FijarCondicionesContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(ContratoAccesoErrores.NoEncontrado);

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        var condiciones = (request.Condiciones ?? new CondicionesContratoAccesoDatos()).ADominio();
        if (!condiciones.EsExitoso)
            return Result.Fallo(condiciones.Error);

        try
        {
            contrato.FijarCondiciones(condiciones.Valor);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ContratoAccesoErrores.TransicionNoPermitida(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
