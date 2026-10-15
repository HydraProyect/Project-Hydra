namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Cuándo está vigente un Proyecto a efectos de las cuentas por periodo de los KPI (días abiertos y Trabajadores
/// asignados). Decisión de producto de 2026-10-15: una obra está vigente desde su inicio hasta su cierre real, o
/// hasta hoy mientras siga abierta; un <see cref="TipoProyecto.Evento"/> cuenta como un «Centro temporal» y está
/// vigente solo entre su fecha de inicio y su fecha de fin (el último día cuenta), nunca como obra abierta hasta el
/// cierre. El evento nunca entra en la facturación: la pantalla de Facturación lo excluye antes de llegar aquí.
/// Funciones puras sobre fechas, para que las consultas de KPI las apliquen a filas ya proyectadas.
/// </summary>
public static class VigenciaDeProyecto
{
    /// <summary>
    /// Último día en que el Proyecto está vigente. Obra: el cierre real o, si sigue abierta, <paramref name="hoy"/>.
    /// Evento: el día de su fecha de fin, o el del cierre si se cerró antes; no depende de <paramref name="hoy"/>
    /// (un evento con el fin en el futuro sigue vigente hasta ese día y uno terminado no se alarga hasta hoy).
    /// </summary>
    public static DateOnly UltimoDiaVigente(TipoProyecto tipo, DateOnly? finPrevista, DateOnly? cierreReal, DateOnly hoy)
    {
        if (tipo != TipoProyecto.Evento)
            return cierreReal ?? hoy;

        // Un evento siempre tiene fin (Proyecto.Crear y Actualizar lo exigen): el respaldo solo evita una excepción
        // ante una fila anómala y la trata como obra abierta, que es lo que se hacía antes de existir el tipo.
        if (finPrevista is not { } fin)
            return cierreReal ?? hoy;

        return cierreReal is { } cierre && cierre < fin ? cierre : fin;
    }

    /// <summary>
    /// Días del periodo [<paramref name="inicioPeriodo"/>, <paramref name="finPeriodo"/>] (ambos inclusivos) en que el
    /// Proyecto está vigente: cuenta inclusiva, el día de inicio y el último día vigente cuentan los dos.
    /// </summary>
    public static int DiasVigentesEnPeriodo(
        TipoProyecto tipo, DateOnly fechaInicio, DateOnly? finPrevista, DateOnly? cierreReal, DateOnly hoy,
        DateOnly inicioPeriodo, DateOnly finPeriodo)
    {
        var ultimo = UltimoDiaVigente(tipo, finPrevista, cierreReal, hoy);
        var desde = fechaInicio > inicioPeriodo ? fechaInicio : inicioPeriodo;
        var hasta = ultimo < finPeriodo ? ultimo : finPeriodo;

        return hasta >= desde ? hasta.DayNumber - desde.DayNumber + 1 : 0;
    }

    /// <summary>
    /// Si el alta de un Trabajador en el Proyecto (<paramref name="altaTecnico"/>–<paramref name="bajaTecnico"/>,
    /// sin baja = sigue activa) cuenta en el periodo. Obra: basta que el alta estuviera activa en algún punto del
    /// periodo, sin mirar las fechas del Proyecto (el criterio de siempre). Evento: además el Trabajador tiene que
    /// estar dado de alta algún día en que el evento está vigente dentro del periodo, así que un alta posterior al
    /// fin del evento, o una baja anterior a su inicio, no cuenta aunque caiga en el periodo.
    /// </summary>
    public static bool TecnicoCuentaEnPeriodo(
        TipoProyecto tipo, DateOnly fechaInicio, DateOnly? finPrevista, DateOnly? cierreReal, DateOnly hoy,
        DateOnly altaTecnico, DateOnly? bajaTecnico, DateOnly inicioPeriodo, DateOnly finPeriodo)
    {
        var altaActivaEnPeriodo = altaTecnico <= finPeriodo && (bajaTecnico is null || bajaTecnico >= inicioPeriodo);
        if (tipo != TipoProyecto.Evento)
            return altaActivaEnPeriodo;

        var ultimo = UltimoDiaVigente(tipo, finPrevista, cierreReal, hoy);
        var desde = Max(Max(inicioPeriodo, fechaInicio), altaTecnico);
        var hasta = Min(Min(finPeriodo, ultimo), bajaTecnico ?? DateOnly.MaxValue);

        return hasta >= desde;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
