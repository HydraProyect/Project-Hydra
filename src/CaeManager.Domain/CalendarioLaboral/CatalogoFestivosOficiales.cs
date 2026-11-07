namespace CaeManager.Domain.CalendarioLaboral;

/// <summary>
/// Los datos estáticos del catálogo de festivos oficiales, que la migración siembra en <c>FestivosOficiales</c>
/// (sin llamadas externas en tiempo de ejecución).
///
/// <para>
/// <b>Fuente</b>: la relación de fiestas laborales del BOE para 2026 (Resolución de 17 de octubre de 2025 de la
/// Dirección General de Trabajo, BOE-A-2025-21667) y para 2027 (Resolución de 8 de octubre de 2026, BOE-A-2026-21254).
/// </para>
///
/// <para>
/// <b>Cobertura de este incremento</b>: los festivos nacionales (los que el cuadro marca en todas las comunidades y
/// ciudades autónomas) y los autonómicos de Aragón, Cataluña, Extremadura y País Vasco, las comunidades del
/// calendario de la maqueta y de la siembra del piloto. Cada comunidad suma 12 días por año, el máximo del
/// artículo 37.2 del Estatuto de los Trabajadores sin los dos locales. Las demás comunidades no tienen aún sus
/// autonómicos: una persona de otra comunidad vería solo los nacionales. Ampliar la cobertura es añadir filas aquí
/// y una migración que las siembre.
/// </para>
///
/// <para>
/// <b>Festivos locales: ninguno.</b> § 3 los prevé por municipio, pero el catálogo no tiene ninguno y no se añaden sin
/// la resolución oficial de su comunidad. Lo que eso supone: a una persona con comunidad le faltan sus dos festivos
/// locales, y su jornada intensiva puede marcarse en uno de ellos (por ejemplo, el 10 de agosto en Huesca). La capa no
/// lo calla: <see cref="IncluyeLocales"/> dice cuándo faltan y la barra de capas lo avisa.
/// </para>
///
/// <para>
/// <b>Val d'Aran: fuera de la cobertura de Cataluña.</b> Los autonómicos de Cataluña de este catálogo son los del
/// cuadro general. En el territorio de Arán, la nota 2 de cada resolución sustituye uno por el 17 de junio (Fiesta de
/// Arán): el 26 de diciembre de 2026 (BOE-A-2025-21667) y el 29 de marzo de 2027 (BOE-A-2026-21254). Contrastado en el
/// BOE el 2026-11-07. Esas sustituciones no se siembran: a una persona de un municipio de Arán
/// (<see cref="EsDeLaValDAran"/>) no le aplican los autonómicos de Cataluña (<see cref="FestivoOficial.AplicaA"/>),
/// <see cref="Cubre"/> es false y la barra la avisa como «Val d'Aran», igual que una comunidad sin autonómicos.
/// </para>
/// </summary>
public static class CatalogoFestivosOficiales
{
    /// <summary>Las comunidades con sus festivos autonómicos sembrados.</summary>
    public static IReadOnlyList<ComunidadAutonoma> ComunidadesCubiertas { get; } =
        [ComunidadAutonoma.Aragon, ComunidadAutonoma.Cataluna, ComunidadAutonoma.Extremadura, ComunidadAutonoma.PaisVasco];

    private static FestivoOficial N(int a, int m, int d, string nombre) => FestivoOficial.Nacional(new DateOnly(a, m, d), nombre);
    private static FestivoOficial A(int a, int m, int d, string nombre, ComunidadAutonoma c) => FestivoOficial.Autonomico(new DateOnly(a, m, d), nombre, c);

    private const ComunidadAutonoma Ar = ComunidadAutonoma.Aragon;
    private const ComunidadAutonoma Ct = ComunidadAutonoma.Cataluna;
    private const ComunidadAutonoma Ex = ComunidadAutonoma.Extremadura;
    private const ComunidadAutonoma Pv = ComunidadAutonoma.PaisVasco;

