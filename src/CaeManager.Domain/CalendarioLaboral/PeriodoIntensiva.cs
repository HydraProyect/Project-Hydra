namespace CaeManager.Domain.CalendarioLaboral;

/// <summary>
/// Un periodo de jornada intensiva que se repite cada año entre dos días (MM-DD), ambos incluidos
/// (Especificación de Ausencias y Coberturas, § 3, «PeriodoIntensiva»). Solo se modela el de «todos los
/// días laborables»: el periodo «Viernes» de la especificación y su configuración por equipo (§ 5.1.6)
/// quedan fuera de este incremento, y el equipo usa siempre <see cref="Verano"/>.
/// </summary>
public sealed class PeriodoIntensiva
{
    /// <summary>El periodo por defecto: «Verano», del 1 de julio al 31 de agosto.</summary>
    public static PeriodoIntensiva Verano { get; } = new("Verano", 7, 1, 8, 31);

    public string Nombre { get; }
    public int MesDesde { get; }
    public int DiaDesde { get; }
    public int MesHasta { get; }
    public int DiaHasta { get; }

    public PeriodoIntensiva(string nombre, int mesDesde, int diaDesde, int mesHasta, int diaHasta)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El periodo necesita un nombre.", nameof(nombre));
        Validar(mesDesde, diaDesde, nameof(diaDesde));
        Validar(mesHasta, diaHasta, nameof(diaHasta));
        if (Clave(mesDesde, diaDesde) > Clave(mesHasta, diaHasta))
            throw new ArgumentException("El periodo no puede acabar antes de empezar dentro del año.", nameof(mesHasta));

        Nombre = nombre.Trim();
        MesDesde = mesDesde;
        DiaDesde = diaDesde;
        MesHasta = mesHasta;
        DiaHasta = diaHasta;
    }

    /// <summary>Si la fecha cae dentro del periodo de su año, ambos extremos incluidos.</summary>
    public bool Contiene(DateOnly fecha)
    {
        var clave = Clave(fecha.Month, fecha.Day);
        return clave >= Clave(MesDesde, DiaDesde) && clave <= Clave(MesHasta, DiaHasta);
    }

    private static int Clave(int mes, int dia) => mes * 100 + dia;

    private static void Validar(int mes, int dia, string parametro)
    {
        // 2024 es bisiesto: admite el 29 de febrero como extremo.
        if (mes is < 1 or > 12 || dia < 1 || dia > DateTime.DaysInMonth(2024, mes))
            throw new ArgumentOutOfRangeException(parametro, $"{mes:00}-{dia:00} no es un día del año.");
    }
}
