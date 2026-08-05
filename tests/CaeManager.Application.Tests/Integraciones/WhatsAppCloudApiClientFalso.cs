using CaeManager.Application.Integraciones;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Fake en memoria — los handlers/servicios de Application se prueban sin red (ver CODING_STANDARDS.md).</summary>
public class WhatsAppCloudApiClientFalso : IWhatsAppCloudApiClient
{
    public NotificacionWhatsAppDto NotificacionADevolver { get; set; } = new("111222333", [], []);
    public List<(string PhoneNumberId, string Telefono, string Texto)> EnviosRealizados { get; } = [];
    public bool FallarEnvios { get; set; }
    public MediaDescargadoWhatsAppDto? MediaADevolver { get; set; }

    private int _contadorWamid;

    public IReadOnlyList<string> ExtraerPhoneNumberIds(string payloadJson) => [NotificacionADevolver.PhoneNumberId];

    public NotificacionWhatsAppDto ExtraerNotificacion(string payloadJson, string phoneNumberId) => NotificacionADevolver;

    public Task<Result<string>> EnviarTextoAsync(
        string tokenAcceso, string phoneNumberId, string telefonoDestino, string texto, CancellationToken cancellationToken)
    {
        if (FallarEnvios)
            return Task.FromResult(Result.Fallo<string>(Error.Crear("Integraciones.WhatsApp.ErrorEnvio", "Fallo simulado.")));

        EnviosRealizados.Add((phoneNumberId, telefonoDestino, texto));
        return Task.FromResult(Result.Exito($"wamid.enviado-{++_contadorWamid}"));
    }

    public Task<Result<MediaDescargadoWhatsAppDto>> DescargarMediaAsync(
        string tokenAcceso, string mediaId, CancellationToken cancellationToken) =>
        Task.FromResult(MediaADevolver is { } media
            ? Result.Exito(media)
            : Result.Fallo<MediaDescargadoWhatsAppDto>(Error.Crear("Integraciones.WhatsApp.ErrorMedia", "Sin media simulado.")));
}
