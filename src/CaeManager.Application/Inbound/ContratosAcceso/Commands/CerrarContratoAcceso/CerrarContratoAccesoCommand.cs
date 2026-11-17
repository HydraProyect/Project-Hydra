using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.Commands.CerrarContratoAcceso;

/// <summary>
/// Finaliza el contrato (Activo → Finalizado) con su cierre real. Conserva el nombre de M1; un Finalizado no se reabre
/// (D-21). Un Suspendido responde <c>ContratoAcceso.TransicionNoPermitida</c>: finalizarlo con un cierre de hoy o futuro
/// le devolvería el acceso que la suspensión retiró (<c>ContratoAcceso.Finalizar</c>). La cascada a asignaciones y subcontratas llega con <c>FinalizarContratoAccesoCommand</c> (B-18).
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record CerrarContratoAccesoCommand(Guid Id, DateOnly FechaCierre, Guid Version = default) : ICommand, IComandoInbound;

public class CerrarContratoAccesoCommandValidator : AbstractValidator<CerrarContratoAccesoCommand>
{
    public CerrarContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class CerrarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<CerrarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(CerrarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        // «Ya cerrado» es solo el Finalizado (también el que cerró la versión anterior durante el relevo:
        // ContratoAcceso.EstadoEfectivo). Un Cancelado o un Rechazado no se finalizó nunca, aunque la versión anterior le
        // haya escrito un cierre real: es una transición no permitida, que Finalizar rechaza.
        if (contrato.EstadoEfectivo == EstadoContratoAcceso.Finalizado)
            return Result.Fallo(Error.Crear("ContratoAcceso.YaCerrado", "Este contrato de acceso ya está cerrado."));

        try
        {
            contrato.Finalizar(request.FechaCierre);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("ContratoAcceso.FechaCierreInvalida", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ContratoAccesoErrores.TransicionNoPermitida(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
