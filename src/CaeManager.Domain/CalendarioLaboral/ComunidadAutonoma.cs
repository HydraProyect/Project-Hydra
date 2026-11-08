namespace CaeManager.Domain.CalendarioLaboral;

/// <summary>
/// Comunidad o ciudad autónoma de la ubicación de trabajo de una persona, con el código del INE
/// como valor (persistido como entero: el valor no se renumera nunca).
/// </summary>
public enum ComunidadAutonoma
{
    Andalucia = 1,
    Aragon = 2,
    Asturias = 3,
    IllesBalears = 4,
    Canarias = 5,
    Cantabria = 6,
    CastillaYLeon = 7,
    CastillaLaMancha = 8,
    Cataluna = 9,
    ComunitatValenciana = 10,
    Extremadura = 11,
    Galicia = 12,
    Madrid = 13,
    Murcia = 14,
    Navarra = 15,
    PaisVasco = 16,
    LaRioja = 17,
    Ceuta = 18,
    Melilla = 19,
}

public static class ComunidadAutonomaNombres
{
    /// <summary>El nombre propio de la comunidad, tal como lo enseña la pantalla (es un topónimo: no se traduce).</summary>
    public static string Nombre(this ComunidadAutonoma comunidad) => comunidad switch
    {
        ComunidadAutonoma.Andalucia => "Andalucía",
        ComunidadAutonoma.Aragon => "Aragón",
        ComunidadAutonoma.Asturias => "Asturias",
        ComunidadAutonoma.IllesBalears => "Illes Balears",
        ComunidadAutonoma.Canarias => "Canarias",
        ComunidadAutonoma.Cantabria => "Cantabria",
        ComunidadAutonoma.CastillaYLeon => "Castilla y León",
        ComunidadAutonoma.CastillaLaMancha => "Castilla-La Mancha",
        ComunidadAutonoma.Cataluna => "Cataluña",
        ComunidadAutonoma.ComunitatValenciana => "Comunitat Valenciana",
        ComunidadAutonoma.Extremadura => "Extremadura",
        ComunidadAutonoma.Galicia => "Galicia",
        ComunidadAutonoma.Madrid => "Comunidad de Madrid",
        ComunidadAutonoma.Murcia => "Región de Murcia",
        ComunidadAutonoma.Navarra => "Navarra",
        ComunidadAutonoma.PaisVasco => "País Vasco",
        ComunidadAutonoma.LaRioja => "La Rioja",
        ComunidadAutonoma.Ceuta => "Ceuta",
        ComunidadAutonoma.Melilla => "Melilla",
        _ => throw new ArgumentOutOfRangeException(nameof(comunidad), comunidad, "Comunidad autónoma desconocida."),
    };
}
