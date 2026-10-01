using System.Text.RegularExpressions;
using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El día de negocio («hoy») sale de una sola fuente, <see cref="DiaDeNegocio"/>
/// (Europe/Madrid, decisión de producto 2026-09-28). Este trinquete impide:
///
/// 1. Que un sitio nuevo calcule «hoy» desde el reloj por su cuenta:
///    <c>DateTime.Today</c>, <c>DateTime.Now</c>, <c>DateTimeOffset.Now</c>,
///    <c>UtcNow.Date</c>, <c>UtcDateTime.Date</c> o
///    <c>DateOnly.FromDateTime(DateTime.UtcNow)</c>. Los dos primeros dependen de
///    la zona del servidor (UTC en los contenedores); los demás dan el día UTC,
///    que entre las 22:00 y las 24:00 UTC en verano todavía es el anterior al de
///    Madrid. No queda ninguno (inventario de 2026-09-28: 95 usos en 77 ficheros,
///    todos migrados), así que no hay lista de deuda: cualquier uso es rojo.
/// 2. Que un sitio nuevo dependa de la zona horaria del servidor para mostrar o
///    cortar un instante (<c>ToLocalTime()</c>, <c>LocalDateTime</c>,
///    <c>TimeZoneInfo.Local</c>…) o resuelva Europe/Madrid por su cuenta. Tolerancia
///    cero: la deuda congelada por #982 (46 <c>ToLocalTime()</c> en 38 ficheros, casi
///    toda hora de un registro pintada en UTC en producción) se retiró entera; una
///    hora para el usuario es <c>instanteUtc.EnHoraPeninsular()</c>.
/// 3. Que el código de producción fije el reloj ambiental de
///    <see cref="DiaDeNegocio.FijarRelojEnEsteFlujo"/>, que es solo para tests.
/// 4. Que un instante UTC (<c>…Utc</c>, <c>UtcNow</c>) se formatee para mostrar
///    sin pasar por <c>EnHoraPeninsular()</c>. Los usos técnicos justificados
///    viven en <see cref="InstanteUtcEnCrudoTecnico"/>.
///
/// Mismo mecanismo de ratchet por texto que <see cref="IdentificadoresDeEntidadUuidV7Tests"/>:
/// una propiedad estática no es una dependencia de tipo, la reflexión no la ve.
/// </summary>
public class DiaDeNegocioUnicaFuenteTests
{
    private const string FuenteUnica = "src/CaeManager.Domain/Common/DiaDeNegocio.cs";

    private static readonly Regex PatronDiaDesdeReloj = new(
        @"\bDateTime\s*\.\s*Today\b"
        + @"|\bDateTime\s*\.\s*Now\b"
        + @"|\bDateTimeOffset\s*\.\s*Now\b"
        + @"|\bUtcNow\s*(?:\(\s*\))?\s*\.\s*Date\b"
        + @"|\bUtcDateTime\s*\.\s*Date\b"
        + @"|\bDateOnly\s*\.\s*FromDateTime\s*\(\s*(?:System\s*\.\s*)?DateTime\s*\.\s*UtcNow\b"
        + @"|\bDateOnly\s*\.\s*FromDateTime\s*\([^;]*?GetUtcNow\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PatronZonaDelServidor = new(
        @"\bToLocalTime\s*\(|\.\s*LocalDateTime\b|\bGetLocalNow\s*\(|\bTimeZoneInfo\s*\.\s*Local\b|""Europe/Madrid""",
        RegexOptions.Compiled);

    private static readonly Regex PatronRelojDeTests = new(@"\bFijarRelojEnEsteFlujo\b", RegexOptions.Compiled);

    /// <summary>
    /// Un instante UTC (nombre acabado en <c>Utc</c>, o <c>UtcNow</c>) formateado
    /// tal cual: <c>x.CreadoEnUtc.ToString("dd/MM/yyyy")</c> o
    /// <c>{x.ExpiraEnUtc:dd/MM HH:mm}</c>. Sin pasar por <c>EnHoraPeninsular()</c>
    /// pinta la hora UTC, y entre las 22:00 y las 24:00 UTC el día anterior. El
    /// formato máquina de ida y vuelta (<c>"O"</c>) no es presentación.
    /// </summary>
    private static readonly Regex PatronInstanteUtcEnCrudo = new(
        @"\b(?:\w*Utc|UtcNow)(?:\s*\.\s*Value)?\s*\??\s*\.\s*ToString\s*\(\s*""(?![Oo]"")"
        + @"|\b(?:\w*Utc|UtcNow)(?:\s*\.\s*Value)?:[dHMyfFgGt]",
        RegexOptions.Compiled);

