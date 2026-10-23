using CaeManager.Domain.Seguridad;

namespace CaeManager.Application.Seguridad.InicioSesion;

/// <summary>Umbrales de la detección. Los valores por defecto son los aprobados para el piloto.</summary>
public sealed record OpcionesDeteccionInicioSesion
{
    /// <summary>Ventana de «país visto recientemente».</summary>
    public int DiasPaisReciente { get; init; } = 90;

    /// <summary>País de referencia cuando la cuenta no tiene ningún inicio correcto con país conocido.</summary>
    public string PaisHabitualPorDefecto { get; init; } = "ES";

    /// <summary>Distancia mínima entre dos inicios para poder hablar de viaje imposible (absorbe la imprecisión de la base).</summary>
    public double DistanciaMinimaViajeKm { get; init; } = 500;

    /// <summary>Velocidad por encima de la cual el viaje es imposible (un vuelo comercial ronda 900 km/h).</summary>
    public double VelocidadMaximaKmH { get; init; } = 1000;

    /// <summary>Intentos fallidos desde una misma IP que forman una ráfaga.</summary>
    public int UmbralRafagaFallos { get; init; } = 10;

    /// <summary>Ventana deslizante de la ráfaga.</summary>
    public int MinutosVentanaRafaga { get; init; } = 15;
}

/// <summary>Lo que la detección necesita de un inicio correcto anterior de la misma cuenta.</summary>
public sealed record InicioSesionPrevio(
    DateTime OcurridoEnUtc,
    string? PaisIso,
    double? Latitud,
    double? Longitud,
    string? HuellaDispositivo);

/// <summary>
/// Reglas de inicio de sesión sospechoso, como función pura sobre el intento y
/// el historial de inicios <b>correctos</b> de la misma cuenta (los fallidos no
/// enseñan nada sobre dónde suele estar la persona). Solo se evalúa un inicio
/// correcto: un fallo no es un acceso, y su única señal es la ráfaga por IP.
/// </summary>
public static class DetectorInicioSesionSospechoso
{
    private const double RadioTierraKm = 6371.0;

    public static SenalesInicioSesion Evaluar(
        InicioSesionPrevio actual,
        IReadOnlyCollection<InicioSesionPrevio> historialCorrectos,
        OpcionesDeteccionInicioSesion opciones)
    {
        var senales = SenalesInicioSesion.Ninguna;

        if (EsPaisNuevo(actual, historialCorrectos, opciones))
            senales |= SenalesInicioSesion.PaisNuevo;

        // El primer inicio de una cuenta no tiene con qué compararse: avisar de
        // «dispositivo nuevo» en cada alta sería ruido que enseña a ignorar el aviso.
        if (actual.HuellaDispositivo is not null
            && historialCorrectos.Count > 0
            && !historialCorrectos.Any(h => h.HuellaDispositivo == actual.HuellaDispositivo))
            senales |= SenalesInicioSesion.DispositivoNuevo;

        if (EsViajeImposible(actual, historialCorrectos, opciones))
            senales |= SenalesInicioSesion.ViajeImposible;

        return senales;
    }

    /// <summary>
    /// País no visto en los inicios correctos de los últimos
    /// <see cref="OpcionesDeteccionInicioSesion.DiasPaisReciente"/> días. Sin
    /// ninguno reciente con país, la referencia es el país habitual: el más
    /// frecuente del historial entero o, sin historial, el país por defecto. Un
    /// país desconocido nunca es «nuevo».
    /// </summary>
    private static bool EsPaisNuevo(
        InicioSesionPrevio actual, IReadOnlyCollection<InicioSesionPrevio> historial, OpcionesDeteccionInicioSesion opciones)
    {
        if (actual.PaisIso is null)
            return false;

        var desde = actual.OcurridoEnUtc.AddDays(-opciones.DiasPaisReciente);
        var recientes = historial
            .Where(h => h.PaisIso is not null && h.OcurridoEnUtc >= desde && h.OcurridoEnUtc <= actual.OcurridoEnUtc)
            .Select(h => h.PaisIso!)
            .ToHashSet(StringComparer.Ordinal);

        if (recientes.Count > 0)
            return !recientes.Contains(actual.PaisIso);

        var habitual = historial
            .Where(h => h.PaisIso is not null)
            .GroupBy(h => h.PaisIso!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(h => h.OcurridoEnUtc))
            .Select(g => g.Key)
            .FirstOrDefault() ?? opciones.PaisHabitualPorDefecto;

        return !string.Equals(actual.PaisIso, habitual, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contra el inicio correcto anterior más reciente que tenga país y
    /// coordenadas: país distinto, al menos
    /// <see cref="OpcionesDeteccionInicioSesion.DistanciaMinimaViajeKm"/> km y una
    /// velocidad implícita por encima de
    /// <see cref="OpcionesDeteccionInicioSesion.VelocidadMaximaKmH"/>. Sin
    /// coordenadas en alguno de los dos no se evalúa.
    /// </summary>
    private static bool EsViajeImposible(
        InicioSesionPrevio actual, IReadOnlyCollection<InicioSesionPrevio> historial, OpcionesDeteccionInicioSesion opciones)
    {
        if (actual.PaisIso is null || actual.Latitud is null || actual.Longitud is null)
            return false;

        var anterior = historial
            .Where(h => h.PaisIso is not null && h.Latitud is not null && h.Longitud is not null
                        && h.OcurridoEnUtc <= actual.OcurridoEnUtc)
            .OrderByDescending(h => h.OcurridoEnUtc)
            .FirstOrDefault();

        if (anterior is null || string.Equals(anterior.PaisIso, actual.PaisIso, StringComparison.Ordinal))
            return false;

        var distanciaKm = DistanciaKm(anterior.Latitud!.Value, anterior.Longitud!.Value, actual.Latitud.Value, actual.Longitud.Value);
        if (distanciaKm < opciones.DistanciaMinimaViajeKm)
            return false;

        // Suelo de un minuto: dos inicios en el mismo instante no dividen por cero.
        var horas = Math.Max((actual.OcurridoEnUtc - anterior.OcurridoEnUtc).TotalHours, 1.0 / 60);
        return distanciaKm / horas > opciones.VelocidadMaximaKmH;
    }

    /// <summary>Distancia ortodrómica (haversine) en kilómetros.</summary>
    public static double DistanciaKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double grados) => grados * Math.PI / 180.0;

        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * RadioTierraKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }
}
