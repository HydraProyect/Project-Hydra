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
///
/// <para>
/// Con <see cref="DirectorioEntraId"/> el buzón es un compartido de Exchange de
/// ese directorio: <see cref="AccessToken"/> es el token de aplicación del
/// directorio (el endpoint ya comprobó con él que el buzón está dentro del
/// ámbito de Exchange), no hay <see cref="RefreshToken"/> ni
/// <see cref="CredencialIntegracion"/>, y la suscripción va sobre
/// <c>users/{buzón}</c>. La conexión, la suscripción y la reclamación del
/// buzón pertenecen al Tenant de la sesión, igual que con el buzón propio
/// (DN-3).
/// </para>
/// <para>
/// <see cref="IdentidadBuzon"/> es la identidad estable del buzón (Id inmutable de su Bandeja de entrada),
/// leída con el mismo token que lo abre: la reclamación global se hace sobre ella y no solo sobre la
/// dirección, para que un alias del mismo buzón no pueda conectarse en otro Tenant.
/// </para>
/// </summary>
public record ConectarBuzonMicrosoft365Command(
    string BuzonEmail, string Nombre, Guid? ClienteId, string AccessToken, string? RefreshToken, string NotificationUrlBase,
    string IdentidadBuzon, Guid? GestorPropietarioId = null, string? DirectorioEntraId = null, Guid? ConsentidoPorUsuarioId = null)
    : ICommand<Guid>
{
    public bool EsBuzonCompartido => DirectorioEntraId is not null;
}

