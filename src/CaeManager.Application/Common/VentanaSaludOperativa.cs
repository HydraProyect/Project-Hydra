namespace CaeManager.Application.Common;

/// <summary>
/// Agrega los desenlaces que <see cref="LoggingBehavior{TRequest,TResponse}"/>
/// ya clasifica por request (fallo/éxito, lento/no) para las dos alertas por
/// tasa de Horizonte 2.4: tasa de error y latencia degradada. Instancia
/// única para todo el proceso (registrada Singleton, ver
/// <c>ApplicationServiceCollectionExtensions</c>) — igual que
/// <see cref="Observabilidad.ColaIaProfundidad"/>, la salud que describe es
/// la del proceso entero, no la de un tenant: con un único VPS y una sola
/// réplica (ver DEPLOY.md), separar por tenant multiplicaría los contadores
/// sin que la guardia de una persona vaya a mirar N ventanas en vez de una.
///
/// Deliberadamente en proceso, no delegado a las Signals/Apps nativas de
/// Seq (que ya recibe estos mismos datos como logs y métricas, Horizonte
/// 2.3): la alerta tiene que salir por Sentry, la misma decisión ya tomada
/// para las otras tres condiciones de 2.4 — Better Stack solo está
/// enganchado a Sentry hoy, no a Seq, y Seq no tiene un puente nativo hacia
/// Sentry a partir de una consulta sobre sus métricas. Montar ese puente
/// (Seq App → webhook → un endpoint propio que sí llame a Sentry) sería más
/// pieza móvil — y una más que se puede caer sin avisar — que estos
/// contadores. Esto tampoco es un rate-limiter: nunca descarta ni retrasa
/// ningún request, solo cuenta y evalúa al cerrar cada ventana.
/// </summary>
public sealed class VentanaSaludOperativa(IAlertaOperativa alertaOperativa)
{
    /// <summary>
    /// Ventana por número de requests, no por tiempo transcurrido: con
    /// tráfico bajo (fuera de horario, un piloto con pocos usuarios) una
    /// ventana de minutos tardaría en llenarse de verdad y evaluaría 2 o 3
    /// muestras sin significado estadístico; con tráfico alto sería
    /// demasiado lenta para avisar a tiempo. 50 requests seguidos es una
    /// unidad de trabajo que se completa en un tiempo proporcional a la
    /// carga real en ambos casos, y no necesita un reloj inyectado para
    /// poder probarse de forma determinista.
    /// </summary>
    private const int TamanoVentana = 50;

    private const double UmbralTasaErrores = 0.20;
    private const double UmbralTasaLentitud = 0.30;

    private readonly object _candado = new();
    private int _total;
    private int _errores;
    private int _lentos;

    /// <param name="fallo">El request terminó en excepción o en un Result fallido.</param>
    /// <param name="lento">
    /// El request superó el umbral de lentitud que <see cref="LoggingBehavior{TRequest,TResponse}"/>
    /// ya usa para decidir si registrarlo como aviso — mismo umbral, no uno nuevo.
    /// </param>
    public void RegistrarDesenlace(bool fallo, bool lento)
    {
        int total, errores, lentos;

        lock (_candado)
        {
            _total++;
            if (fallo) _errores++;
            if (lento) _lentos++;

            if (_total < TamanoVentana) return;

            total = _total;
            errores = _errores;
            lentos = _lentos;
            _total = 0;
            _errores = 0;
            _lentos = 0;
        }

        Evaluar(total, errores, lentos);
    }

    private void Evaluar(int total, int errores, int lentos)
    {
        var tasaErrores = (double)errores / total;
        if (tasaErrores >= UmbralTasaErrores)
        {
            alertaOperativa.Emitir(
                $"Tasa de error del {tasaErrores:P0} en los últimos {total} requests ({errores}/{total}, umbral {UmbralTasaErrores:P0}).",
                NivelAlertaOperativa.Critica);
        }

        var tasaLentitud = (double)lentos / total;
        if (tasaLentitud >= UmbralTasaLentitud)
        {
            alertaOperativa.Emitir(
                $"Latencia degradada: {tasaLentitud:P0} de los últimos {total} requests superó el umbral de lentitud ({lentos}/{total}, umbral {UmbralTasaLentitud:P0}).",
                NivelAlertaOperativa.Aviso);
        }
    }
}
