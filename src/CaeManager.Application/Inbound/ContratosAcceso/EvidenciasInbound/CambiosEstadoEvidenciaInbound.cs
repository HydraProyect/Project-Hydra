using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;

// Cambios de estado de una evidencia Inbound, los mismos que AcreditacionDocumentoPlataforma ya tiene para lo validado:
// rechazar con causa tipificada y motivo literal, y volver a validar. La autoridad es la de la lectura
// (LecturaInboundAutoridad sobre el contrato de la evidencia): quien no puede validar ese contrato tampoco cambia sus
// evidencias, y lo que no ve responde «no encontrado». Rechazar no lee el otro Tenant: la evidencia ya es del Tenant del
// Cliente empresarial, y revocar la autorización no la toca. Volver a validar sí lo lee, solo para aplicar D-12 (ver
// ValidarEvidenciaInboundCommandHandler).

[AreaDelEncargo("ContratosAcceso")]
public record RechazarEvidenciaInboundCommand(Guid EvidenciaId, CausaRechazoAcreditacion Causa, string Motivo) : ICommand, IComandoInbound;

public class RechazarEvidenciaInboundCommandValidator : AbstractValidator<RechazarEvidenciaInboundCommand>
{
    public RechazarEvidenciaInboundCommandValidator()
    {
        RuleFor(c => c.Causa).IsInEnum();
        RuleFor(c => c.Motivo).NotEmpty().WithMessage("Indica el motivo del rechazo.")
            .MaximumLength(EvidenciaInbound.LongitudMaximaMotivoRechazo);
    }
}

[AreaDelEncargo("ContratosAcceso")]
public record ValidarEvidenciaInboundCommand(Guid EvidenciaId) : ICommand, IComandoInbound;

public class RechazarEvidenciaInboundCommandHandler(
    IEvidenciaInboundRepository repositorio, IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser, IUnitOfWork unitOfWork, TimeProvider reloj, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<RechazarEvidenciaInboundCommand, Result>
{
    public async Task<Result> Handle(RechazarEvidenciaInboundCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ValidarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var evidencia = await EvidenciaVisible.ObtenerAsync(
            request.EvidenciaId, repositorio, contratosContext, alcanceDatos, currentUser, cancellationToken);
        if (evidencia is null || await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(EvidenciaInboundErrores.NoEncontrada);
        if (evidencia.Estado != EstadoAcreditacion.Aceptada)
            return Result.Fallo(EvidenciaInboundErrores.EstadoNoAdmite);

        evidencia.Rechazar(request.Causa, request.Motivo, usuarioId, reloj.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

/// <summary>
/// Vuelve a validar una evidencia rechazada. D-12 se aplica igual que en <see cref="RegistrarEvidenciaInboundCommand"/>
/// (decisión del módulo Inbound, 2026-11-18, opción a): la marca de quién subió la versión se lee del Tenant de la Empresa
/// contratista por <see cref="ILectorDocumentosInbound"/>, con el <c>DocumentoOrigenId</c> de la evidencia. Si el original
/// ya no es visible (sustituido, revocado, fuera de la autorización), no se puede comprobar quién lo subió y se falla
/// cerrado con <see cref="EvidenciaInboundErrores.OriginalNoVisibleParaRevalidar"/>. Es el único cambio de estado que lee el
/// otro Tenant, y lo hace fuera de transacción, antes del único guardado, como exige el lector.
/// </summary>
public class ValidarEvidenciaInboundCommandHandler(
    IEvidenciaInboundRepository repositorio, IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser, IUnitOfWork unitOfWork, TimeProvider reloj, IAutorizacionInbound autorizacionInbound,
    ILectorDocumentosInbound lector, IActorAuditoria actorAuditoria)
    : IRequestHandler<ValidarEvidenciaInboundCommand, Result>
{
    public async Task<Result> Handle(ValidarEvidenciaInboundCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ValidarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        var evidencia = await EvidenciaVisible.ObtenerAsync(
            request.EvidenciaId, repositorio, contratosContext, alcanceDatos, currentUser, cancellationToken);
        if (evidencia is null || await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(EvidenciaInboundErrores.NoEncontrada);
        if (evidencia.Estado != EstadoAcreditacion.Rechazada)
            return Result.Fallo(EvidenciaInboundErrores.EstadoNoAdmite);

        // D-12.2: bajo impersonación no se valida (provisional, ver GuardaD12), antes de leer el otro Tenant.
        if (await GuardaD12.HayImpersonacionAsync(actorAuditoria))
            return Result.Fallo(EvidenciaInboundErrores.ValidarBajoImpersonacionNoDisponible);

        var original = (await lector.DocumentosAutorizadosAsync(evidencia.ContratoAccesoId, cancellationToken))
            .FirstOrDefault(d => d.DocumentoId == evidencia.DocumentoOrigenId);
        if (original is null)
            return Result.Fallo(EvidenciaInboundErrores.OriginalNoVisibleParaRevalidar);
        if (original.SubidoPorQuienLee)
            return Result.Fallo(EvidenciaInboundErrores.ValidaLoQueSubio);

        evidencia.Validar(usuarioId, reloj.GetUtcNow().UtcDateTime);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

internal static class EvidenciaVisible
{
    public static async Task<EvidenciaInbound?> ObtenerAsync(
        Guid evidenciaId, IEvidenciaInboundRepository repositorio, IContratosAccesoQueryContext contratosContext,
        IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser, CancellationToken cancellationToken)
    {
        var evidencia = await repositorio.ObtenerPorIdAsync(evidenciaId, cancellationToken);
        return evidencia is not null
               && await LecturaInboundAutoridad.PuedeLeerAsync(evidencia.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken)
            ? evidencia
            : null;
    }
}