public class ConectarBuzonMicrosoft365CommandValidator : AbstractValidator<ConectarBuzonMicrosoft365Command>
{
    public ConectarBuzonMicrosoft365CommandValidator()
    {
        RuleFor(c => c.BuzonEmail).NotEmpty().EmailAddress().WithMessage("El buzón debe ser un correo válido.");
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("La conexión debe tener un nombre.");
        RuleFor(c => c.AccessToken).NotEmpty();
        RuleFor(c => c.IdentidadBuzon).NotEmpty().MaximumLength(ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon);
        RuleFor(c => c.RefreshToken).NotEmpty().When(c => !c.EsBuzonCompartido);
        RuleFor(c => c.RefreshToken).Null().When(c => c.EsBuzonCompartido)
            .WithMessage("Un buzón compartido no guarda el refresh token de nadie.");
        RuleFor(c => c.ConsentidoPorUsuarioId).NotNull().When(c => c.EsBuzonCompartido);
        RuleFor(c => c.NotificationUrlBase).NotEmpty();
        RuleFor(c => c).Must(c => c.ClienteId is null || (c.GestorPropietarioId is null && !c.EsBuzonCompartido))
            .WithMessage("Un buzón de una Empresa no puede ser personal de un Gestor CAE ni compartido: el compartido es el de la organización.");
        RuleFor(c => c).Must(c => !c.EsBuzonCompartido || c.GestorPropietarioId is null)
            .WithMessage("Un buzón compartido no puede ser el buzón personal de un Gestor CAE.");
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
        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md.
        if (request.ClienteId is { } clienteId)
        {
            var cliente = await empresaRepositorio.ObtenerPorIdAsync(clienteId, cancellationToken);
            if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este Cliente."));
        }

        if (request.GestorPropietarioId is { } gestorId && !await directorioUsuarios.EsVisibleEnTenantActualAsync(gestorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Integraciones.Microsoft365.GestorNoVisible", "El gestor elegido no pertenece a esta organización."));

        // La conexión aún no está guardada: TenantSelladoInterceptor solo
        // estampa TenantId en el SaveChangesAsync de más abajo, así que la
        // reclamación necesita el Tenant de la sesión explícito (DN-3, el
        // buzón conectado pertenece siempre al Tenant propietario).
        if (tenantActual.TenantId is not { } tenantId)
            return Result.Fallo<Guid>(Error.Crear("Integraciones.Microsoft365.TenantNoResuelto", "No se pudo determinar la organización actual."));

        // Sin esta comprobación, repetir el flujo OAuth para un buzón que el
        // propio Tenant ya tiene conectado chocaría contra el índice único
        // GLOBAL de ReclamacionBuzonIntegracion (por diseño, ni siquiera
        // reconoce a su propio dueño) y devolvería el mensaje de "otra
        // organización" — engañoso cuando la organización es la misma.
        //
        // Excluye Deshabilitada (hallazgo P2, revisión Codex sobre PR #820):
        // DesconectarBuzonCommand libera la reclamación pero NO borra la
        // ConexionIntegracion — queda Deshabilitada. Bloquear también ese
        // caso impediría reconectar desde cero cuando la suscripción de
        // Graph anterior ya no existe, el único camino que
        // ReactivarConexionCommand documenta para ese escenario (no crea una
        // suscripción nueva por sí solo).
        var buzonNormalizado = request.BuzonEmail.Trim().ToLowerInvariant();
        var identidad = request.IdentidadBuzon;
        if (await queryContext.ConexionesIntegracion.AnyAsync(
                c => (c.BuzonEmail.ToLower() == buzonNormalizado || c.IdentidadBuzon == identidad) &&
                     c.Estado != EstadoConexionIntegracion.Deshabilitada,
                cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Integraciones.Microsoft365.BuzonYaConectadoEnEstaOrganizacion",
                "Ya tienes una conexión con este buzón en esta organización. Desconéctala antes de volver a conectarla."));

        ConexionIntegracion conexion;
        if (request.DirectorioEntraId is { } directorioEntraId)
        {
            conexion = ConexionIntegracion.CrearBuzonCompartido(
                request.BuzonEmail, request.Nombre, directorioEntraId, request.ConsentidoPorUsuarioId!.Value);
            conexionRepositorio.Agregar(conexion);
        }
        else
        {
            conexion = new ConexionIntegracion(
                request.BuzonEmail, request.Nombre, request.ClienteId, gestorPropietarioId: request.GestorPropietarioId,
                consentidoPorUsuarioId: request.ConsentidoPorUsuarioId);
            conexionRepositorio.Agregar(conexion);
            credencialRepositorio.Agregar(new CredencialIntegracion(conexion.Id, request.RefreshToken!));
        }

        conexion.FijarIdentidadBuzon(identidad);

        // Secreto propio de Hydra, nunca elegido por Graph — se guarda
        // cifrado en SuscripcionWebhook y se compara en cada notificación
        // entrante (ver Project-Hydra-Negocio/tecnico/docs/MULTITENANCY.md § 8, tercer modo).
        var clientState = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var notificationUrl = $"{request.NotificationUrlBase.TrimEnd('/')}/api/integraciones/webhooks/microsoft365/{conexion.Id}";

        var suscripcionResultado = await graphClient.CrearSuscripcionAsync(
            request.AccessToken, BuzonGraph.De(conexion), notificationUrl, clientState, cancellationToken);
        if (suscripcionResultado.EsFallido)
            return Result.Fallo<Guid>(suscripcionResultado.Error);

        var graphSubscriptionId = suscripcionResultado.Valor.GraphSubscriptionId;
        suscripcionRepositorio.Agregar(new SuscripcionWebhook(
            conexion.Id, graphSubscriptionId, clientState, suscripcionResultado.Valor.FechaExpiracionUtc));

        // Unicidad global del buzón (incremento 1 de PROPUESTA-BUZONES-COMPARTIDOS-M365
        // § 5.4): sin esta reclamación, el mismo buzón podía conectarse en dos
        // Tenants a la vez, con la misma dirección o con un alias suyo.
        reclamacionRepositorio.Reclamar(new ReclamacionBuzonIntegracion(request.BuzonEmail, identidad, tenantId, conexion.Id, DateTime.UtcNow));

        bool buzonLibre;
        try
        {
            buzonLibre = await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken);
        }
        catch
        {
            await CompensarSuscripcionHuerfanaAsync(graphSubscriptionId);
            throw;
        }

        if (!buzonLibre)
        {
            await CompensarSuscripcionHuerfanaAsync(graphSubscriptionId);
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
