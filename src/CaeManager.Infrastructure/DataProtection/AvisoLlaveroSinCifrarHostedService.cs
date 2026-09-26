using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.DataProtection;

/// <summary>
/// Solo se registra cuando Production arranca con el llavero de Data Protection
/// SIN CIFRAR porque así lo permite la bandera transitoria
/// <see cref="RegistroDataProtection.ClavePermitirClavesSinCifrar"/> (P1-F3).
/// En cada arranque deja un aviso en el log y una alerta operativa que nombra
/// la bandera: mientras siga activa no puede quedarse en silencio, o la
/// transición se vuelve permanente sin que nadie lo decida.
/// </summary>
public class AvisoLlaveroSinCifrarHostedService(
    IAlertaOperativa alertaOperativa,
    ILogger<AvisoLlaveroSinCifrarHostedService> logger) : IHostedService
{
    internal const string Mensaje =
        "Production arranca con las claves de Data Protection SIN CIFRAR porque " +
        RegistroDataProtection.ClavePermitirClavesSinCifrar + "=true. Es una bandera de transición (P1-F3): " +
        "monta el certificado en /run/secretos, informa DataProtection:Certificado:CertificadoRuta y " +
        "ClavePrivadaRuta, y retira la bandera.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // P41c: lo que haga este servicio de fondo lo hace la propia plataforma.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        logger.LogWarning("{Aviso}", Mensaje);
        alertaOperativa.Emitir(Mensaje, NivelAlertaOperativa.Aviso);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
