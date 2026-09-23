using CaeManager.Application.Common;
using System.Security.Cryptography;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;

/// <summary>
/// Persiste una conexión nueva a un buzón de Microsoft 365 ya consentido —
/// el canje de "code" por tokens ocurre en el endpoint del callback OAuth
/// (Web), antes de construir este Command, porque necesita el
/// <c>redirectUri</c> exacto de la petición HTTP entrante. Este Command
/// recibe los tokens ya obtenidos.
/// </summary>
public record ConectarBuzonMicrosoft365Command(
    string BuzonEmail, string Nombre, Guid? ClienteId, string AccessToken, string RefreshToken, string NotificationUrlBase,
    Guid? GestorPropietarioId = null)
    : ICommand<Guid>;

public class ConectarBuzonMicrosoft365CommandValidator : AbstractValidator<ConectarBuzonMicrosoft365Command>
{
    public ConectarBuzonMicrosoft365CommandValidator()
    {
        RuleFor(c => c.BuzonEmail).NotEmpty().EmailAddress().WithMessage("El buzón debe ser un correo válido.");
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("La conexión debe tener un nombre.");
        RuleFor(c => c.AccessToken).NotEmpty();
        RuleFor(c => c.RefreshToken).NotEmpty();
        RuleFor(c => c.NotificationUrlBase).NotEmpty();
        RuleFor(c => c).Must(c => c.ClienteId is null || c.GestorPropietarioId is null)
            .WithMessage("Un buzón no puede ser a la vez de un Cliente y personal de un gestor.");
    }
}

public class ConectarBuzonMicrosoft365CommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    ICredencialIntegracionRepository credencialRepositorio,
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    IIntegracionesQueryContext queryContext,
    IEmpresaRepository empresaRepositorio,
    IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios,
    IMicrosoft365GraphClient graphClient,
    ITenantActual tenantActual,
    ILogger<ConectarBuzonMicrosoft365CommandHandler> logger)
    : IRequestHandler<ConectarBuzonMicrosoft365Command, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ConectarBuzonMicrosoft365Command request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (request.ClienteId is { } clienteId)
        {
            var cliente = await empresaRepositorio.ObtenerPorIdAsync(clienteId, cancellationToken);
            if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        if (request.GestorPropietarioId is { } gestorId && !await directorioUsuarios.EsVisibleEnTenantActualAsync(gestorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Integraciones.Microsoft365.GestorNoVisible", "El gestor elegido no pertenece a esta organización."));

        // La conexión aún no está guardada: TenantSelladoInterceptor solo
        // estampa TenantId en el SaveChangesAsync de más abajo, así que la
        // reclamación necesita el Tenant de la sesión explícito (DN-3, el
        // buzón conectado pertenece siempre al Tenant propietario).
        if (tenantActual.TenantId is not { } tenantId)
            return Result.Fallo<Guid>(Error.Crear("Integraciones.Microsoft365.TenantNoResuelto", "No se pudo determinar el tenant actual."));

        // Sin esta comprobación, repetir el flujo OAuth para un buzón que el
        // propio Tenant ya tiene conectado chocaría contra el índice único
        // GLOBAL de ReclamacionBuzonIntegracion (por diseño, ni siquiera
        // reconoce a su propio dueño) y devolvería el mensaje de "otra
        // organización" — engañoso cuando la organización es la misma.
        var buzonNormalizado = request.BuzonEmail.Trim().ToLowerInvariant();
        if (await queryContext.ConexionesIntegracion.AnyAsync(c => c.BuzonEmail.ToLower() == buzonNormalizado, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Integraciones.Microsoft365.BuzonYaConectadoEnEstaOrganizacion",
                "Ya tienes una conexión con este buzón en esta organización. Desconéctala antes de volver a conectarla."));

        var conexion = new ConexionIntegracion(
            request.BuzonEmail, request.Nombre, request.ClienteId, gestorPropietarioId: request.GestorPropietarioId);
        conexionRepositorio.Agregar(conexion);
        credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, request.RefreshToken));

        // Secreto propio de Hydra, nunca elegido por Graph — se guarda
        // cifrado en SuscripcionWebhook y se compara en cada notificación
        // entrante (ver docs/MULTITENANCY.md § 8, tercer modo).
        var clientState = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var notificationUrl = $"{request.NotificationUrlBase.TrimEnd('/')}/api/integraciones/webhooks/microsoft365/{conexion.Id}";

        var suscripcionResultado = await graphClient.CrearSuscripcionAsync(
            request.AccessToken, request.BuzonEmail, notificationUrl, clientState, cancellationToken);
        if (suscripcionResultado.EsFallido)
            return Result.Fallo<Guid>(suscripcionResultado.Error);

        suscripcionRepositorio.Agregar(new SuscripcionWebhook(
            conexion.Id, suscripcionResultado.Valor.GraphSubscriptionId, clientState, suscripcionResultado.Valor.FechaExpiracionUtc));

        // Unicidad global del buzón (incremento 1 de PROPUESTA-BUZONES-COMPARTIDOS-M365
        // § 5.4): sin esta reclamación, el mismo BuzonEmail podía conectarse
        // en dos Tenants a la vez.
        reclamacionRepositorio.Reclamar(new ReclamacionBuzonIntegracion(request.BuzonEmail, tenantId, conexion.Id, DateTime.UtcNow));

        bool buzonLibre;
        try
        {
            buzonLibre = await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken);
        }
        catch
        {
            await CompensarSuscripcionHuerfanaAsync(suscripcionResultado.Valor.GraphSubscriptionId);
            throw;
        }

        if (!buzonLibre)
        {
            await CompensarSuscripcionHuerfanaAsync(suscripcionResultado.Valor.GraphSubscriptionId);
            return Result.Fallo<Guid>(Error.Crear(
                "Integraciones.Microsoft365.BuzonYaConectado", "Este buzón ya está conectado en otra organización."));
        }

        return Result.Exito(conexion.Id);

        // Compensación best-effort (auditoría módulo 6): si la conexión no se
        // pudo persistir localmente —por fallo o porque el buzón ya estaba
        // reclamado—, no dejar una suscripción huérfana en Graph que Hydra
        // nunca sabrá que existe — mismo criterio que el borrador huérfano de
        // EnviarNuevoMensajeAsync.
        async Task CompensarSuscripcionHuerfanaAsync(string graphSubscriptionId)
        {
            try
            {
                await graphClient.EliminarSuscripcionAsync(request.AccessToken, graphSubscriptionId, cancellationToken);
            }
            catch (Exception exCompensacion)
            {
                logger.LogWarning(exCompensacion,
                    "No se pudo eliminar la suscripción huérfana {SubscriptionId} tras un fallo al conectar el buzón.",
                    graphSubscriptionId);
            }
        }
    }
}
