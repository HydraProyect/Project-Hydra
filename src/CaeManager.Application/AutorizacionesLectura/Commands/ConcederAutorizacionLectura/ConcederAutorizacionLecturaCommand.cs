using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AutorizacionesLectura.Commands.ConcederAutorizacionLectura;

/// <summary>
/// El Tenant de la Empresa contratista autoriza al Tenant del Cliente empresarial a leer, para un contrato de acceso,
/// los Documentos de estos Trabajadores y, si se marca, los de su Empresa propia.
/// </summary>
/// <param name="TenantDestinatarioId">El Tenant del Cliente empresarial. Cómo se conoce es la decisión DN-1, abierta: el comando lo recibe como parámetro y no hay pantalla.</param>
/// <param name="ContratoAccesoId">El contrato de acceso del Tenant destinatario; también por DN-1.</param>
/// <param name="VigenciaHasta">Último día autorizado; null = sin fecha de fin.</param>
public record ConcederAutorizacionLecturaCommand(
    Guid TenantDestinatarioId,
    Guid ContratoAccesoId,
    bool IncluyeDocumentosEmpresa,
    DateOnly VigenciaDesde,
    DateOnly? VigenciaHasta,
    IReadOnlyList<Guid> TrabajadorIds)
    : ICommand<Guid>;

public class ConcederAutorizacionLecturaCommandValidator : AbstractValidator<ConcederAutorizacionLecturaCommand>
{
    public ConcederAutorizacionLecturaCommandValidator()
    {
        RuleFor(c => c.TenantDestinatarioId).NotEmpty();
        RuleFor(c => c.ContratoAccesoId).NotEmpty();
        RuleFor(c => c.VigenciaHasta).GreaterThanOrEqualTo(c => c.VigenciaDesde).When(c => c.VigenciaHasta is not null)
            .WithMessage("La vigencia no puede terminar antes de empezar.");
        RuleFor(c => c.TrabajadorIds).NotNull();
        RuleForEach(c => c.TrabajadorIds).NotEmpty();
    }
}

/// <summary>
/// Alta de la autorización. Solo Administrador (<see cref="AutorizacionLecturaAutoridad"/>), y el rol se mira antes de
/// leer nada, para que quien no puede conceder tampoco pueda sondear qué Trabajadores existen.
/// </summary>
public class ConcederAutorizacionLecturaCommandHandler(
    IAutorizacionLecturaRepository repositorio,
    ITrabajadoresQueryContext trabajadoresContext,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    TimeProvider reloj,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ConcederAutorizacionLecturaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ConcederAutorizacionLecturaCommand request, CancellationToken cancellationToken)
    {
        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService)
            || await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || tenantActual.TenantId is not { } tenantId)
            return Result.Fallo<Guid>(AutorizacionLecturaAutoridad.SoloAdministrador);

        // El CHECK CK_AutorizacionesLectura_DestinatarioAjeno lo repite en la base; aquí sale con su mensaje.
        if (request.TenantDestinatarioId == tenantId)
            return Result.Fallo<Guid>(Error.Crear(
                "AutorizacionLectura.DestinatarioPropio", "No puedes autorizar la lectura a tu propia organización."));

        var trabajadorIds = request.TrabajadorIds.Distinct().ToList();
        if (!await TrabajadoresDelTenant.ExistenTodosAsync(trabajadoresContext, trabajadorIds, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionLecturaAutoridad.TrabajadorNoEncontrado);

        // El índice único filtrado sobre las no revocadas cierra la carrera entre dos altas simultáneas.
        if (await repositorio.ExisteNoRevocadaAsync(request.TenantDestinatarioId, request.ContratoAccesoId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "AutorizacionLectura.YaConcedida",
                "Ya hay una autorización de lectura sin revocar para este contrato: revócala antes de conceder otra."));

        AutorizacionLecturaDocumental autorizacion;
        try
        {
            autorizacion = AutorizacionLecturaDocumental.Conceder(
                request.TenantDestinatarioId, request.ContratoAccesoId, request.IncluyeDocumentosEmpresa,
                request.VigenciaDesde, request.VigenciaHasta, usuarioId, reloj.GetUtcNow().UtcDateTime);
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo<Guid>(Error.Crear("AutorizacionLectura.Invalida", ex.Message));
        }

        repositorio.Agregar(autorizacion);
        foreach (var trabajadorId in trabajadorIds)
            repositorio.AgregarTrabajador(autorizacion.AsignarTrabajador(trabajadorId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(autorizacion.Id);
    }
}
