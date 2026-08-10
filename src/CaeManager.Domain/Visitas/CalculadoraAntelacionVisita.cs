namespace CaeManager.Domain.Visitas;

/// <summary>
/// Resultado congelado de la matriz de antelación de una Visita. Se persiste
/// en la propia <see cref="Visita"/> en vez de recalcularse al leer: es un
/// hecho de un instante concreto, y los umbrales de
/// <see cref="Configuracion.ParametroSistema"/> pueden cambiar después.
/// </summary>
public readonly record struct ResultadoAntelacion(
    decimal NominalHoras,
    decimal EfectivaHoras,
    decimal BloqueadoPorClienteHoras,
    TramoAntelacion Tramo,
    AtribucionUrgencia Atribucion);

/// <summary>
/// Separa lo que el cliente <i>dice</i> que dio de margen de lo que el gestor
/// <i>tuvo</i> de verdad. Lógica pura sin dependencias, igual que
/// <see cref="CalculadoraUrgenciaVisita"/> — y complementaria, no sustituta:
/// aquella clasifica el riesgo de una visita futura en días naturales, esta
/// atribuye la responsabilidad de una gestión con horas reales.
///
/// <code>
/// Nominal            = T_visita - T_solicitud          (lo que el cliente cree que dio)
/// Efectiva           = T_visita - T_expedienteCompleto (lo que el gestor tuvo)
/// BloqueadoPorCliente = T_expedienteCompleto - T_solicitud
/// </code>
/// </summary>
public static class CalculadoraAntelacionVisita
{
    public static ResultadoAntelacion Calcular(
        DateTime fechaHoraEntrada,
        DateTime fechaHoraSolicitud,
        DateTime fechaHoraExpedienteCompleto,
        int horasAvisoVisita,
        int horasCriticasVisita,
        TimeOnly horaInicioJornada,
        TimeOnly horaFinJornada)
    {
        var nominal = Horas(fechaHoraEntrada - fechaHoraSolicitud);
        var efectiva = Horas(fechaHoraEntrada - fechaHoraExpedienteCompleto);
        var bloqueado = Horas(fechaHoraExpedienteCompleto - fechaHoraSolicitud);

        // Una entrada fuera de la franja de jornada es exprés por definición: nadie
        // del equipo está disponible para resolver lo que falte esa mañana.
        var horaEntrada = TimeOnly.FromDateTime(fechaHoraEntrada);
        var fueraDeJornada = horaEntrada < horaInicioJornada || horaEntrada > horaFinJornada;

        var tramo = fueraDeJornada
            ? TramoAntelacion.Expres
            : efectiva < horasCriticasVisita ? TramoAntelacion.Expres
            : efectiva < horasAvisoVisita ? TramoAntelacion.Urgente
            : TramoAntelacion.Estandar;

        var atribucion = Atribuir(nominal, efectiva, horasAvisoVisita);

        return new ResultadoAntelacion(nominal, efectiva, bloqueado, tramo, atribucion);
    }

    /// <summary>
    /// Se atribuye a la restricción que de verdad ató las manos, y el orden importa:
    /// si el aviso ya llegó dentro de la ventana de urgencia, la documentación tardía
    /// es consecuencia y no causa. Solo cuando hubo aviso con margen sobrado y aun así
    /// el expediente se completó dentro de la ventana se imputa a la documentación —
    /// ese es exactamente el "falso aviso con tiempo".
    /// <see cref="AtribucionUrgencia.RetrasoGestor"/> no se emite aquí a propósito;
    /// ver su documentación.
    /// </summary>
    private static AtribucionUrgencia Atribuir(decimal nominal, decimal efectiva, int horasAvisoVisita)
    {
        if (nominal < horasAvisoVisita)
            return AtribucionUrgencia.SolicitudTardiaCliente;

        if (efectiva < horasAvisoVisita)
            return AtribucionUrgencia.DocumentacionTardiaCliente;

        return AtribucionUrgencia.SinUrgencia;
    }

    /// <summary>Horas con dos decimales — suficiente para facturar tramos y evita que dos visitas idénticas difieran por microsegundos.</summary>
    private static decimal Horas(TimeSpan diferencia) => Math.Round((decimal)diferencia.TotalHours, 2);
}
