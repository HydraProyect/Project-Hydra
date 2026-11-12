using CaeManager.Application.Calendario.Queries;
using CaeManager.Domain.CalendarioLaboral;

namespace CaeManager.Web.Features.Calendario.Components;

/// <summary>
/// Lo que las capas «Festivos» y «Jornada intensiva» pintan en cada día del mes, ya filtrado por las casillas y por
/// «Festivos de» (maqueta «Calendario TALVEG», <c>capaDia</c>): las etiquetas de festivo sin repetir («Nombre ·
/// nacional» o «Nombre · lugar») y si el día lleva la línea de jornada intensiva. <c>CapasCalendario</c> lo calcula
/// y la página solo lo reparte a las celdas.
/// </summary>
public sealed class MarcasCapasMes
{
    public static readonly MarcasCapasMes Vacias = new(new Dictionary<DateOnly, IReadOnlyList<string>>(), new HashSet<DateOnly>(), _ => string.Empty, string.Empty);

    private readonly IReadOnlyDictionary<DateOnly, IReadOnlyList<string>> _festivos;
    private readonly IReadOnlySet<DateOnly> _intensiva;
    private readonly Func<string, string> _festivoAccesible;
    private readonly string _intensivaAccesible;

    private MarcasCapasMes(
        IReadOnlyDictionary<DateOnly, IReadOnlyList<string>> festivos, IReadOnlySet<DateOnly> intensiva,
        Func<string, string> festivoAccesible, string intensivaAccesible)
    {
        _festivos = festivos;
        _intensiva = intensiva;
        _festivoAccesible = festivoAccesible;
        _intensivaAccesible = intensivaAccesible;
    }

    /// <param name="comunidad">La opción de «Festivos de»; null es «Todo el equipo».</param>
    /// <param name="etiqueta">Cómo se escribe un festivo en la celda («Nombre · nacional», «Nombre · lugar»).</param>
    public static MarcasCapasMes Calcular(
        CapasCalendarioDto capas, bool festivos, bool intensiva, string? comunidad,
        Func<FestivoCapaDto, string> etiqueta, Func<string, string> festivoAccesible, string intensivaAccesible)
    {
        var porDia = new Dictionary<DateOnly, IReadOnlyList<string>>();
        if (festivos)
        {
            porDia = capas.Personas
                .Where(p => comunidad is null || string.Equals(p.Comunidad, comunidad, StringComparison.Ordinal))
                .SelectMany(p => p.Festivos)
                .GroupBy(f => f.Fecha)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(etiqueta).Distinct(StringComparer.Ordinal).ToList());
        }

        // La intensiva no se filtra por «Festivos de» (como en la maqueta): el día la lleva si la tiene alguien visible.
        var dias = intensiva
            ? capas.Personas.SelectMany(p => p.DiasJornadaIntensiva).ToHashSet()
            : [];
        return new MarcasCapasMes(porDia, dias, festivoAccesible, intensivaAccesible);
    }

    public IReadOnlyList<string> Festivos(DateOnly dia) => _festivos.TryGetValue(dia, out var lista) ? lista : [];

    public bool Intensiva(DateOnly dia) => _intensiva.Contains(dia);

    public bool TieneMarcas(DateOnly dia) => Festivos(dia).Count > 0 || Intensiva(dia);

    /// <summary>El nombre accesible de la línea de jornada intensiva en la celda («Jornada intensiva · …»).</summary>
    public string IntensivaAccesible => _intensivaAccesible;

    /// <summary>Lo que el día añade a su nombre accesible («. Festivo: …. Jornada intensiva · …»), vacío sin marcas.</summary>
    public string TextoAccesible(DateOnly dia)
    {
        var partes = Festivos(dia).Select(_festivoAccesible).ToList();
        if (Intensiva(dia)) partes.Add(_intensivaAccesible);
        return partes.Count == 0 ? string.Empty : ". " + string.Join(". ", partes);
    }

    /// <summary>Si un festivo es nacional (sin lugar): para escribir «· nacional» y no «· España».</summary>
    public static bool EsNacional(FestivoCapaDto festivo) => festivo.Ambito == AmbitoFestivo.Nacional || festivo.Lugar is null;
}