    public static IReadOnlyList<FestivoOficial> Todos { get; } =
    [
        // 2026 · nacionales (BOE-A-2025-21667)
        N(2026, 1, 1, "Año Nuevo"),
        N(2026, 1, 6, "Epifanía del Señor"),
        N(2026, 4, 3, "Viernes Santo"),
        N(2026, 5, 1, "Fiesta del Trabajo"),
        N(2026, 8, 15, "Asunción de la Virgen"),
        N(2026, 10, 12, "Fiesta Nacional de España"),
        N(2026, 12, 8, "Inmaculada Concepción"),
        N(2026, 12, 25, "Natividad del Señor"),

        // 2026 · Aragón
        A(2026, 4, 2, "Jueves Santo", Ar),
        A(2026, 4, 23, "San Jorge · Día de Aragón", Ar),
        A(2026, 11, 2, "Lunes siguiente a Todos los Santos", Ar),
        A(2026, 12, 7, "Lunes siguiente al Día de la Constitución", Ar),

        // 2026 · Cataluña
        A(2026, 4, 6, "Lunes de Pascua", Ct),
        A(2026, 6, 24, "San Juan", Ct),
        A(2026, 9, 11, "Fiesta Nacional de Cataluña", Ct),
        A(2026, 12, 26, "San Esteban", Ct),

        // 2026 · Extremadura
        A(2026, 4, 2, "Jueves Santo", Ex),
        A(2026, 9, 8, "Día de Extremadura", Ex),
        A(2026, 11, 2, "Lunes siguiente a Todos los Santos", Ex),
        A(2026, 12, 7, "Lunes siguiente al Día de la Constitución", Ex),

        // 2026 · País Vasco
        A(2026, 3, 19, "San José", Pv),
        A(2026, 4, 2, "Jueves Santo", Pv),
        A(2026, 4, 6, "Lunes de Pascua", Pv),
        A(2026, 7, 25, "Santiago Apóstol", Pv),

        // 2027 · nacionales (BOE-A-2026-21254)
        N(2027, 1, 1, "Año Nuevo"),
        N(2027, 1, 6, "Epifanía del Señor"),
        N(2027, 3, 26, "Viernes Santo"),
        N(2027, 5, 1, "Fiesta del Trabajo"),
        N(2027, 10, 12, "Fiesta Nacional de España"),
        N(2027, 11, 1, "Todos los Santos"),
        N(2027, 12, 6, "Día de la Constitución Española"),
        N(2027, 12, 8, "Inmaculada Concepción"),
        N(2027, 12, 25, "Natividad del Señor"),

        // 2027 · Aragón
        A(2027, 3, 25, "Jueves Santo", Ar),
        A(2027, 4, 23, "San Jorge · Día de Aragón", Ar),
        A(2027, 8, 16, "Lunes siguiente a la Asunción de la Virgen", Ar),

        // 2027 · Cataluña
        A(2027, 3, 29, "Lunes de Pascua", Ct),
        A(2027, 6, 24, "San Juan", Ct),
        A(2027, 9, 11, "Fiesta Nacional de Cataluña", Ct),

        // 2027 · Extremadura
        A(2027, 3, 25, "Jueves Santo", Ex),
        A(2027, 9, 8, "Día de Extremadura", Ex),
        A(2027, 10, 11, "Traslado de la Asunción de la Virgen", Ex),

        // 2027 · País Vasco
        A(2027, 3, 25, "Jueves Santo", Pv),
        A(2027, 3, 29, "Lunes de Pascua", Pv),
        A(2027, 10, 7, "Aniversario del primer Gobierno Vasco", Pv),
    ];

    /// <summary>Los años cuyo calendario oficial está en el catálogo. Va detrás de <see cref="Todos"/>, que inicializa antes.</summary>
    public static IReadOnlyList<int> AniosCubiertos { get; } = Todos.Select(f => f.Fecha.Year).Distinct().Order().ToList();

