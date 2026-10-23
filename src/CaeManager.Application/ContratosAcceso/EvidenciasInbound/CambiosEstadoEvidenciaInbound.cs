using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EvidenciasInbound;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.EvidenciasInbound;

// Cambios de estado de una evidencia Inbound, los mismos que AcreditacionDocumentoPlataforma ya tiene para lo validado:
// rechazar con causa tipificada y motivo literal, y volver a validar. La autoridad es la de la lectura
// (LecturaInboundAutoridad sobre el contrato de la evidencia): quien no puede validar ese contrato tampoco cambia sus
// evidencias, y lo que no ve responde «no encontrado». Ninguno lee el otro Tenant: la evidencia ya es del Tenant del
// Cliente empresarial, y revocar la autorización no la toca.

public record RechazarEvidenciaInboundCommand(Guid EvidenciaId, CausaRechazoAcreditacion Causa, string Motivo) : ICommand;

public class RechazarEvidenciaInboundCommandValidator : AbstractValidator<RechazarEvidenciaInboundCommand>
{
    public RechazarEvidenciaInboundCommandValidator()
    {
        RuleFor(c => c.Causa).IsInEnum();
        RuleFor(c => c.Motivo).NotEmpty().WithMessage("Indica el motivo del rechazo.")
            .MaximumLength(EvidenciaInbound.LongitudMaximaMotivoRechazo);
    }
}

public record ValidarEvidenciaInboundCommand(Guid EvidenciaId) : ICommand;

public class RechazarEvidenciaInboundCommandHandler(
    IEvidenciaInboundRepository repositorio, IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser, IUnitOfWork unitOfWork, TimeProvider reloj)
    : IRequestHandler<RechazarEvidenciaInboundCommand, Result>
{
    public async Task<Result> Handle(RechazarEvidenciaInboundCommand request, CancellationToken cancellationToken)
    {
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

public class ValidarEvidenciaInboundCommandHandler(
    IEvidenciaInboundRepository repositorio, IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser, IUnitOfWork unitOfWork, TimeProvider reloj)
    : IRequestHandler<ValidarEvidenciaInboundCommand, Result>
{
    public async Task<Result> Handle(ValidarEvidenciaInboundCommand request, CancellationToken cancellationToken)
    {
        var evidencia = await EvidenciaVisible.ObtenerAsync(
            request.EvidenciaId, repositorio, contratosContext, alcanceDatos, currentUser, cancellationToken);
        if (evidencia is null || await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(EvidenciaInboundErrores.NoEncontrada);
        if (evidencia.Estado != EstadoAcreditacion.Rechazada)
            return Result.Fallo(EvidenciaInboundErrores.EstadoNoAdmite);

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
