using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Qué seeders cruzan la frontera de identidad, y cuáles no.</b>
///
/// <para>
/// El arranque siembra con <b>dos</b> identidades distintas: la administrativa
/// (<c>dbContextBootstrap</c>, rol propietario) y la de tráfico normal
/// (<c>dbContext</c>, <c>cae_app_runtime</c> cuando está configurado). Cuál usa
/// cada seeder no es un detalle de fontanería: es la diferencia entre arrancar y
/// no arrancar.
/// </para>
///
/// <para>
/// Lo aprendimos en staging el 2026-08-23, en cuanto ese entorno adoptó el rol
/// restringido: <c>IdentitySeeder</c> murió con <c>23505</c> porque su lectura de
/// <c>EstadoBootstrapPlataforma</c> quedaba filtrada por RLS y la guarda "si no
/// existe, créala" entraba siempre. El backfill de asignaciones habría fallado
/// después por el motivo contrario —escribe una fila por tenant y la política
/// exige <c>PropietarioTenantId = app.tenant_id</c>—, aunque allí no llegó a
/// manifestarse porque sus filas ya existían.
/// </para>
///
/// <para>
/// <b>La dirección peligrosa es moverlos hacia el contexto administrativo.</b> Un
/// seeder tenant-scoped que pasara a sembrar con el rol propietario dejaría de
/// estar sujeto a RLS sin que ninguna aserción de comportamiento lo notara: sus
/// tests seguirían en verde. Por eso la lista es cerrada en los dos sentidos.
/// </para>
/// </summary>
public class FronteraDeSeedersDeBootstrapTests
{
    /// <summary>
    /// Seeders administrativos, con el motivo por el que no son tráfico de aplicación.
    /// </summary>
    private static readonly Dictionary<string, string> ConIdentidadAdministrativa = new()
    {
        ["IdentitySeeder"] =
            "escribe EstadoBootstrapPlataforma, que es estado de SISTEMA: su política de SELECT exige " +
            "app.usuario_id y en el arranque no hay sesión de usuario",
        ["AsignacionesOperativasBackfillSeeder"] =
            "es cross-tenant por diseño —lo declara al justificar IgnoreQueryFilters— y escribe una fila " +
            "por cada tenant en un solo SaveChanges",
        ["PlantillasRespuestaSeeder"] =
            "instala contenido estático de catálogo en los Tenants propietarios existentes durante el arranque; " +
            "no lee ni copia datos operativos entre ellos y filtra cada consulta por el Tenant propietario",
        // Decisión de Chris, DECISION_LOG 2026-11-16 («identidad de la siembra de la línea Inbound»; commit 26ea503 de
        // Project-Hydra-Negocio): la opción A, solo esta fila. El resto de la siembra Inbound sigue con dbContext.
        ["LineaInboundDemoSeeder"] =
            "escribe una sola fila de ProductosContratados, la línea Inbound vigente del Tenant TCE de la siembra Inbound de " +
            "Development: en esa tabla cae_app_runtime solo lee y el único rol que inserta, cae_app_aprovisionamiento, exige " +
            "una Sesión Privilegiada de Aprovisionamiento que el arranque no tiene. Solo en Development, solo sobre ese " +
            "Tenant con su marcador de demo, dentro de su ámbito explícito e idempotente",
    };

    /// <summary>
    /// Toda invocación de un seeder desde el arranque, con el texto de sus
    /// argumentos: la unidad sobre la que se decide la identidad.
    /// </summary>
    private static readonly Regex Invocacion = new(
        @"await\s+(\w+Seeder)\.\w+\(([^;]*)\)", RegexOptions.Compiled);

    private sealed record Llamada(string Seeder, string Argumentos);

    private static List<Llamada> LlamadasDelArranque()
    {
        var programa = File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        return Invocacion.Matches(programa)
            .Select(m => new Llamada(m.Groups[1].Value, m.Groups[2].Value))
            .ToList();
    }

    private static bool UsaBootstrap(Llamada llamada) =>
        Regex.IsMatch(llamada.Argumentos, @"\bdbContextBootstrap\b");

