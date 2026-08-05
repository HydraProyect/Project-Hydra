using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.ActualizarLineaWhatsApp;

/// <summary>
/// Edición de una línea WhatsApp: rotación de token (null = conservar el
/// actual), modo de asignación, pool y mensaje de auto-triage. Version es la
/// de la ConexionIntegracion (el agregado raíz editable) que vio el usuario
/// — regla de concurrencia de CLAUDE.md.
/// </summary>
public record ActualizarLineaWhatsAppCommand(
    Guid LineaId,
    Guid Version,
    string? NuevoToken,
    ModoAsignacionLinea Modo,
    Guid? ComercialAsignadoId,
    IReadOnlyList<Guid> MiembrosPool,
    string? MensajeAutoTriage) : ICommand;

public class ActualizarLineaWhatsAppCommandValidator : AbstractValidator<ActualizarLineaWhatsAppCommand>
{
    public ActualizarLineaWhatsAppCommandValidator()
    {
        RuleFor(c => c.LineaId).NotEmpty();
        RuleFor(c => c.MensajeAutoTriage).MaximumLength(LineaWhatsApp.LongitudMaximaMensajeAutoTriage);

        RuleFor(c => c.ComercialAsignadoId).NotEmpty()
            .When(c => c.Modo == ModoAsignacionLinea.GestorFijo)
            .WithMessage("Una línea con gestor fijo necesita el comercial dueño.");
        RuleFor(c => c.MiembrosPool).NotEmpty()
            .When(c => c.Modo == ModoAsignacionLinea.PoolInbound)
            .WithMessage("Una línea en modo pool necesita al menos un gestor participante.");
    }
}

public class ActualizarLineaWhatsAppCommandHandler(
    ILineaWhatsAppRepository lineaRepositorio,
    IConexionIntegracionRepository conexionRepositorio,
    IDirectorioUsuariosService directorioUsuarios,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarLineaWhatsAppCommand, Result>
{
    public async Task<Result> Handle(ActualizarLineaWhatsAppCommand request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolActualAsync() != "Administrador")
            return Result.Fallo(Error.Crear(
                "Autorizacion.SoloAdministrador", "Solo un administrador puede configurar líneas de WhatsApp."));

        var linea = await lineaRepositorio.ObtenerPorIdAsync(request.LineaId, cancellationToken);
        if (linea is null)
            return Result.Fallo(Error.Crear("Integraciones.WhatsApp.LineaNoEncontrada", "No encontramos esta línea."));

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(linea.ConexionIntegracionId, cancellationToken);
        if (conexion is null)
            return Result.Fallo(Error.Crear("Integraciones.WhatsApp.LineaNoEncontrada", "No encontramos esta línea."));

        if (ConcurrenciaOptimista.Verificar(conexion, request.Version, "esta línea de WhatsApp") is { } conflicto)
            return Result.Fallo(conflicto);

        foreach (var usuarioId in UsuariosReferidos(request))
        {
            if (!await directorioUsuarios.EsVisibleEnTenantActualAsync(usuarioId, cancellationToken))
                return Result.Fallo(Error.Crear(
                    "Integraciones.WhatsApp.UsuarioNoVisible", "Uno de los gestores elegidos no pertenece a esta organización."));
        }

        if (!string.IsNullOrWhiteSpace(request.NuevoToken))
            linea.ActualizarToken(request.NuevoToken);

        linea.ConfigurarAsignacion(request.Modo, request.ComercialAsignadoId);
        linea.ReemplazarMiembrosPool(request.Modo == ModoAsignacionLinea.PoolInbound ? request.MiembrosPool : []);
        linea.EstablecerMensajeAutoTriage(request.MensajeAutoTriage);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private static IEnumerable<Guid> UsuariosReferidos(ActualizarLineaWhatsAppCommand request)
    {
        if (request.ComercialAsignadoId is { } comercial) yield return comercial;
        foreach (var miembro in request.MiembrosPool.Distinct()) yield return miembro;
    }
}
