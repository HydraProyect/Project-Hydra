using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;

/// <summary>
/// Persiste una conexión nueva a un buzón de Microsoft 365 ya consentido —
/// el canje de "code" por tokens ocurre en el endpoint del callback OAuth
/// (Web), antes de construir este Command, porque necesita el
/// <c>redirectUri</c> exacto de la petición HTTP entrante. Este Command
/// recibe los tokens ya obtenidos.
/// </summary>
public record ConectarBuzonMicrosoft365Command(
    string BuzonEmail, string Nombre, Guid? ClienteId, string AccessToken, string RefreshToken, string NotificationUrlBase)
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
    }
}

public class ConectarBuzonMicrosoft365CommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    ICredencialIntegracionRepository credencialRepositorio,
    ISuscripcionWebhookRepository suscripcionRepositorio,
    IClienteRepository clienteRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ConectarBuzonMicrosoft365Command, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ConectarBuzonMicrosoft365Command request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        if (request.ClienteId is { } clienteId)
        {
            var cliente = await clienteRepositorio.ObtenerPorIdAsync(clienteId, cancellationToken);
            if (cliente is null || !await alcanceDatos.ClienteVisibleAsync(cliente.Id, cancellationToken))
                return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));
        }

        var conexion = new ConexionIntegracion(request.BuzonEmail, request.Nombre, request.ClienteId);
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

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(conexion.Id);
    }
}