    private static bool UsaInyectado(Llamada llamada) =>
        !UsaBootstrap(llamada) && Regex.IsMatch(llamada.Argumentos, @"\bdbContext\b");

    /// <summary>
    /// <b>Cada llamada declara su identidad en el propio sitio de llamada.</b>
    ///
    /// <para>
    /// Repara un falso negativo demostrado por mutación, y de la clase de
    /// regresión más grave que este ratchet dice impedir. Bastaba un alias para
    /// que un seeder tenant-scoped pasara al rol propietario sin que nada lo
    /// notara:
    /// </para>
    /// <code>
    /// var contextoAdministrativo = dbContextBootstrap;
    /// await DelegacionesSoporteSeeder.SeedAsync(contextoAdministrativo, …);
    /// </code>
    /// <para>
    /// Esa llamada no contiene <c>dbContextBootstrap</c> —así que no entraba en
    /// la lista administrativa— ni <c>dbContext</c> —así que tampoco en la de
    /// tráfico normal—. Caía <b>fuera de las dos listas</b>, y las tres
    /// aserciones de abajo seguían pasando: la equivalencia porque la lista
    /// administrativa no cambiaba, la de no vacío porque quedaban otros seeders
    /// inyectados, y la de intersección porque no había ninguna. El seeder
    /// sembraba fuera de RLS con el ratchet en verde.
    /// </para>
    ///
    /// <para>
    /// La propiedad no es "estas dos listas contienen lo que deben": es que
    /// <b>toda</b> llamada pertenezca a una de las dos. Una llamada que no se
    /// pueda clasificar leyendo su sitio de llamada es, por sí sola, el defecto
    /// —da igual a qué contexto acabe apuntando el alias—, porque la frontera de
    /// identidad deja de ser legible justo donde se cruza.
    /// </para>
    /// </summary>
    [Fact]
    public void Toda_llamada_a_un_seeder_declara_su_identidad_en_el_sitio_de_llamada()
    {
        var llamadas = LlamadasDelArranque();

        llamadas.Should().NotBeEmpty(
            "el arranque siembra; una lista vacía significaría que el patrón ya no reconoce las " +
            "invocaciones que este test dice vigilar");

        var sinClasificar = llamadas
            .Where(l => !UsaBootstrap(l) && !UsaInyectado(l))
            .Select(l => $"{l.Seeder}({l.Argumentos.Trim()})")
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, sinClasificar).Should().BeEmpty(
            "cada seeder tiene que recibir dbContextBootstrap o dbContext de forma visible en su propia " +
            "llamada; pasarlo por un alias esconde qué identidad —y por tanto qué régimen de RLS— usa esa " +
            "siembra, que es justo lo que este ratchet existe para mantener a la vista");
    }

    [Fact]
    public void Solo_los_seeders_administrativos_declarados_usan_el_contexto_de_bootstrap()
    {
        var llamadas = LlamadasDelArranque();

        var conBootstrap = llamadas.Where(UsaBootstrap).Select(l => l.Seeder).Distinct().OrderBy(n => n).ToList();
        var conInyectado = llamadas.Where(UsaInyectado).Select(l => l.Seeder).Distinct().OrderBy(n => n).ToList();

        // Guarda del instrumento: si el patrón dejara de reconocer las llamadas,
        // las dos listas saldrían vacías y el test pasaría sin observar nada.
        conInyectado.Should().NotBeEmpty(
            "el arranque tiene que seguir sembrando algo con el contexto inyectado; dos listas vacías " +
            "significarían que este test ya no ve las llamadas que dice vigilar");

        conBootstrap.Should().BeEquivalentTo(ConIdentidadAdministrativa.Keys,
            "mover un seeder a la identidad administrativa lo saca de RLS sin que ninguna aserción de " +
            "comportamiento lo note: sus tests seguirían verdes. Añadir uno aquí exige escribir por qué " +
            "no es tráfico de aplicación, en el mismo commit");

        conBootstrap.Should().NotIntersectWith(conInyectado,
            "un mismo seeder con las dos identidades sería ambiguo: la mitad de sus escrituras estaría " +
            "sujeta a RLS y la otra mitad no");
    }

    [Fact]
    public void Cada_seeder_administrativo_declara_por_que_lo_es()
    {
        ConIdentidadAdministrativa.Should().NotContain(
            e => string.IsNullOrWhiteSpace(e.Value),
            "salir de RLS es una decisión de seguridad, y una decisión de seguridad sin motivo escrito " +
            "es un descuido que nadie podrá revisar después");
    }

    /// <summary>
    /// <b>«Solo en Development» es una promesa que el propio seeder tiene que cumplir.</b>
    ///
    /// <para>
    /// Un seeder de la lista administrativa siembra con el rol propietario, fuera de RLS. Cuando su motivo dice
    /// «Solo en Development», lo que lo mantiene fuera de staging y de producción es una guarda
    /// <c>IsDevelopment()</c> dentro de su propio código. Hoy esa guarda solo la fijan los tests de cada seeder: un
    /// seeder nuevo que entrara en la lista declarando esa promesa podría entrar sin guarda, y sembraría con el rol
    /// propietario en cualquier entorno sin que ningún ratchet lo notara. El test recorre el diccionario, sin una
    /// lista aparte, así que cubre a todo seeder que declare esa promesa, hoy y en el futuro.
    /// </para>
    ///
    /// <para>
    /// <b>Qué cuenta como guarda.</b> Se quitan antes los comentarios (<c>//</c>, <c>///</c> y <c>/* */</c>) y el
    /// contenido de los literales de cadena, para que mencionar <c>IsDevelopment()</c> en un comentario o en un
    /// mensaje no cuente. Sobre el código que queda se exige un <c>if</c> cuyo cuerpo empiece por <c>return</c> o
    /// <c>throw</c> (la salida temprana) y cuya condición, partida por <c>||</c>, tenga un disyunto que sea
    /// exactamente <c>!receptor.IsDevelopment()</c>: <c>!entorno.IsDevelopment()</c> solo, o junto a otras
    /// condiciones como <c>!activo || !entorno.IsDevelopment()</c>. Cualquier otra forma de condición (con
    /// <c>&amp;&amp;</c>, <c>&amp;</c>, <c>?:</c>, doble negación o la llamada dentro de una lambda) no se acepta, porque
    /// ya no garantiza salir siempre que el entorno no sea Development. Una forma legítima que no encaje se
    /// reescribe a esa, o se amplía este criterio con su propio caso en el test del detector; el fallo avisa.
    /// </para>
    ///
    /// <para>
    /// <b>Límites declarados.</b> El detector es léxico y trabaja por fichero: comprueba que la guarda existe, no
    /// que esté en el camino de ejecución de la escritura (un segundo punto de entrada sin guarda, una guarda tras
    /// la escritura o bajo <c>#if DEBUG</c> no se distinguen), y la vigilancia depende de que el motivo contenga la
    /// frase «Solo en Development» tal cual. Lo que sí garantiza es que un seeder no entra en la lista con esa
    /// promesa y sin ninguna guarda en su código.
    /// </para>
    ///
    /// <para>
    /// El fichero del seeder se localiza por nombre de clase bajo <c>src/</c>; si no aparece exactamente uno, el
    /// test falla en vez de saltarse al seeder, porque un seeder que no se encuentra es uno que no se vigila.
    /// </para>
    /// </summary>
    [Fact]
    public void Todo_seeder_administrativo_que_declara_solo_en_Development_lleva_la_guarda_en_su_codigo()
    {
        var declaran = ConIdentidadAdministrativa
            .Where(e => e.Value.Contains(MarcadorSoloEnDevelopment, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Key)
            .OrderBy(n => n)
            .ToList();

        // Guarda del instrumento: si el filtro dejara de casar con ningún motivo, el bucle de abajo no
        // observaría nada y el test pasaría en vacío.
        declaran.Should().NotBeEmpty(
            $"al menos un seeder administrativo declara «{MarcadorSoloEnDevelopment}» en su motivo; una lista " +
            "vacía significaría que el filtro ya no casa con el diccionario y este test no vigila nada");

        var raiz = RaizDelRepositorio();
        var defectos = new List<string>();

        foreach (var seeder in declaran)
        {
            var ficheros = LocalizarFuenteDeClase(raiz, seeder);

            if (ficheros.Count != 1)
            {
                defectos.Add(
                    $"{seeder}: se esperaba exactamente un fichero con «class {seeder}» bajo src/ y hay " +
                    $"{ficheros.Count}" + (ficheros.Count == 0 ? "" : ": " + string.Join(", ", ficheros)));
                continue;
            }

            var codigo = SinComentariosNiLiterales(File.ReadAllText(Path.Combine(raiz, ficheros[0])));

            if (!TieneGuardaDeDevelopment(codigo))
                defectos.Add(
                    $"{seeder} ({ficheros[0]}): su motivo dice «{MarcadorSoloEnDevelopment}» pero su código no " +
                    "contiene una guarda real: un if que niegue IsDevelopment() y salga con return o throw");
        }

        string.Join(Environment.NewLine, defectos).Should().BeEmpty(
            "un seeder administrativo escribe con el rol propietario, fuera de RLS; si declara que es solo de " +
            "Development, la guarda IsDevelopment() tiene que estar en su propio código y no solo en sus tests, " +
            "porque es lo único que evita que siembre en staging o en producción");
    }

    /// <summary>
    /// El detector de guardas es el instrumento del test anterior: se prueba contra casos construidos para
    /// que no dé por buena una guarda que no lo es (comentario, cadena, condición con <c>&amp;&amp;</c>, falta de
    /// salida) y acepte las formas de guarda legítimas, con y sin condiciones adicionales.
    /// </summary>
    [Theory]
    [InlineData("var a = @\"if (!entorno.IsDevelopment()) return;\";", false)]
    [InlineData("var a = $\"x {y} if (!entorno.IsDevelopment()) return;\";", false)]
    [InlineData("var a = \"\"\"if (!entorno.IsDevelopment()) return;\"\"\"; var b = 1;", false)]
    [InlineData("var c = '\"'; if (!entorno.IsDevelopment()) return;", true)]
    [InlineData("var a = @\"hola\"\" mundo\"; if (!entorno.IsDevelopment()) return;", true)]
    [InlineData("var a = \"\"\"\"x \"\"\" y\"\"\"\"; if (!entorno.IsDevelopment()) return;", true)]
    [InlineData("if (!entorno.IsDevelopment() && activo) return;", false)]
    [InlineData("if (activo & !entorno.IsDevelopment()) return;", false)]
    [InlineData("if (!!entorno.IsDevelopment()) return;", false)]
    [InlineData("if (lista.Any(x => !x.IsDevelopment())) return;", false)]
    [InlineData("if (cond ? !entorno.IsDevelopment() : false) return;", false)]
    [InlineData("if (!entorno.IsDevelopment()) throw new InvalidOperationException(\"x\");", true)]
    [InlineData("if (!entorno.IsDevelopment())\n    return;", true)]
    [InlineData("if (!configuration.GetValue<bool>(\"A:B\") || !entorno.IsDevelopment()) { return; }", true)]
    [InlineData("// if (!entorno.IsDevelopment()) return;\nvar x = 1;", false)]
    [InlineData("/// <c>if (!entorno.IsDevelopment()) return;</c>\nvar x = 1;", false)]
    [InlineData("/* if (!entorno.IsDevelopment()) return; */ var x = 1;", false)]
    [InlineData("var m = \"if (!entorno.IsDevelopment()) return;\";", false)]
    [InlineData("if (activo && !entorno.IsDevelopment()) return;", false)]
    [InlineData("if (entorno.IsDevelopment()) return;", false)]
    [InlineData("if (!entorno.IsDevelopment()) logger.LogWarning(\"x\");", false)]
    [InlineData("var esDev = entorno.IsDevelopment();", false)]
    public void El_detector_de_guarda_de_Development_solo_acepta_una_salida_temprana_real(string fuente, bool esperado)
    {
        TieneGuardaDeDevelopment(SinComentariosNiLiterales(fuente)).Should().Be(esperado);
    }

    private const string MarcadorSoloEnDevelopment = "Solo en Development";

    private static readonly Regex IfConSalida = new(
        @"\bif\s*\(([^;{}]*)\)\s*(?:\{\s*)?(?:return|throw)\b", RegexOptions.Compiled);

    private static readonly Regex NegacionDeIsDevelopment = new(
        @"^\s*!\s*[\w.]+\s*\.\s*IsDevelopment\s*\(\s*\)\s*$", RegexOptions.Compiled);

    private static bool TieneGuardaDeDevelopment(string codigoSinComentarios) =>
        IfConSalida.Matches(codigoSinComentarios)
            .Select(m => m.Groups[1].Value)
            .Any(condicion => condicion.Split("||").Any(NegacionDeIsDevelopment.IsMatch));

    private static List<string> LocalizarFuenteDeClase(string raiz, string clase)
    {
        var separador = Path.DirectorySeparatorChar;
        var patron = new Regex($@"\bclass\s+{Regex.Escape(clase)}\b");

        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}"))
            .Where(f =>
            {
                var texto = File.ReadAllText(f);
                return texto.Contains(clase, StringComparison.Ordinal)
                    && patron.IsMatch(SinComentariosNiLiterales(texto));
            })
            .Select(f => Path.GetRelativePath(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Quita comentarios y vacía los literales de cadena, carácter incluido, de un fuente C#. No es un analizador
    /// completo —las llaves de interpolación no se interpretan—, pero cubre lo que importa aquí: que una mención
    /// de <c>IsDevelopment()</c> en un comentario o en un mensaje de excepción no se confunda con una guarda.
    /// </summary>
    private static string SinComentariosNiLiterales(string codigo)
    {
        var salida = new StringBuilder(codigo.Length);
        var i = 0;

        while (i < codigo.Length)
        {
            var c = codigo[i];
            var siguiente = i + 1 < codigo.Length ? codigo[i + 1] : '\0';

            if (c == '/' && siguiente == '/')
            {
                while (i < codigo.Length && codigo[i] != '\n')
                    i++;
            }
            else if (c == '/' && siguiente == '*')
            {
                var fin = codigo.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = fin < 0 ? codigo.Length : fin + 2;
                salida.Append(' ');
            }
            else if (c == '"' && string.CompareOrdinal(codigo, i, "\"\"\"", 0, 3) == 0
                && !(i > 0 && codigo[i - 1] == '@'))
            {
                // Literal de cadena sin escapes: abre con N comillas (N >= 3) y cierra con otras N.
                var comillas = 0;
                while (i + comillas < codigo.Length && codigo[i + comillas] == '"')
                    comillas++;

                var fin = codigo.IndexOf(new string('"', comillas), i + comillas, StringComparison.Ordinal);
                i = fin < 0 ? codigo.Length : fin + comillas;
                salida.Append("\"\"");
            }
            else if (c == '"')
            {
                var literal = i > 0 && (codigo[i - 1] == '@' || (i > 1 && codigo[i - 1] == '$' && codigo[i - 2] == '@'));
                i++;

                while (i < codigo.Length)
                {
                    if (literal && codigo[i] == '"' && i + 1 < codigo.Length && codigo[i + 1] == '"')
                        i += 2;
                    else if (!literal && codigo[i] == '\\')
                        i += 2;
                    else if (codigo[i] == '"')
                        break;
                    else
                        i++;
                }

                i = Math.Min(i + 1, codigo.Length);
                salida.Append("\"\"");
            }
            else if (c == '\'')
            {
                var fin = codigo.IndexOf('\'', i + (siguiente == '\\' ? 3 : 2));
                i = fin < 0 ? codigo.Length : fin + 1;
                salida.Append("''");
            }
            else
            {
                salida.Append(c);
                i++;
            }
        }

        return salida.ToString();
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
