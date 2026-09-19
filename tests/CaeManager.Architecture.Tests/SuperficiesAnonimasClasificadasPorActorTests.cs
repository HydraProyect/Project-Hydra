using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Toda superficie sin sesión está clasificada por quién puede estar detrás</b>
/// (P41c, seguimiento).
///
/// <para>
/// La auditoría distingue persona, plataforma e integración externa, pero solo si
/// alguien declara el actor: sin sesión, no hay identidad de la que deducirlo. El
/// primer incremento cubrió los servicios de fondo y el handler de la clave de API
/// y se dejó los endpoints <c>AllowAnonymous</c> que escriben —el webhook de
/// Stripe entre ellos— sin mirar: su escritura seguía cayendo en
/// <c>Desconocido</c>. Este ratchet cierra esa clase de omisión, no solo los tres
/// casos conocidos.
/// </para>
///
/// <para>
/// Cada <c>AllowAnonymous</c> de <c>src</c> tiene que estar en
/// <see cref="Clasificacion"/> con su categoría y su número exacto por fichero. Uno
/// nuevo —aunque sea en un fichero ya conocido— deja el test en rojo hasta que
/// alguien decida qué clase de llamador es. Las categorías:
/// </para>
/// <list type="bullet">
/// <item><see cref="Categoria.SistemaDeTercero"/>: un webhook de proveedor,
/// acreditado por firma o <c>clientState</c>. Tiene que llevar
/// <c>ActorIntegracionExternaEndpointFilter</c> en la misma cadena, o su escritura
/// se auditaría como <c>Desconocido</c>.</item>
/// <item><see cref="Categoria.PersonaSinSesion"/>: login, doble factor,
/// restablecer contraseña, SSO. Es un humano todavía sin identificar: <b>no</b>
/// es una integración, y llamarlo así sería falso. Sigue en <c>Desconocido</c>
/// hasta que el propietario decida qué valor le corresponde; este ratchet solo
/// fija que la decisión está pendiente y localizada, no la toma.</item>
/// <item><see cref="Categoria.NoEscribe"/>: páginas legales, error, recursos
/// estáticos, comprobación de salud, contrato OpenAPI. No producen filas de
/// auditoría, así que no necesitan actor.</item>
/// </list>
///
/// <para>
/// Además, el grupo <c>/api/v1</c> (<c>RequireAuthorization("ApiPublica")</c>) no
/// es anónimo pero lo llama una integración: se comprueba aparte que su cadena
/// lleve el mismo filtro. Hoy solo mapea <c>GET</c>; el filtro es para que la
/// primera escritura futura no pase por una persona (el handler de la clave mete
/// el Id de la clave como <c>NameIdentifier</c>).
/// </para>
///
/// <para>
/// <b>Lo que observa y lo que no.</b> Observa TEXTO: que el filtro esté en la
/// cadena de llamadas. No observa que el ámbito llegue al <c>SaveChanges</c> —lo
/// demuestra <c>TipoActorDeAuditoriaBajoRuntimeTests</c>— ni qué hace un
/// endpoint que se cuelga de un grupo definido en otro fichero.
/// </para>
/// </summary>
public class SuperficiesAnonimasClasificadasPorActorTests
{
    private enum Categoria { SistemaDeTercero, PersonaSinSesion, NoEscribe }

    /// <summary>Ruta relativa a <c>src</c>, con separador <c>/</c>, → número de ocurrencias y categoría.</summary>
    private static readonly Dictionary<string, (int Cuantas, Categoria Categoria)> Clasificacion = new()
    {
        ["CaeManager.Web/Api/Comercial/WebhookStripeEndpoints.cs"] = (1, Categoria.SistemaDeTercero),
        ["CaeManager.Web/Api/Integraciones/WebhookMicrosoft365Endpoints.cs"] = (1, Categoria.SistemaDeTercero),
        ["CaeManager.Web/Api/Integraciones/WebhookWhatsAppEndpoints.cs"] = (1, Categoria.SistemaDeTercero),

        // Iniciar sesión con Microsoft y su retorno: una persona, aún sin sesión.
        ["CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs"] = (2, Categoria.PersonaSinSesion),
        ["CaeManager.Web/Components/Account/Pages/Login.razor"] = (1, Categoria.PersonaSinSesion),
        ["CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor"] = (1, Categoria.PersonaSinSesion),
        ["CaeManager.Web/Components/Account/Pages/OlvideContrasena.razor"] = (1, Categoria.PersonaSinSesion),
        ["CaeManager.Web/Components/Account/Pages/RestablecerContrasena.razor"] = (1, Categoria.PersonaSinSesion),

        ["CaeManager.Web/Components/Legal/PoliticaPrivacidad.razor"] = (1, Categoria.NoEscribe),
        ["CaeManager.Web/Components/Legal/TerminosCondiciones.razor"] = (1, Categoria.NoEscribe),
        ["CaeManager.Web/Components/Pages/Error.razor"] = (1, Categoria.NoEscribe),
        ["CaeManager.Web/Components/Pages/NotFound.razor"] = (1, Categoria.NoEscribe),
        // Recursos estáticos, /salud y el contrato OpenAPI.
        ["CaeManager.Web/Program.cs"] = (3, Categoria.NoEscribe),
    };

