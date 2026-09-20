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
/// <b>Lo que observa y lo que no.</b> Observa TEXTO sin comentarios —quitados por
/// <c>LimpiadorDeComentarios</c>, cuyo recorrido contrasta <c>LimpiadorDeComentariosTests</c> con
/// Roslyn y con cada fuente real de <c>src</c>—: que el filtro esté en
/// la cadena de llamadas. No observa que el ámbito llegue al <c>SaveChanges</c> —lo
/// demuestra <c>TipoActorDeAuditoriaBajoRuntimeTests</c>— ni qué hace un
/// endpoint que se cuelga de un grupo definido en otro fichero.
/// </para>
///
/// <para>
/// <b>Hueco con nombre (no cerrado a propósito).</b> Solo ve superficies <i>anónimas</i>
/// y el grupo <c>/api/v1</c>. Un webhook de proveedor NO anónimo, autenticado con un
/// esquema propio (el patrón de <c>ApiKeyAuthenticationHandler</c> y
/// <c>ExtensionAuthenticationHandler</c> en <c>Program.cs</c>) más su propia política,
/// no lleva ninguno de los dos marcadores: nada le exigiría el filtro, y si su handler
/// mete un <c>NameIdentifier</c> como hace el de clave, <c>ResolverTipoActor()</c> lo
/// resolvería como <c>Persona</c>. Hoy no existe ninguno: los dos esquemas propios son la
/// clave de API (cubierta por su ámbito y por el filtro de <c>/api/v1</c>) y la extensión
/// de navegador, que reconstruye el principal de una persona. Ampliar el trinquete a
/// «superficie de tercero» agrandaría este incremento; queda anotado para cuando aparezca.
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

    internal const string Filtro = "AddEndpointFilter<ActorIntegracionExternaEndpointFilter>";

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
        @"\bI?AllowAnonymous(?:Attribute)?\b",
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
        // Texto SIN comentarios: con el texto crudo, un comentario que nombrase el
        // filtro ("falta .AddEndpointFilter<...>() aquí") caía dentro de la sentencia y
        // el `Contains` daba verde con el filtro AUSENTE (hallazgo de la revisión previa,
        // reproducido con esa mutación).
        var fuentes = FuentesSinComentarios().ToDictionary(f => f.Ruta, f => f.Texto);

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
        // Sin comentarios, por lo mismo que arriba: el comentario P41c de Program.cs
        // precede al grupo y nombra el filtro.
        var program = FuentesSinComentarios().Single(f => f.Ruta == "CaeManager.Web/Program.cs").Texto;

        var inicio = program.IndexOf("RequireAuthorization(\"ApiPublica\")", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0, "el grupo /api/v1 tiene que existir para que este test observe algo");

        SentenciaQueContiene(program, inicio).Should().Contain(Filtro,
            "el grupo /api/v1 lo llama una integración con clave: sin el filtro, una escritura futura " +
            "se resolvería como Persona porque el handler mete el Id de la clave como NameIdentifier");
    }

    /// <summary>
    /// <b>La premisa de un escaneo textual.</b> Este ratchet solo ve las superficies que
    /// escriben <c>AllowAnonymous</c>. Es válido porque <c>Program.cs</c> fija un
    /// <c>FallbackPolicy</c> que exige usuario autenticado: sin esa política, un endpoint
    /// que no declarase nada sería anónimo por omisión y el escaneo sería ciego de raíz.
    /// Se comprueba en vez de suponerse.
    /// </summary>
    [Fact]
    public void El_escaneo_de_AllowAnonymous_es_valido_porque_hay_un_FallbackPolicy_que_exige_sesion()
    {
        var program = FuentesSinComentarios().Single(f => f.Ruta == "CaeManager.Web/Program.cs").Texto;

        var inicio = program.IndexOf("options.FallbackPolicy", StringComparison.Ordinal);
        inicio.Should().BeGreaterThan(0, "Program.cs tiene que fijar un FallbackPolicy");

        SentenciaQueContiene(program, inicio).Should().Contain("RequireAuthenticatedUser()",
            "sin exigir usuario autenticado, un endpoint que no declara nada sería anónimo por omisión " +
            "y este ratchet no lo vería");
    }

    /// <summary>
    /// <b>El filtro solo cubre el handler, no la ejecución de su resultado.</b> En las
    /// API mínimas el <c>IResult</c> que devuelve el handler se ejecuta cuando el
    /// filtro ya ha retornado y ha liberado el ámbito (medido en
    /// <c>FiltroDeActorYEjecucionDelResultadoTests</c>). Hoy no hay ninguna fila
    /// afectada porque ningún endpoint de los grupos filtrados devuelve un resultado
    /// que escriba: solo <c>Results.Ok/NotFound/Text/StatusCode/…</c>.
    ///
    /// <para>
    /// <b>Qué mira exactamente, sin decir más.</b> Mira las construcciones que ejecutan
    /// código DESPUÉS de que el handler retorne, en el contexto de la petición:
    /// <c>Results.Stream/File/PushStream</c>, <c>IAsyncEnumerable</c> (que se enumera al
    /// serializar), <c>OnCompleted</c>, <c>OnStarting</c>, <c>RegisterForDispose[Async]</c>
    /// y, en <c>src</c>, las clases que implementan <c>IResult</c> directamente.
    /// </para>
    ///
    /// <para>
    /// <b>Qué NO mira, a propósito:</b> el trabajo que el propio handler pone en marcha.
    /// <c>Task.Run</c> lanzado desde el handler hereda el ámbito (el
    /// <c>ExecutionContext</c> fluye; medido en <c>FiltroDeActorYEjecucionDelResultadoTests</c>),
    /// y una señal a un servicio de fondo (<c>senal.Despertar()</c> en el webhook de
    /// WhatsApp) hace que escriba ese servicio bajo <c>EstablecerSistema()</c>, no el
    /// filtro. Ninguna de las dos es «ejecución del resultado», y por eso no están en la
    /// lista; sí cambian QUIÉN queda como actor de esas filas (ver la PR).
    /// </para>
    ///
    /// <para>
    /// <b>Alcance de <c>ImplementaIResult</c>:</b> prohíbe la implementación directa en
    /// TODO <c>src</c>, más allá de los grupos filtrados, porque textualmente no se puede
    /// saber qué endpoint devuelve qué tipo, con cualquier combinación de modificadores
    /// (<c>partial</c>, <c>abstract</c>, <c>readonly</c>, <c>file</c>, <c>unsafe</c>…) y de atributos
    /// delante. No ve herencia indirecta (<c>class X : BaseResultado</c>, ni una interfaz propia que
    /// extienda <c>IResult</c>) ni una declaración que no empiece su línea
    /// (<c>namespace N { class X : IResult }</c> en una sola). Si algún día hace falta un <c>IResult</c> propio
    /// legítimo, se añade a <see cref="ResultadosPermitidos"/> tras comprobar que su
    /// <c>ExecuteAsync</c> no escribe nada auditable; no se debilita la expresión regular.
    /// </para>
    /// </summary>
    [Fact]
    public void Los_grupos_filtrados_no_devuelven_resultados_cuya_ejecucion_escriba()
    {
        var fuentes = FuentesSinComentarios();

        var api = fuentes.Where(f => f.Ruta.StartsWith("CaeManager.Web/Api/", StringComparison.Ordinal)
                                     && f.Ruta.EndsWith(".cs", StringComparison.Ordinal)).ToList();
        api.Count.Should().BeGreaterThanOrEqualTo(8,
            "control positivo: los tres webhooks y los ficheros de /api/v1 tienen que estar en el barrido");

        var infractores = api
            .SelectMany(f => ResultadoConEjecucionDiferida.Matches(f.Texto).Select(m => $"{f.Ruta}: {m.Value.Trim()}"))
            .Concat(fuentes
                .Where(f => f.Ruta.EndsWith(".cs", StringComparison.Ordinal))
                .SelectMany(f => ImplementaIResult.Matches(f.Texto).Select(m => (f.Ruta, Tipo: m.Groups["tipo"].Value)))
                .Where(x => !ResultadosPermitidos.Contains(x.Tipo))
                .Select(x => $"{x.Ruta}: {x.Tipo} implementa IResult"))
            .ToList();

        infractores.Should().BeEmpty(
            "un resultado que escribe al ejecutarse lo haría fuera del ámbito de actor: esa fila se " +
            "auditaría como Desconocido. Si hace falta, declara el ámbito en un middleware que envuelva " +
            "el endpoint entero y cambia este ratchet");

        // Control positivo del patrón.
        ResultadoConEjecucionDiferida.IsMatch("return Results.Stream(x);").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("IAsyncEnumerable<Foo> Listar()").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("ctx.Response.OnCompleted(() => x);").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("ctx.Response.RegisterForDisposeAsync(x);").Should().BeTrue(
            "la variante Async: la primera versión exigía el paréntesis justo tras RegisterForDispose");
        ResultadoConEjecucionDiferida.IsMatch("ctx.Response.RegisterForDispose(x);").Should().BeTrue();
        ResultadoConEjecucionDiferida.IsMatch("return Results.Ok(x);").Should().BeFalse("un Ok(x) no escribe");
        ResultadoConEjecucionDiferida.IsMatch("senal.Despertar(); await Task.Run(() => 1);").Should().BeFalse(
            "el trabajo que el propio handler pone en marcha NO es ejecución del resultado: ver el resumen del test");
        ImplementaIResult.Match("public sealed class Mio(int a) : IResult").Groups["tipo"].Value.Should().Be("Mio");
        ImplementaIResult.IsMatch("public sealed class Mio : IHttpResult, IResult").Should().BeTrue();
        ImplementaIResult.IsMatch("var r = Result.Exito(x);").Should().BeFalse("Result de dominio no es IResult");
        ImplementaIResult.IsMatch("public class Derivado : BaseResultado").Should().BeFalse(
            "límite conocido y declarado: no ve herencia indirecta");
        // Las formas que la lista cerrada de modificadores dejaba pasar (hallazgo P1-3).
        foreach (var declaracion in new[]
                 {
                     "internal sealed partial class Mio : IResult",
                     "public partial class Mio : IResult",
                     "internal abstract class Mio : IResult",
                     "public readonly struct Mio : IResult",
                     "file class Mio : IResult",
                     "internal unsafe class Mio : IResult",
                     "[Foo] public record struct Mio(int A) : IResult",
                     "public partial record Mio(int A) : IResult",
                     "    public new sealed class Mio<T> : IResult",
                     "public ref struct Mio : IResult",
                     // Hallazgos de la pasada de Codex: identificador verbatim y `]` dentro del atributo.
                     "public sealed class @Resultado : IResult",
                     "[Tipo(typeof(int[]))] public sealed class Resultado : IResult",
                     "[Tipo(new[] { 1 }), Otro] internal partial class Resultado : IResult",
                 })
            ImplementaIResult.IsMatch(declaracion).Should().BeTrue($"«{declaracion}» es una implementación directa de IResult");
        ImplementaIResult.IsMatch("public partial class Mio : IDisposable").Should().BeFalse("control negativo: no implementa IResult");
    }

    private static readonly Regex ResultadoConEjecucionDiferida = new(
        @"\b(?:Typed)?Results\.(?:Stream|File|PushStream)\s*\(|\bIAsyncEnumerable\s*<|\.OnCompleted\s*\(|\.OnStarting\s*\(|\bRegisterForDispose(?:Async)?\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Declaración de tipo que lista <c>IResult</c> entre sus bases, con cualquier
    /// combinación de modificadores (<c>partial</c>, <c>abstract</c>, <c>readonly</c>,
    /// <c>file</c>, <c>unsafe</c>, <c>new</c>, <c>ref</c>…) y atributos delante, y con
    /// <c>class</c>, <c>record</c>, <c>record struct</c> o <c>struct</c>. Los modificadores
    /// se enumeran porque la lista cerrada anterior dejaba pasar <c>internal sealed partial
    /// class</c> (hallazgo P1-3 de la revisión previa). Para el operador de los modificadores
    /// vale lo mismo que para el resto del trinquete: un modificador que se le olvide es un
    /// <c>IResult</c> invisible, no una regla más laxa.
    /// </summary>
    internal static readonly Regex ImplementaIResult = new(
        @"^[ \t]*(?:\[(?:[^\[\]\r\n]|\[[^\[\]\r\n]*\])*\][ \t]*)*(?:(?:public|internal|private|protected|sealed|static|partial|abstract|readonly|file|unsafe|new|ref)[ \t]+)*(?:record[ \t]+struct|record[ \t]+class|class|record|struct)[ \t]+(?<tipo>@?\w+)[^{;=]*:[^{;=]*\bIResult\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Tipos que implementan <c>IResult</c> y se han revisado: su <c>ExecuteAsync</c> no
    /// escribe nada auditable. Vacío hoy. Añadir uno exige esa comprobación, no relajar el patrón.
    /// </summary>
    private static readonly HashSet<string> ResultadosPermitidos = new(StringComparer.Ordinal);

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

        // El hueco de la revisión previa: el filtro AUSENTE con un comentario que lo nombra
        // no puede dar verde. El comprobador recibe el texto ya sin comentarios.
        CadenaDeAnonimoLlevaElFiltro(LimpiadorDeComentarios.Quitar("""
            // TODO: falta .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>() aquí
            var g = app.MapGroup("/x")
                .AllowAnonymous();
            """, razor: false)).Should().BeFalse("un comentario que nombra el filtro no es el filtro");
        // Un `//` dentro de una cadena no es un comentario: la versión anterior cortaba la línea
        // (y el código que la seguía).
        LimpiadorDeComentarios.Quitar("""var url = "https://x.example/a"; var g = app.MapGroup("/x").AllowAnonymous(); // fin""", razor: false)
            .Should().Contain(".AllowAnonymous()").And.Contain("https://x.example/a").And.NotContain("fin");
        LimpiadorDeComentarios.Quitar("""var r = @"C:\a//b"; var raw = "quo"; x.AllowAnonymous(); /* c */ var c = '"'; y();""", razor: false)
            .Should().Contain("x.AllowAnonymous();").And.Contain("y();").And.NotContain("c */");

        // Un comentario que nombra la llamada no es una superficie; una llamada real sí.
        SuperficiesAnonimas([("A.cs", "// cita: .AllowAnonymous() en prosa\n/// <c>[AllowAnonymous]</c>\n")])
            .Should().BeEmpty("los comentarios no cuentan");
        SuperficiesAnonimas([("B.razor", "@attribute [AllowAnonymous]\n"), ("C.cs", "x.AllowAnonymous();\n")])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["B.razor"] = 1, ["C.cs"] = 1 },
                "el atributo de Razor y la llamada de minimal API cuentan");
        // El marcador `IAllowAnonymous` también deja un endpoint sin sesión (P2 de la revisión previa); hoy no aparece en src.
        SuperficiesAnonimas([("G.cs", "builder.WithMetadata(new IAllowAnonymous_Marca()); class M : IAllowAnonymous { }")])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["G.cs"] = 1 });
        SuperficiesAnonimas([("D.cs", "endpoints.MapPost(\"/x\", [AllowAnonymous] async (HttpContext c) => Results.Ok());")])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["D.cs"] = 1 },
                "el atributo inline en un lambda es una superficie anónima: la primera versión no la veía");
        SuperficiesAnonimas([("E.razor", "@* cita [AllowAnonymous] en un comentario de Razor *@\n<!-- y [AllowAnonymous] en HTML -->\n@code {\n    /* y en bloque de C# AllowAnonymous */\n}\n")])
            .Should().BeEmpty("los comentarios de Razor y los de C# dentro de un bloque de código no cuentan");
        // Un `/* */` en el MARCADO no es un comentario de Razor: la versión anterior lo quitaba y, con él, todo lo que
        // hubiera hasta el siguiente `*/` (hallazgo P1-2). Contar de más es rojo; quitar de más es verde con la regla violada.
        SuperficiesAnonimas([("F.razor", "/* no es un comentario en el marcado AllowAnonymous */\n")])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["F.razor"] = 1 });
    }

    internal static bool CadenaDeAnonimoLlevaElFiltro(string texto)
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

    internal static Dictionary<string, int> SuperficiesAnonimas(List<(string Ruta, string Texto)> fuentes)
        => fuentes
            .Select(f => (f.Ruta, Cuantas: AllowAnonymousEnCodigo.Matches(
                LimpiadorDeComentarios.Quitar(f.Texto, f.Ruta.EndsWith(".razor", StringComparison.Ordinal))).Count))
            .Where(f => f.Cuantas > 0)
            .ToDictionary(f => f.Ruta, f => f.Cuantas);

    /// <summary>Las fuentes de <c>src</c> con los comentarios ya quitados: es lo que miran todos los comprobadores de cadena.</summary>
    private static List<(string Ruta, string Texto)> FuentesSinComentarios()
        => FuentesDeSrc()
            .Select(f => (f.Ruta, LimpiadorDeComentarios.Quitar(f.Texto, f.Ruta.EndsWith(".razor", StringComparison.Ordinal))))
            .ToList();

    internal static List<(string Ruta, string Texto)> FuentesDeSrc()
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
