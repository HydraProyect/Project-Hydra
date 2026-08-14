namespace CaeManager.Infrastructure.VigilanciaNormativa;

/// <summary>
/// Interruptor del sondeo del sumario diario del BOE (tramo 1 bis del
/// MVP-1 de formatos, corte mínimo).
///
/// <b>Apagado por defecto.</b> A diferencia de otros <c>BackgroundService</c>
/// de este proyecto (p. ej. <c>VigilanciaVisitasUrgentesHostedService</c>),
/// este sí necesita interruptor propio porque llama a un servicio externo
/// (boe.es) en cada ciclo — mismo criterio que separa
/// <c>Microsoft365GraphOptions</c>/<c>WhatsAppCloudApiOptions</c> (mandan
/// algo fuera de la aplicación) de los que se quedan dentro. Sin este
/// interruptor, cada arranque de la app — incluidos los tests E2E, que
/// levantan el binario real (ver WebAppFixture) — dispararía una llamada de
/// red real a un tercero.
/// </summary>
public class VigilanciaNormativaBoeOptions
{
    public const string SeccionConfiguracion = "VigilanciaNormativaBoe";

    public bool Activa { get; set; }
}