    /// <summary>
    /// Formateos de un instante UTC que no se enseñan al usuario como hora. Cada
    /// entrada dice por qué; un uso nuevo de presentación convierte con
    /// <c>EnHoraPeninsular()</c>.
    /// </summary>
    private static readonly Dictionary<string, int> InstanteUtcEnCrudoTecnico = new()
    {
        // Fecha de cada mensaje dentro del prompt al modelo de relevancia CAE: contexto para la IA, no pantalla.
        ["src/CaeManager.Application/Comunicaciones/Deteccion/IRelevanciaCaeService.cs"] = 1,
        // Nombre de fichero de un adjunto de WhatsApp sin nombre: identificador técnico.
        ["src/CaeManager.Application/Integraciones/IngestaWebhookWhatsAppService.cs"] = 1,
        // Nombre del fichero de credenciales de la siembra demo, con sufijo Z: zona explícita.
        ["src/CaeManager.Infrastructure/Persistence/Seed/SiembraDemoDireccionAdministrativa.cs"] = 1,
    };

    [Fact]
    public void Ningun_sitio_nuevo_calcula_el_dia_desde_el_reloj_fuera_de_la_fuente_unica()
    {
        var medidos = ContarPorFichero(RaizDelRepositorio(), "src", PatronDiaDesdeReloj);

        medidos.Should().BeEmpty(
            "«hoy» es DiaDeNegocio.Hoy() (Europe/Madrid), no el día UTC ni el del servidor; el día de un " +
            "instante es DiaDeNegocio.De(instante) y la hora para mostrar, DiaDeNegocio.EnHoraPeninsular");
    }

    [Fact]
    public void Ningun_sitio_nuevo_depende_de_la_zona_horaria_del_servidor()
    {
        var esperado = new Dictionary<string, int> { [FuenteUnica] = 1 };
        var medidos = ContarPorFichero(RaizDelRepositorio(), "src", PatronZonaDelServidor);

        Divergencias(esperado, medidos).Should().BeEmpty(
            "los contenedores corren en UTC: convierte con DiaDeNegocio.Zona o DiaDeNegocio.De(instante), y " +
            "Europe/Madrid solo se resuelve en DiaDeNegocio; para mostrar una hora, instanteUtc.EnHoraPeninsular()");
    }

    [Fact]
    public void Ningun_instante_utc_se_formatea_para_mostrar_sin_pasar_a_hora_peninsular()
    {
        var medidos = ContarPorFichero(RaizDelRepositorio(), "src", PatronInstanteUtcEnCrudo);

        Divergencias(InstanteUtcEnCrudoTecnico, medidos).Should().BeEmpty(
            "una fecha u hora que ve el usuario es instanteUtc.EnHoraPeninsular().ToString(...); formateado en crudo " +
            "sale en UTC. Si es un uso técnico (nombre de fichero, formato máquina), justifícalo en la lista");
    }

    /// <summary>
    /// Formato único de fecha y hora en pantalla, dd/MM/yyyy HH:mm (decisión del
    /// 2026-09-29; D-19 del recorrido en staging: convivían «28/9/2026 14:50»,
    /// «01/10/26» y «01/10/2026»). Ni año de dos cifras ni el formato corto de la
    /// cultura (<c>"g"</c>/<c>"G"</c>), que cambia con el idioma.
    /// </summary>
    private static readonly Regex PatronFormatoNoCanonico = new(
        @"""dd/MM/yy(?:[\s""])|ToString\s*\(\s*""[gG]""", RegexOptions.Compiled);

    [Fact]
    public void Las_fechas_y_horas_usan_el_formato_unico()
    {
        EsCodigoQueCasa("<span>@item.GeneradoEnUtc.EnHoraPeninsular().ToString(\"dd/MM/yy HH:mm\")</span>", PatronFormatoNoCanonico)
            .Should().BeTrue("control positivo: año de dos cifras");
        EsCodigoQueCasa("        utc.EnHoraPeninsular().ToString(\"g\", CultureInfo.CurrentCulture);", PatronFormatoNoCanonico)
            .Should().BeTrue("control positivo: formato corto de la cultura");
        EsCodigoQueCasa("<span>@item.GeneradoEnUtc.EnHoraPeninsular().ToString(\"dd/MM/yyyy HH:mm\")</span>", PatronFormatoNoCanonico)
            .Should().BeFalse("control negativo: el formato único");

        ContarPorFichero(RaizDelRepositorio(), "src", PatronFormatoNoCanonico).Keys.Should().BeEmpty(
            "fecha y hora en pantalla: dd/MM/yyyy HH:mm, en hora peninsular");
    }

    [Fact]
    public void El_reloj_ambiental_de_tests_no_se_fija_desde_produccion()
    {
        var medidos = ContarPorFichero(RaizDelRepositorio(), "src", PatronRelojDeTests);

        medidos.Keys.Should().Equal([FuenteUnica],
            "FijarRelojEnEsteFlujo sustituye el reloj de DiaDeNegocio.Hoy() para todo el flujo: en producción " +
            "movería el día de negocio de cualquier petición que herede el contexto");
    }

