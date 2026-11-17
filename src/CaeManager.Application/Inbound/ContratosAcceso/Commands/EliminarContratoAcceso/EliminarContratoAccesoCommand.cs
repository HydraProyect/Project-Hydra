using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.Commands.EliminarContratoAcceso;

/// <summary>
/// Baja lógica (soft delete): el runtime no tiene DELETE sobre <c>ContratosAcceso</c>. DN1 (DECISION_LOG 2026-11-15): solo
/// se elimina un contrato Cancelado o que no ha empezado (día de negocio de Europe/Madrid); otro responde
/// <c>ContratoAcceso.TransicionNoPermitida</c>. <paramref name="Version"/> es la que vio quien pide la baja: si el contrato
/// cambió desde entonces (p. ej. alguien lo canceló o lo finalizó), responde el conflicto de concurrencia.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record EliminarContratoAccesoCommand(Guid Id, Guid Version = default) : ICommand, IComandoInbound;

public class EliminarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService, TimeProvider reloj, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<EliminarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(EliminarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(ContratoAccesoErrores.NoEncontrado);

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        try
        {
            contrato.Eliminar(DiaDeNegocio.Hoy(reloj), usuarioId ?? Guid.Empty);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ContratoAccesoErrores.TransicionNoPermitida(ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
