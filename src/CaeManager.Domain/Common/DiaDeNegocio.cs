namespace CaeManager.Domain.Common;

/// <summary>
/// Única fuente del día de negocio («hoy») de TALVEG: el día civil en la España
/// peninsular, zona IANA <c>Europe/Madrid</c> (decisión de producto 2026-09-28; no
/// <c>Atlantic/Canary</c>).
///
/// Los instantes se siguen guardando y comparando en UTC. Lo único que pasa por
/// aquí es el día de calendario: vencimientos y vigencias de documentos, las
/// reglas «no puede ser futura», los cortes de alertas, calendario y Mi trabajo,
/// los KPI diarios y el plazo del art. 42.1 ET (días naturales, ver
/// <c>SolicitudCertificacionTgss.PlazoDiasTgss</c>). Entre las 22:00 y las 24:00 UTC
/// en verano (23:00–24:00 en invierno) el día UTC todavía es el anterior al de
/// Madrid; con el día UTC, un documento que vence «hoy» seguía vigente para el
/// sistema y una fecha de hoy se rechazaba como futura.
///
/// No depende de la zona horaria del servidor: los contenedores de producción y
/// de CI corren en UTC. La zona se resuelve por su id IANA; en Linux eso exige
/// <c>tzdata</c> en la imagen (el <c>Dockerfile</c> lo instala explícitamente) y,
/// si falta, el primer uso lanza en vez de caer en silencio a UTC.
///
/// <c>DiaDeNegocioUnicaFuenteTests</c> (Architecture.Tests) prohíbe
/// <c>DateTime.Today</c>, <c>DateTime.Now</c>, <c>UtcNow.Date</c> y
/// <c>DateOnly.FromDateTime(DateTime.UtcNow)</c> fuera de aquí, con una lista de
/// deuda congelada que solo puede bajar.
/// </summary>
public static class DiaDeNegocio
{
    public const string IdZonaHoraria = "Europe/Madrid";

    private static readonly Lazy<TimeZoneInfo> ZonaResuelta = new(ResolverZona);

    private static readonly AsyncLocal<TimeProvider?> RelojDelFlujo = new();

    /// <summary>La zona del día de negocio, resuelta por su id IANA.</summary>
    public static TimeZoneInfo Zona => ZonaResuelta.Value;

    /// <summary>
    /// El día de negocio actual. Usa el reloj del flujo si un test lo fijó con
    /// <see cref="FijarRelojEnEsteFlujo"/>; si no, el del sistema. Donde ya hay un
    /// <see cref="TimeProvider"/> inyectado, mejor <see cref="Hoy(TimeProvider)"/>.
    /// </summary>
    public static DateOnly Hoy() => De((RelojDelFlujo.Value ?? TimeProvider.System).GetUtcNow());

    /// <summary>El día de negocio del instante actual de <paramref name="reloj"/>.</summary>
    public static DateOnly Hoy(TimeProvider reloj)
    {
        ArgumentNullException.ThrowIfNull(reloj);
        return De(reloj.GetUtcNow());
    }

    /// <summary>El día de negocio al que pertenece <paramref name="instante"/>.</summary>
    public static DateOnly De(DateTimeOffset instante) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instante, Zona).DateTime);

    /// <summary>
    /// El día de negocio al que pertenece un instante UTC. Un <see cref="DateTime"/>
    /// de tipo <see cref="DateTimeKind.Local"/> se rechaza: su valor dependería de
    /// la zona del servidor. <see cref="DateTimeKind.Unspecified"/> se lee como UTC,
    /// que es como EF Core devuelve las columnas <c>...Utc</c>.
    /// </summary>
    public static DateOnly De(DateTime instanteUtc)
    {
        if (instanteUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException("El instante tiene que estar en UTC, no en la hora local del servidor.", nameof(instanteUtc));

        return De(new DateTimeOffset(DateTime.SpecifyKind(instanteUtc, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Un instante UTC expresado en hora peninsular, para mostrarlo (hora de un
    /// registro, saludo del día). En vez de <c>ToLocalTime()</c>, que usa la zona
    /// del servidor (UTC en los contenedores).
    /// </summary>
    public static DateTime EnHoraPeninsular(DateTime instanteUtc)
    {
        if (instanteUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException("El instante tiene que estar en UTC, no en la hora local del servidor.", nameof(instanteUtc));

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(instanteUtc, DateTimeKind.Utc), Zona);
    }

    /// <summary>La hora peninsular actual, con el mismo reloj que <see cref="Hoy()"/>.</summary>
    public static DateTime AhoraEnHoraPeninsular() =>
        EnHoraPeninsular((RelojDelFlujo.Value ?? TimeProvider.System).GetUtcNow().UtcDateTime);

    /// <summary>
    /// El instante UTC en que empieza <paramref name="dia"/> en Madrid (00:00 hora
    /// peninsular). Sirve para cortar instantes por día de negocio: «hasta el final
    /// del día D» es <c>instante &lt; InicioEnUtc(D.AddDays(1))</c>. Los cambios de
    /// hora en España son a las 02:00/03:00, nunca a medianoche, así que la
    /// medianoche local existe siempre y no es ambigua.
    /// </summary>
    public static DateTime InicioEnUtc(DateOnly dia) =>
        TimeZoneInfo.ConvertTimeToUtc(dia.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Zona);

    /// <summary>
    /// Fija el reloj de <see cref="Hoy()"/> para el flujo asíncrono actual y sus
    /// descendientes, hasta que se libere el valor devuelto. Solo para tests: el
    /// trinquete de Architecture.Tests prohíbe llamarlo desde <c>src/</c>.
    /// </summary>
    public static IDisposable FijarRelojEnEsteFlujo(TimeProvider reloj)
    {
        ArgumentNullException.ThrowIfNull(reloj);
        var anterior = RelojDelFlujo.Value;
        RelojDelFlujo.Value = reloj;
        return new Restaurador(anterior);
    }

    private static TimeZoneInfo ResolverZona()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(IdZonaHoraria);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"No se encuentra la zona horaria '{IdZonaHoraria}', que define el día de negocio. " +
                "En Linux hace falta el paquete tzdata en la imagen.", ex);
        }
    }

    private sealed class Restaurador(TimeProvider? anterior) : IDisposable
    {
        public void Dispose() => RelojDelFlujo.Value = anterior;
    }
}
