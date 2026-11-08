using CaeManager.Domain.Common;

namespace CaeManager.Domain.CalendarioLaboral;

/// <summary>Ámbito de un festivo oficial. Persistido como entero.</summary>
public enum AmbitoFestivo
{
    Nacional = 1,
    Autonomico = 2,
    Local = 3,
}

/// <summary>
/// Un festivo laboral oficial (Especificación de Ausencias y Coberturas, § 3, «Festivo»): fecha,
/// nombre, ámbito y, según el ámbito, comunidad autónoma (autonómico y local) y municipio (solo local).
///
/// <para>
/// <b>Catálogo global de referencia, sin Tenant</b>: el calendario oficial es el mismo para todos, por
/// eso extiende <see cref="Entity"/> y no <see cref="EntidadConTenant"/>. Lo siembra la migración con
/// los datos estáticos de <see cref="CatalogoFestivosOficiales"/>; la aplicación solo lo lee.
/// </para>
/// </summary>
public sealed class FestivoOficial : Entity
{
    public const int LongitudMaximaNombre = 120;
    public const int LongitudMaximaMunicipio = 120;

    public DateOnly Fecha { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public AmbitoFestivo Ambito { get; private set; }

    /// <summary>Solo en autonómicos y locales.</summary>
    public ComunidadAutonoma? Comunidad { get; private set; }

    /// <summary>Solo en locales.</summary>
    public string? Municipio { get; private set; }

    private FestivoOficial()
    {
        // Requerido por EF Core.
    }

    private FestivoOficial(DateOnly fecha, string nombre, AmbitoFestivo ambito, ComunidadAutonoma? comunidad, string? municipio)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre del festivo es obligatorio y de hasta {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Fecha = fecha;
        Nombre = nombre.Trim();
        Ambito = ambito;
        Comunidad = comunidad;
        Municipio = municipio;
        // Id estable: la migración siembra el catálogo con HasData, que compara por clave; un Id
        // nuevo en cada compilación del modelo reescribiría el catálogo entero en cada migración.
        Id = new Guid(fecha.DayNumber, (short)ambito, (short)(comunidad is { } c ? (int)c : 0),
            [0x46, 0x45, 0x53, 0x54, .. HuellaMunicipio(municipio)]);
    }

    /// <summary>
    /// Cuatro bytes deterministas del municipio (FNV-1a sobre el texto en mayúsculas invariantes), cero sin
    /// municipio. No vale <c>string.GetHashCode</c>: está aleatorizado por proceso.
    /// </summary>
    private static byte[] HuellaMunicipio(string? municipio)
    {
        if (municipio is null) return [0, 0, 0, 0];
        var huella = 2166136261u;
        foreach (var caracter in municipio.ToUpperInvariant())
            huella = unchecked((huella ^ caracter) * 16777619u);
        return BitConverter.GetBytes(huella | 1u);
    }

    public static FestivoOficial Nacional(DateOnly fecha, string nombre) =>
        new(fecha, nombre, AmbitoFestivo.Nacional, null, null);

    public static FestivoOficial Autonomico(DateOnly fecha, string nombre, ComunidadAutonoma comunidad) =>
        new(fecha, nombre, AmbitoFestivo.Autonomico, comunidad, null);

    public static FestivoOficial Local(DateOnly fecha, string nombre, ComunidadAutonoma comunidad, string municipio)
    {
        var limpio = PerfilLaboral.NormalizarMunicipio(municipio)
            ?? throw new ArgumentException("Un festivo local exige municipio.", nameof(municipio));
        return new(fecha, nombre, AmbitoFestivo.Local, comunidad, limpio);
    }

    /// <summary>
    /// A una persona le aplican todos los nacionales, los autonómicos de su comunidad y los locales de su
    /// municipio. Sin comunidad, solo los nacionales (Especificación § 3). Los autonómicos de Cataluña del catálogo
    /// son los del cuadro general y no aplican en la Val d'Aran, que sustituye uno por la Fiesta de Arán
    /// (<see cref="CatalogoFestivosOficiales.EsDeLaValDAran"/>): allí solo se conocen los nacionales. Como en una
    /// comunidad sin autonómicos en el catálogo (Madrid), «no aplica» es aquí «no se conoce»: la Diada sigue siendo
    /// festivo en Arán y <see cref="PerfilLaboral.EsLaborable"/> la da por laborable. Quien cuente días laborables debe
    /// mirar antes <see cref="CatalogoFestivosOficiales.Cubre"/>, como hace la jornada intensiva.
    /// </summary>
    public bool AplicaA(PerfilLaboral perfil)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        return Ambito switch
        {
            AmbitoFestivo.Nacional => true,
            AmbitoFestivo.Autonomico => perfil.Comunidad is { } c && c == Comunidad
                                        && !CatalogoFestivosOficiales.EsDeLaValDAran(perfil),
            AmbitoFestivo.Local => perfil.Comunidad is { } c && c == Comunidad
                                   && perfil.Municipio is { } m
                                   && string.Equals(m, Municipio, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
