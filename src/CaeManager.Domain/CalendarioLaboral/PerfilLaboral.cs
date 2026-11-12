namespace CaeManager.Domain.CalendarioLaboral;

/// <summary>
/// Lo que decide los festivos y la jornada intensiva de una persona (Especificación de Ausencias y
/// Coberturas, § 3, «CalendarioLaboral»): comunidad autónoma (puede faltar), municipio (opcional, y solo
/// con comunidad) y si le aplica la jornada intensiva del equipo. § 3 pide el municipio cuando hay comunidad; aquí
/// puede faltar, y entonces no se conocen sus festivos locales (<see cref="CatalogoFestivosOficiales.IncluyeLocales"/>).
///
/// <para>
/// Reglas: día laborable es de lunes a viernes y no festivo de esa persona (§ 4.1, D1); la jornada
/// intensiva es día laborable completo (D2), así que solo la tiene un día laborable dentro del periodo.
/// Si no aplica la intensiva, la persona no tiene días de intensiva (§ 10, punto 3).
/// </para>
/// </summary>
public sealed class PerfilLaboral
{
    /// <summary>
    /// La persona sin calendario laboral guardado: sin comunidad (solo festivos nacionales) y con la
    /// jornada intensiva del equipo, que es lo que trae por defecto.
    /// </summary>
    public static PerfilLaboral SinConfigurar { get; } = new(null, null, aplicaJornadaIntensiva: true);

    public ComunidadAutonoma? Comunidad { get; }
    public string? Municipio { get; }
    public bool AplicaJornadaIntensiva { get; }

    public PerfilLaboral(ComunidadAutonoma? comunidad, string? municipio, bool aplicaJornadaIntensiva)
    {
        if (comunidad is { } c && !Enum.IsDefined(c))
            throw new ArgumentOutOfRangeException(nameof(comunidad), comunidad, "Comunidad autónoma desconocida.");

        var limpio = NormalizarMunicipio(municipio);
        if (limpio is not null && comunidad is null)
            throw new ArgumentException("El municipio solo se indica junto con la comunidad autónoma.", nameof(municipio));
        if (limpio is { Length: > FestivoOficial.LongitudMaximaMunicipio })
            throw new ArgumentException($"El municipio tiene como máximo {FestivoOficial.LongitudMaximaMunicipio} caracteres.", nameof(municipio));

        Comunidad = comunidad;
        Municipio = limpio;
        AplicaJornadaIntensiva = aplicaJornadaIntensiva;
    }

    internal static string? NormalizarMunicipio(string? municipio) =>
        string.IsNullOrWhiteSpace(municipio) ? null : municipio.Trim();

    /// <summary>Los festivos del catálogo que le aplican, por fecha.</summary>
    public IReadOnlyList<FestivoOficial> FestivosQueLeAplican(IEnumerable<FestivoOficial> catalogo) =>
        catalogo.Where(f => f.AplicaA(this)).OrderBy(f => f.Fecha).ThenBy(f => f.Ambito).ToList();

    /// <summary>De lunes a viernes y no festivo de esta persona (§ 4.1).</summary>
    public bool EsLaborable(DateOnly fecha, IEnumerable<FestivoOficial> catalogo) =>
        fecha.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && !catalogo.Any(f => f.Fecha == fecha && f.AplicaA(this));

    /// <summary>Día laborable suyo dentro del periodo, si le aplica la intensiva del equipo (D2).</summary>
    public bool TieneJornadaIntensiva(DateOnly fecha, IEnumerable<FestivoOficial> catalogo, PeriodoIntensiva periodo)
    {
        ArgumentNullException.ThrowIfNull(periodo);
        return AplicaJornadaIntensiva && periodo.Contiene(fecha) && EsLaborable(fecha, catalogo);
    }
}
