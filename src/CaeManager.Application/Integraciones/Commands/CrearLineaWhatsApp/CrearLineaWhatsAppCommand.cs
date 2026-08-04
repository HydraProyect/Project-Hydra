using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;

/// <summary>
/// Alta de una línea WhatsApp: crea la pareja ConexionIntegracion (proveedor
/// WhatsApp) + LineaWhatsApp. Restringido a Administrador — la línea lleva el
/// System User token de Meta, una credencial de plataforma.
/// </summary>
public record CrearLineaWhatsAppCommand(
    string Nombre,
    string NumeroTelefono,
    string PhoneNumberId,
    string WabaId,
    string TokenAcceso,
    ModoAsignacionLinea Modo,
    Guid? ComercialAsignadoId,
    IReadOnlyList<Guid> MiembrosPool,
    Guid? ClienteId,
    string? MensajeAutoTriage) : ICommand<Guid>;

public class CrearLineaWhatsAppCommandValidator : AbstractValidator<CrearLineaWhatsAppCommand>
{
    public CrearLineaWhatsAppCommandValidator()
    {
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("La línea debe tener un nombre.")
            .MaximumLength(ConexionIntegracion.LongitudMaximaNombre);
        RuleFor(c => c.NumeroTelefono).NotEmpty().WithMessage("La línea debe tener un número de teléfono.")
            .MaximumLength(LineaWhatsApp.LongitudMaximaNumeroTelefono)
            .Matches(@"^\+\d{7,15}$").WithMessage("El número debe ir en formato internacional, p. ej. +34686543364.");
        RuleFor(c => c.PhoneNumberId).NotEmpty().WithMessage("Falta el identificador de número de Meta (Phone Number ID).")
            .MaximumLength(LineaWhatsApp.LongitudMaximaPhoneNumberId);
        RuleFor(c => c.WabaId).NotEmpty().WithMessage("Falta el identificador de la cuenta de WhatsApp Business (WABA ID).")
            .MaximumLength(LineaWhatsApp.LongitudMaximaWabaId);
        RuleFor(c => c.TokenAcceso).NotEmpty().WithMessage("Falta el token de acceso de la línea.");
        RuleFor(c => c.MensajeAutoTriage).MaximumLength(LineaWhatsApp.LongitudMaximaMensajeAutoTriage);

        RuleFor(c => c.ComercialAsignadoId).NotEmpty()
            .When(c => c.Modo == ModoAsignacionLinea.GestorFijo)
            .WithMessage("Una línea con gestor fijo necesita el comercial dueño.");
        RuleFor(c => c.MiembrosPool).NotEmpty()
            .When(c => c.Modo == ModoAsignacionLinea.PoolInbound)
            .WithMessage("Una línea en modo pool necesita al menos un gestor participante.");
    }
}

public class CrearLineaWhatsAppCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    ILineaWhatsAppRepository lineaRepositorio,
    IIntegracionesQueryContext integracionesContext,
    IClienteRepository clienteRepositorio,
    IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearLineaWhatsAppCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearLineaWhatsAppCommand request, CancellationToken cancellationToken)
    {
        // Mismo criterio que el alta de delegaciones (P0-7): configurar una
        // credencial de plataforma es cosa del Administrador, no de la
        // lista blanca general de escritura.
        if (await currentUserService.ObtenerRolActualAsync() != "Administrador")
            return Result.Fallo<Guid>(Error.Crear(
                "Autorizacion.SoloAdministrador", "Solo un administrador puede configurar líneas de WhatsApp."));

        // El índice único global de PhoneNumberId protege entre tenants; esta
        // comprobación solo da un mensaje amable en el caso normal (el índice
        // seguiría rechazando el duplicado cross-tenant en SaveChanges).
        if (await integracionesContext.LineasWhatsApp.AnyAsync(l => l.PhoneNumberId == request.PhoneNumberId.Trim(), cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Integraciones.WhatsApp.LineaDuplicada", "Ya existe una línea con ese Phone Number ID."));

        if (request.ClienteId is { } clienteId)
        {
            var cliente = await clienteRepositorio.ObtenerPorIdAsync(clienteId, cancellationToken);
            if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        foreach (var usuarioId in UsuariosReferidos(request))
        {
            if (!await directorioUsuarios.EsVisibleEnTenantActualAsync(usuarioId, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear(
                    "Integraciones.WhatsApp.UsuarioNoVisible", "Uno de los gestores elegidos no pertenece a esta organización."));
        }

        var conexion = new ConexionIntegracion(
            request.NumeroTelefono.Trim(), request.Nombre.Trim(), request.ClienteId, ProveedorIntegracion.WhatsApp);
        conexionRepositorio.Agregar(conexion);

        var linea = new LineaWhatsApp(
            conexion.Id, request.PhoneNumberId, request.WabaId, request.NumeroTelefono, request.TokenAcceso,
            request.Modo, request.ComercialAsignadoId, request.MensajeAutoTriage);
        if (request.Modo == ModoAsignacionLinea.PoolInbound)
            linea.ReemplazarMiembrosPool(request.MiembrosPool);
        lineaRepositorio.Agregar(linea);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(linea.Id);
    }

    private static IEnumerable<Guid> UsuariosReferidos(CrearLineaWhatsAppCommand request)
    {
        if (request.ComercialAsignadoId is { } comercial) yield return comercial;
        foreach (var miembro in request.MiembrosPool.Distinct()) yield return miembro;
    }
}
