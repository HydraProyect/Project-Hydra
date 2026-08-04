namespace CaeManager.Application.Integraciones;

/// <summary>
/// Señal en memoria que despierta al consumidor de la cola de WhatsApp sin
/// esperar al tick de sondeo — la latencia de un chat no tolera los 10 s del
/// patrón de correo. NO transporta datos y NO reintroduce el problema por el
/// que se eliminó <c>System.Threading.Channels</c> del repositorio (encargos
/// perdidos al reiniciar): la fuente de verdad sigue siendo la cola durable
/// <c>EventoWebhook</c> en PostgreSQL; perder una señal (reinicio, o el
/// webhook cayó en una réplica que no es la líder) cuesta como máximo un
/// tick de sondeo. Costura multi-réplica: cuando haga falta señal
/// cross-proceso, esta interfaz se reimplementa sobre pub/sub de Redis
/// (SignalRRedisOptions ya modela el backplane) sin tocar a los llamadores.
/// </summary>
public interface ISenalIngestaWhatsApp
{
    void Despertar();

    /// <summary>Espera hasta que alguien llame a <see cref="Despertar"/> o venza <paramref name="maximo"/> — lo que ocurra antes.</summary>
    Task EsperarAsync(TimeSpan maximo, CancellationToken cancellationToken);
}