    private const string Filtro = "AddEndpointFilter<ActorIntegracionExternaEndpointFilter>";

    /// <summary>
    /// Toda mención de <c>AllowAnonymous</c> en código, en cualquier forma sintáctica:
    /// <c>.AllowAnonymous()</c>, <c>[AllowAnonymous]</c> sobre una clase o método,
    /// <c>@attribute [AllowAnonymous]</c> y —el caso que la primera versión de este
    /// ratchet dejaba pasar— el atributo <b>inline en un lambda</b>
    /// (<c>MapPost("/x", [AllowAnonymous] async () =&gt; …)</c>), además de
    /// <c>AllowAnonymousAttribute</c>. Se cuenta sobre el texto SIN comentarios y
    /// sin líneas <c>using</c>.
    /// </summary>
    private static readonly Regex AllowAnonymousEnCodigo = new(
        @"\bAllowAnonymous(?:Attribute)?\b",
        RegexOptions.Compiled);

    [Fact]
    public void Toda_superficie_anonima_esta_clasificada()
    {
        var reales = SuperficiesAnonimas(FuentesDeSrc());

        // Control positivo: los tres webhooks tienen que verse. Un patrón que dejara
        // de casar daría una lista vacía, y "no hay nada sin clasificar" sería falso.
        reales.Keys.Should().Contain(
            [
                "CaeManager.Web/Api/Comercial/WebhookStripeEndpoints.cs",
                "CaeManager.Web/Api/Integraciones/WebhookMicrosoft365Endpoints.cs",
                "CaeManager.Web/Api/Integraciones/WebhookWhatsAppEndpoints.cs",
            ],
            "el patrón tiene que ver las superficies anónimas que se sabe que existen");

        var sinClasificar = reales
            .Where(r => !Clasificacion.TryGetValue(r.Key, out var esperado) || esperado.Cuantas != r.Value)
            .Select(r => $"{r.Key}: {r.Value} (clasificado: " +
                         (Clasificacion.TryGetValue(r.Key, out var e) ? e.Cuantas.ToString() : "no") + ")")
            .ToList();

        sinClasificar.Should().BeEmpty(
            "una superficie sin sesión nueva tiene que decir qué clase de llamador es: sistema de un " +
            "tercero (lleva el filtro de actor), persona sin sesión, o no escribe");

        var obsoletas = Clasificacion.Keys.Except(reales.Keys).ToList();
        obsoletas.Should().BeEmpty("una entrada de clasificación que ya no existe oculta que la lista se pudrió");
    }

    [Fact]
    public void Todo_webhook_de_tercero_declara_el_actor_de_integracion_externa()
    {
        var fuentes = FuentesDeSrc().ToDictionary(f => f.Ruta, f => f.Texto);

        var terceros = Clasificacion.Where(c => c.Value.Categoria == Categoria.SistemaDeTercero).Select(c => c.Key).ToList();
        terceros.Should().NotBeEmpty();

        foreach (var ruta in terceros)
            CadenaDeAnonimoLlevaElFiltro(fuentes[ruta]).Should().BeTrue(
                $"{ruta} es AllowAnonymous y lo llama el sistema de un tercero: sin {Filtro} sus " +
                "escrituras se auditan como Desconocido");
    }

