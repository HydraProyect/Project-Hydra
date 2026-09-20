using CaeManager.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Integraciones;

/// <summary>
/// Adelanta al arranque la señal que, si no, el conector de Microsoft 365 solo daría
/// tarde y en un log que nadie mira hasta que alguien pregunta por qué no entra el correo:
/// <b>certificado configurado pero ilegible.</b> Con las dos rutas informadas la
/// configuración parece completa, los servicios se registran y el fallo sale en cada
/// canje o renovación como <c>CertificadoNoLegible</c>. El caso típico es un PEM creado
/// <c>root:root</c> con modo 600: el proceso corre sin privilegios y no puede abrirlo.
///
/// <para>
/// La otra señal de arranque del conector, <b>la configuración a medias</b>, ya no vive
/// aquí: la avisa el mecanismo genérico <c>AvisarSiConfiguracionAMedias</c>
/// (<see cref="Microsoft365GraphOptions"/> implementa <c>IOpcionesConGate</c>), como el
/// resto de gates. Este servicio solo se registra con certificado configurado; una
/// configuración completa e inservible no es un gate «a medias» y el mecanismo genérico
/// no sabe expresarla.
/// </para>
///
/// Reglas: (a) <b>solo legibilidad</b> — abre el fichero para leer y lo cierra; no carga
/// el certificado ni la clave privada, así que no hay material de clave en memoria en un
/// camino que no lo necesita; (b) <b>nunca fatal</b> — un aviso, jamás una excepción ni
/// un arranque abortado: si el fichero aparece más tarde (una rotación, un montaje que
/// tarda) la aplicación sigue levantando y el canje reintentará; (c) <b>sin valores ni
/// contenido</b> — nombra la opción y la categoría del motivo, nunca la ruta ni la
/// excepción. No cambia cuándo se registran la ingesta ni la renovación.
/// </summary>
public class AvisoConfiguracionMicrosoft365HostedService(
    IOptions<Microsoft365GraphOptions> opciones,
    ILogger<AvisoConfiguracionMicrosoft365HostedService> logger,
    Func<string, Stream>? abrirParaLectura = null) : IHostedService
{
    private readonly Func<string, Stream> _abrirParaLectura = abrirParaLectura ?? AbrirSoloParaLeer;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // P41c: ver ServiciosDeFondoDeclaranActorDeSistemaTests. Este servicio no
        // escribe entidades, pero el trinquete no admite excepciones por «este no
        // escribe»: caducaría el día que escribiera.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        try
        {
            var config = opciones.Value;

            if (config.UsaCertificado)
            {
                AvisarSiNoSePuedeLeer(nameof(Microsoft365GraphOptions.CertificadoRuta), config.CertificadoRuta!);
                AvisarSiNoSePuedeLeer(nameof(Microsoft365GraphOptions.ClavePrivadaRuta), config.ClavePrivadaRuta!);
            }
        }
        catch (Exception ex)
        {
            // Un aviso de arranque no puede tumbar el arranque. Solo el tipo: el mensaje
            // de una excepción de E/S suele nombrar la ruta.
            logger.LogWarning(
                "Conector de Microsoft 365: no se pudo completar la comprobación de arranque de su configuración ({Tipo}).",
                ex.GetType().Name);
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void AvisarSiNoSePuedeLeer(string opcion, string ruta)
    {
        string? motivo;
        try
        {
            using var _ = _abrirParaLectura(ruta);
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            motivo = "el fichero no existe";
        }
        catch (UnauthorizedAccessException)
        {
            motivo = "el proceso no tiene permiso de lectura (revisa propietario y modo del PEM: la aplicación no corre como root)";
        }
        catch (Exception)
        {
            motivo = "no se pudo abrir el fichero";
        }

        logger.LogWarning(
            "Conector de Microsoft 365: {Opcion} apunta a un fichero que no se puede leer ({Motivo}). " +
            "Los servicios de correo están registrados, pero cada canje o renovación de tokens fallará con " +
            "CertificadoNoLegible hasta que se corrija; si el fichero aparece más tarde, el siguiente intento lo usará.",
            opcion, motivo);
    }

    /// <summary>Abre y devuelve el flujo sin leer un solo byte: el cierre (Dispose) es cosa del llamante.</summary>
    private static Stream AbrirSoloParaLeer(string ruta) =>
        new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
