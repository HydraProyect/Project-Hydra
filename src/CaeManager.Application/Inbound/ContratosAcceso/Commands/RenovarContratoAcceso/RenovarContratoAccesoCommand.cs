using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.Commands.RenovarContratoAcceso;

/// <summary>
/// Renovar amplía la vigencia del contrato sin cambiar de estado (duda 3, DECISION_LOG 2026-11-14): fin nuevo posterior
/// al actual, solo en un contrato Activo o Suspendido. Deja el fin anterior y el nuevo, quién y cuándo, en el historial de
/// vigencia (B-05).
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record RenovarContratoAccesoCommand(Guid Id, DateOnly NuevaFechaFin, Guid Version = default) : ICommand, IComandoInbound;

public class RenovarContratoAccesoCommandValidator : AbstractValidator<RenovarContratoAccesoCommand>
{
    public RenovarContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class RenovarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService, TimeProvider reloj, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<RenovarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(RenovarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(ContratoAccesoErrores.NoEncontrado);

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(ContratoAccesoErrores.SinUsuario);

        try
        {
            contrato.Renovar(request.NuevaFechaFin, usuarioId, reloj.GetUtcNow().UtcDateTime);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ContratoAcceso.FechaFinInvalida", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ContratoAccesoErrores.TransicionNoPermitida(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