    [Fact]
    public void El_grupo_de_la_api_publica_declara_el_actor_de_integracion_externa()
    {
        var program = FuentesDeSrc().Single(f => f.Ruta == "CaeManager.Web/Program.cs").Texto;

        var inicio = program.IndexOf("RequireAuthorization(\"ApiPublica\")", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0, "el grupo /api/v1 tiene que existir para que este test observe algo");

        SentenciaQueContiene(program, inicio).Should().Contain(Filtro,
            "el grupo /api/v1 lo llama una integración con clave: sin el filtro, una escritura futura " +
            "se resolvería como Persona porque el handler mete el Id de la clave como NameIdentifier");
    }

    /// <summary>
    /// <b>El filtro solo cubre el handler, no la ejecución de su resultado.</b> En las
    /// API mínimas el <c>IResult</c> que devuelve el handler se ejecuta cuando el
    /// filtro ya ha retornado y ha liberado el ámbito (medido en
    /// <c>FiltroDeActorYEjecucionDelResultadoTests</c>). Hoy no hay ninguna fila
    /// afectada porque ningún endpoint de los grupos filtrados devuelve un resultado
    /// que escriba: solo <c>Results.Ok/NotFound/Text/StatusCode/…</c>. Este ratchet
    /// sostiene esa premisa: en <c>Api/</c> no aparece nada cuya ejecución pueda
    /// escribir o diferir trabajo más allá del handler, y en <c>src</c> nadie
    /// implementa <c>IResult</c>.
    /// </summary>
    [Fact]
    public void Los_grupos_filtrados_no_devuelven_resultados_cuya_ejecucion_escriba()
    {
        var fuentes = FuentesDeSrc();

        var api = fuentes.Where(f => f.Ruta.StartsWith("CaeManager.Web/Api/", StringComparison.Ordinal)
                                     && f.Ruta.EndsWith(".cs", StringComparison.Ordinal)).ToList();
        api.Count.Should().BeGreaterThanOrEqualTo(8,
            "control positivo: los tres webhooks y los ficheros de /api/v1 tienen que estar en el barrido");

        var infractores = api
            .SelectMany(f => ResultadoConEjecucionDiferida.Matches(SinComentarios(f.Texto)).Select(m => $"{f.Ruta}: {m.Value.Trim()}"))
            .Concat(fuentes
                .Where(f => f.Ruta.EndsWith(".cs", StringComparison.Ordinal))
                .Where(f => ImplementaIResult.IsMatch(SinComentarios(f.Texto)))
                .Select(f => $"{f.Ruta}: implementa IResult"))
            .ToList();

        infractores.Should().BeEmpty(
            "un resultado que escribe al ejecutarse lo haría fuera del ámbito de actor: esa fila se " +
            "auditaría como Desconocido. Si hace falta, declara el ámbito en un middleware que envuelva " +
            "el endpoint entero y cambia este ratchet");

        // Control positivo del patrón.
        ResultadoConEjecucionDiferida.IsMatch("return Results.Stream(x);").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("IAsyncEnumerable<Foo> Listar()").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("ctx.Response.OnCompleted(() => x);").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("return Results.Ok(x);").Should().BeFalse("un Ok(x) no escribe");
        ImplementaIResult.IsMatch("public sealed class Mio(int a) : IResult").Should().BeTrue();
        ImplementaIResult.IsMatch("public sealed class Mio : IHttpResult, IResult").Should().BeTrue();
        ImplementaIResult.IsMatch("var r = Result.Exito(x);").Should().BeFalse("Result de dominio no es IResult");
    }

