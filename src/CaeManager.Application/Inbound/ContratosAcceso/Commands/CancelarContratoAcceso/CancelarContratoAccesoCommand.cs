using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ContratosAcceso.Commands.CancelarContratoAcceso;

/// <summary>
/// D-21 (DECISION_LOG 2026-11-14): «Cancelado» es la baja antes de empezar, sin actividad. Solo un contrato Pendiente de
/// confirmar o Activo, antes de su fecha de inicio (día de negocio de Europe/Madrid) y sin actividad. La actividad que el
/// Tenant TCE puede ver hoy son las copias de evidencia del contrato (<c>EvidenciasInbound</c>), validadas o rechazadas; las
/// asignaciones se sumarán cuando existan (B-17). El contrato no se borra: queda Cancelado y no da acceso nunca.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record CancelarContratoAccesoCommand(Guid Id, Guid Version = default) : ICommand, IComandoInbound;

public class CancelarContratoAccesoCommandValidator : AbstractValidator<CancelarContratoAccesoCommand>
{
    public CancelarContratoAccesoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class CancelarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IEvidenciasInboundQueryContext evidenciasContext, IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork, TimeProvider reloj)
    : IRequestHandler<CancelarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(CancelarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(ContratoAccesoErrores.NoEncontrado);

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        var tieneActividad = await evidenciasContext.EvidenciasInbound
            .AnyAsync(e => e.ContratoAccesoId == contrato.Id, cancellationToken);

        try
        {
            contrato.Cancelar(DiaDeNegocio.Hoy(reloj), tieneActividad);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ContratoAccesoErrores.TransicionNoPermitida(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
