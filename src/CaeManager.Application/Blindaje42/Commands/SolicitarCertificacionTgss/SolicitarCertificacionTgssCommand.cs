using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;

/// <summary>
/// Registra que el Cliente (empresa principal) ha solicitado a la TGSS la
/// certificación negativa por descubiertos sobre una Empresa (art. 42.1 ET).
/// Siempre crea una solicitud nueva — no hay "editar una solicitud ya
/// hecha", igual que <c>RegistrarVerificacionExternaSubcontrataCommand</c>:
/// cada una es un hecho fechado, y una contrata larga necesita varias a lo
/// largo del tiempo (ver el comentario de <see cref="SolicitudCertificacionTgss"/>).
/// </summary>
public record SolicitarCertificacionTgssCommand(
    Guid EmpresaId,
    Guid ClienteId,
    DateOnly FechaSolicitud,
    string? Observaciones) : ICommand<Guid>;

public class SolicitarCertificacionTgssCommandValidator : AbstractValidator<SolicitarCertificacionTgssCommand>
{
    public SolicitarCertificacionTgssCommandValidator()
    {
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.ClienteId).NotEmpty();

        RuleFor(c => c.FechaSolicitud)
            .Must(f => f <= DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de solicitud no puede ser futura.");

        RuleFor(c => c.Observaciones)
            .MaximumLength(SolicitudCertificacionTgss.LongitudMaximaObservaciones)
            .WithMessage($"Las observaciones no pueden superar {SolicitudCertificacionTgss.LongitudMaximaObservaciones} caracteres.");
    }
}

public class SolicitarCertificacionTgssCommandHandler(
    ISolicitudCertificacionTgssRepository repositorio,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<SolicitarCertificacionTgssCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SolicitarCertificacionTgssCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("CertificacionTgss.ClienteNoEncontrado", "No encontramos este cliente."));

        // Defensa en profundidad (REC-149): este Command ya es inalcanzable
        // para el rol Cliente vía AutorizacionEscrituraBehavior (lista blanca
        // de roles de escritura), pero el alcance de gestión es la puerta
        // correcta igualmente — falla por dos motivos independientes en vez
        // de uno.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("CertificacionTgss.EmpresaNoEncontrada", "No encontramos esta empresa."));

        // No exigimos VigenciaHasta == null a propósito: la responsabilidad
        // solidaria del art. 42.2 ET se extiende 3 años DESPUÉS de que
        // termine el encargo, así que una solicitud tardía sobre una
        // RelacionEmpresarial ya cerrada sigue siendo legítima — basta con
        // que la relación existiera en la fecha de la solicitud, no que
        // siga abierta hoy.
        var finDelDiaSolicitud = DateTime.SpecifyKind(request.FechaSolicitud.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc);

        var sonEmpresaYClienteRelacionados = await empresasContext.RelacionesEmpresariales
            .AnyAsync(r => r.ProveedoraId == request.EmpresaId && r.ClienteId == request.ClienteId
                && r.VigenciaDesde <= finDelDiaSolicitud, cancellationToken);

        if (!sonEmpresaYClienteRelacionados)
            return Result.Fallo<Guid>(Error.Crear(
                "CertificacionTgss.SinRelacion", "Esta empresa no trabaja (ni trabajó) para este cliente."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("CertificacionTgss.SinUsuario", "No pudimos identificar al usuario solicitante."));

        var solicitud = new SolicitudCertificacionTgss(
            request.EmpresaId, request.ClienteId, request.FechaSolicitud, usuarioId.Value, request.Observaciones);

        repositorio.Agregar(solicitud);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(solicitud.Id);
    }
}