    /// <summary>
    /// Si el catálogo conoce los festivos nacionales y autonómicos de una persona ese año: el año está sembrado y su
    /// comunidad, si la tiene, también, y la persona no es de un municipio de la Val d'Aran (<see cref="EsDeLaValDAran"/>),
    /// cuyos autonómicos no son los de Cataluña sembrados. Sin ello la jornada intensiva no se pinta (se pintaría sobre
    /// festivos nacionales o autonómicos que el catálogo no conoce). Sin comunidad se pinta: son los nacionales y la
    /// intensiva por defecto de quien aún no tiene ubicación de trabajo. No mira los festivos locales: eso es
    /// <see cref="IncluyeLocales"/>.
    /// </summary>
    public static bool Cubre(int anio, PerfilLaboral perfil)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        return AniosCubiertos.Contains(anio)
            && (perfil.Comunidad is null || ComunidadesCubiertas.Contains(perfil.Comunidad.Value))
            && !EsDeLaValDAran(perfil);
    }

    /// <summary>Cómo nombra la barra de capas la Val d'Aran cuando avisa de que sus autonómicos no están en el catálogo.</summary>
    public const string NombreValDAran = "Val d'Aran";

    /// <summary>
    /// Los nueve municipios de la comarca de Aran según Idescat (consultado el 2026-11-07): Arres, Bausen, Es Bòrdes,
    /// Bossòst, Canejan, Les, Naut Aran, Vielha e Mijaran y Vilamòs. Se añaden formas no oficiales que alguien puede
    /// escribir, en catalán (Les Bordes, Alt Aran, Vielha i Mijaran) y en castellano (Las Bordas, Bosost, Alto Arán,
    /// Viella y Medio Arán), «Vielha» o «Viella» a secas y el nombre del territorio. Una forma de más solo deja sin
    /// cubrir a quien la escriba; una de menos le pinta el cuadro general sin aviso. Normalizados con
    /// <see cref="NormalizarParaComparar"/>.
    /// </summary>
    private static readonly HashSet<string> MunicipiosValDAran =
    [
        "arres", "bausen", "es bordes", "les bordes", "las bordas", "bossost", "bosost", "canejan", "les",
        "naut aran", "alt aran", "alto aran",
        "vielha e mijaran", "vielha i mijaran", "vielha mijaran", "viella y medio aran", "vielha", "viella",
        "vilamos",
        "val d'aran", "vall d'aran", "valle de aran", "aran",
    ];

    /// <summary>
    /// Si la persona trabaja en un municipio de la Val d'Aran: comunidad Cataluña y municipio de la comarca de Aran.
    /// El municipio es texto libre: se compara sin mayúsculas, acentos ni espacios repetidos, y un nombre que no esté
    /// en la lista (un núcleo como Salardú o una errata) no se reconoce y se trata como el resto de Cataluña. Sin
    /// municipio tampoco se sabe: se aplica el cuadro general, y <see cref="IncluyeLocales"/> ya avisa de que falta.
    /// Los guiones cuentan como espacios («Vielha-Mijaran»).
    /// </summary>
    public static bool EsDeLaValDAran(PerfilLaboral perfil)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        return perfil.Comunidad == ComunidadAutonoma.Cataluna
            && perfil.Municipio is { } municipio
            && MunicipiosValDAran.Contains(NormalizarParaComparar(municipio));
    }

    private static string NormalizarParaComparar(string texto)
    {
        var descompuesto = texto.Normalize(System.Text.NormalizationForm.FormD);
        var limpio = new System.Text.StringBuilder(descompuesto.Length);
        foreach (var caracter in descompuesto)
        {
            var categoria = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(caracter);
            if (categoria == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            // Los caracteres de formato (categoría Format: guion blando U+00AD, ancho cero U+200B a U+200D, U+2060,
            // BOM U+FEFF...) son invisibles y se cuelan al copiar y pegar, dentro de una palabra o entre palabras: se
            // eliminan, no separan. Una regla general en vez de enumerarlos uno a uno.
            if (categoria == System.Globalization.UnicodeCategory.Format)
                continue;
            // Todo espacio cuenta como el ordinario: NBSP (U+00A0), tabuladores, espacios Unicode (U+2000 a U+200A,
            // U+202F, U+3000...) y saltos de línea. Separar solo por ' ' dejaba «Vielha e Mijaran» con NBSP sin reconocer.
            limpio.Append(caracter switch
            {
                '’' or '‘' or '´' or '`' => '\'',
                '-' => ' ',
                _ when char.IsWhiteSpace(caracter) => ' ',
                _ => char.ToLowerInvariant(caracter),
            });
        }
        return string.Join(' ', limpio.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Si el catálogo tiene los festivos locales de la persona ese año.
    /// <list type="bullet">
    /// <item>Sin comunidad, sí: § 3 le aplica solo los nacionales.</item>
    /// <item>Con comunidad y sin municipio, no. § 3 pide el municipio con la comunidad; <see cref="PerfilLaboral"/> lo
    /// deja faltar, y entonces no se sabe qué locales le tocan.</item>
    /// <item>Con municipio, solo si el catálogo tiene festivos locales de ese municipio y año. Los locales de un
    /// municipio se siembran juntos, los dos de su año (artículo 37.2 del Estatuto de los Trabajadores), y lo fija
    /// un test. Hoy el catálogo no tiene ninguno, así que con municipio devuelve siempre false.</item>
    /// </list>
    /// </summary>
    public static bool IncluyeLocales(int anio, PerfilLaboral perfil)
    {
        ArgumentNullException.ThrowIfNull(perfil);
        if (perfil.Comunidad is null) return true;
        return perfil.Municipio is not null
            && Todos.Any(f => f.Ambito == AmbitoFestivo.Local && f.Fecha.Year == anio && f.AplicaA(perfil));
    }
}