    [Fact]
    public void Los_patrones_reconocen_las_formas_que_vigilan_e_ignoran_comentarios()
    {
        string[] casan =
        [
            "        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);",
            "        var hoy = DateOnly.FromDateTime(System.DateTime.UtcNow);",
            "        var hoy = DateOnly.FromDateTime(DateTime.Today);",
            "        var hoy = DateTime.UtcNow.Date;",
            "        var hoy = reloj.GetUtcNow().UtcDateTime.Date;",
            "        var hoy = DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime);",
            "        var hoy = DateTimeOffset.UtcNow.Date;",
            "        var saludo = DateTime.Now.Hour;",
            "        var hoy = DateTimeOffset.Now;",
        ];
        foreach (var linea in casan)
            EsCodigoQueCasa(linea, PatronDiaDesdeReloj).Should().BeTrue(linea);

        string[] noCasan =
        [
            "        var ahora = DateTime.UtcNow;",
            "        var hoy = DiaDeNegocio.Hoy();",
            "        var fecha = DateOnly.FromDateTime(celda.GetDateTime());",
            "        // antes: DateOnly.FromDateTime(DateTime.UtcNow)",
            "        /// <see cref=\"DateTime.Today\"/>; el router nunca la rellena",
        ];
        foreach (var linea in noCasan)
            EsCodigoQueCasa(linea, PatronDiaDesdeReloj).Should().BeFalse(linea);

        EsCodigoQueCasa("<td>@registro.FechaUtc.ToLocalTime().ToString(\"dd/MM\")</td>", PatronZonaDelServidor)
            .Should().BeTrue();
        EsCodigoQueCasa("    TimeZoneInfo.FindSystemTimeZoneById(\"Europe/Madrid\");", PatronZonaDelServidor)
            .Should().BeTrue();
        EsCodigoQueCasa("    var local = TimeZoneInfo.ConvertTimeFromUtc(instante, DiaDeNegocio.Zona);", PatronZonaDelServidor)
            .Should().BeFalse();

        string[] instantesEnCrudo =
        [
            "<td>@registro.FechaUtc.ToString(\"dd/MM/yyyy HH:mm\")</td>",
            "        return fechaUtc.ToString(\"dd/MM/yyyy\");",
            "<td>@(clave.UltimoUsoUtc?.ToString(\"dd/MM/yyyy\"))</td>",
            "        var texto = $\"Expira el {_expiraEnUtc.Value:dd/MM/yyyy HH:mm}\";",
            "        var subtitulo = $\"generado el {DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC\";",
        ];
        foreach (var linea in instantesEnCrudo)
            EsCodigoQueCasa(linea, PatronInstanteUtcEnCrudo).Should().BeTrue(linea);

        string[] instantesConvertidos =
        [
            "<td>@registro.FechaUtc.EnHoraPeninsular().ToString(\"dd/MM/yyyy HH:mm\")</td>",
            "        var texto = $\"Expira el {_expiraEnUtc.Value.EnHoraPeninsular():dd/MM/yyyy HH:mm}\";",
            "        expiraEnUtc = _expiraEnUtc.Value.ToString(\"O\"),",
            "        var vence = documento.FechaVencimiento.ToString(\"dd/MM/yyyy\");",
            "        var hoy = esUtc ? aUtc : bUtc;",
        ];
        foreach (var linea in instantesConvertidos)
            EsCodigoQueCasa(linea, PatronInstanteUtcEnCrudo).Should().BeFalse(linea);
    }

    private static List<string> Divergencias(Dictionary<string, int> esperado, Dictionary<string, int> medido) =>
        esperado.Keys.Union(medido.Keys)
            .Select(ruta => (Ruta: ruta, Esperado: esperado.GetValueOrDefault(ruta), Medido: medido.GetValueOrDefault(ruta)))
            .Where(x => x.Esperado != x.Medido)
            .OrderBy(x => x.Ruta, StringComparer.Ordinal)
            .Select(x => $"{x.Ruta}: esperado {x.Esperado}, medido {x.Medido}")
            .ToList();

    private static bool EsCodigoQueCasa(string linea, Regex patron)
    {
        var contenido = linea.TrimStart();
        if (contenido.StartsWith("//", StringComparison.Ordinal)
            || contenido.StartsWith("*", StringComparison.Ordinal)
            || contenido.StartsWith("/*", StringComparison.Ordinal)
            || contenido.StartsWith("@*", StringComparison.Ordinal))
        {
            return false;
        }

        return patron.IsMatch(linea);
    }

    private static Dictionary<string, int> ContarPorFichero(string raiz, string carpeta, Regex patron)
    {
        var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(directorio).Should().BeTrue($"si {carpeta} cambia de sitio, este ratchet deja de vigilar nada");

        var ficheros = new[] { "*.cs", "*.razor" }
            .SelectMany(ext => Directory.EnumerateFiles(directorio, ext, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        ficheros.Should().NotBeEmpty($"{carpeta} tiene que contener código que vigilar");

        return ficheros
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace(Path.DirectorySeparatorChar, '/'),
                          Cuenta: File.ReadLines(f).Where(l => EsCodigoQueCasa(l, patron)).Sum(l => patron.Matches(l).Count)))
            .Where(x => x.Cuenta > 0)
            .ToDictionary(x => x.Ruta, x => x.Cuenta);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