    private static readonly Regex ResultadoConEjecucionDiferida = new(
        @"\b(?:Typed)?Results\.(?:Stream|File|PushStream)\s*\(|\bIAsyncEnumerable\s*<|\.OnCompleted\s*\(|\.OnStarting\s*\(|\bRegisterForDispose\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ImplementaIResult = new(
        @"^[ \t]*(?:(?:public|internal|sealed|private|protected|static)[ \t]+)*(?:class|record|struct)[ \t]+\w+[^{;=]*:[^{;=]*\bIResult\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Quita comentarios de bloque (<c>/* */</c>, <c>@* *@</c>, <c>&lt;!-- --&gt;</c>), de línea y las
    /// líneas <c>using</c>. La primera versión solo quitaba las líneas que EMPEZABAN por
    /// <c>//</c>, así que un comentario de Razor entre <c>@* *@</c> que citara
    /// <c>[AllowAnonymous]</c> contaba como superficie.
    /// </summary>
    private static string SinComentarios(string texto)
    {
        texto = Regex.Replace(texto, @"/\*.*?\*/|@\*.*?\*@|<!--.*?-->", string.Empty, RegexOptions.Singleline);
        texto = Regex.Replace(texto, @"//[^\r\n]*", string.Empty);
        return Regex.Replace(texto, @"^[ \t]*using[ \t].*$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>
    /// Control positivo del instrumento: el comprobador rechaza una cadena sin filtro
    /// y una en la que el filtro está en otra sentencia, y acepta la buena.
    /// </summary>
    [Fact]
    public void El_comprobador_distingue_la_cadena_con_filtro_de_la_que_no()
    {
        CadenaDeAnonimoLlevaElFiltro("""
            var g = app.MapGroup("/x").AllowAnonymous().WithTags("x");
            """).Should().BeFalse("sin filtro");

        CadenaDeAnonimoLlevaElFiltro("""
            var g = app.MapGroup("/x").AllowAnonymous();
            g.AddEndpointFilter<ActorIntegracionExternaEndpointFilter>();
            """).Should().BeFalse("el filtro está en otra sentencia: otro grupo, o ningún efecto");

        CadenaDeAnonimoLlevaElFiltro("""
            var g = app.MapGroup("/x")
                .AllowAnonymous()
                .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>();
            """).Should().BeTrue("la forma correcta");

        // Un comentario que nombra la llamada no es una superficie; una llamada real sí.
        SuperficiesAnonimas([("A.cs", "// cita: .AllowAnonymous() en prosa\n/// <c>[AllowAnonymous]</c>\n")])
            .Should().BeEmpty("los comentarios no cuentan");
        SuperficiesAnonimas([("B.razor", "@attribute [AllowAnonymous]\n"), ("C.cs", "x.AllowAnonymous();\n")])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["B.razor"] = 1, ["C.cs"] = 1 },
                "el atributo de Razor y la llamada de minimal API cuentan");
        SuperficiesAnonimas([("D.cs", "endpoints.MapPost(\"/x\", [AllowAnonymous] async (HttpContext c) => Results.Ok());")])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["D.cs"] = 1 },
                "el atributo inline en un lambda es una superficie anónima: la primera versión no la veía");
        SuperficiesAnonimas([("E.razor", "@* cita [AllowAnonymous] en un comentario de Razor *@\n<!-- y [AllowAnonymous] en HTML -->\n/* y en bloque AllowAnonymous */\n")])
            .Should().BeEmpty("los comentarios de bloque tampoco cuentan");
    }

    private static bool CadenaDeAnonimoLlevaElFiltro(string texto)
    {
        var indice = 0;
        var visto = false;

        while ((indice = texto.IndexOf(".AllowAnonymous()", indice, StringComparison.Ordinal)) >= 0)
        {
            visto = true;
            if (!SentenciaQueContiene(texto, indice).Contains(Filtro, StringComparison.Ordinal))
                return false;
            indice += ".AllowAnonymous()".Length;
        }

        return visto;
    }

    /// <summary>La sentencia (hasta el <c>;</c> anterior y el siguiente) que contiene la posición.</summary>
    private static string SentenciaQueContiene(string texto, int posicion)
    {
        var desde = texto.LastIndexOf(';', posicion);
        var hasta = texto.IndexOf(';', posicion);
        desde = desde < 0 ? 0 : desde + 1;
        hasta = hasta < 0 ? texto.Length : hasta;
        return texto[desde..hasta];
    }

    private static Dictionary<string, int> SuperficiesAnonimas(List<(string Ruta, string Texto)> fuentes)
        => fuentes
            .Select(f => (f.Ruta, Cuantas: AllowAnonymousEnCodigo.Matches(SinComentarios(f.Texto)).Count))
            .Where(f => f.Cuantas > 0)
            .ToDictionary(f => f.Ruta, f => f.Cuantas);

    private static List<(string Ruta, string Texto)> FuentesDeSrc()
    {
        var src = Path.Combine(RaizDelRepositorio(), "src");
        var sep = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .Where(ruta => (ruta.EndsWith(".cs", StringComparison.Ordinal) || ruta.EndsWith(".razor", StringComparison.Ordinal))
                        && !ruta.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
                        && !ruta.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(ruta => (Path.GetRelativePath(src, ruta).Replace('\\', '/'), File.ReadAllText(ruta)))
            .ToList();
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
