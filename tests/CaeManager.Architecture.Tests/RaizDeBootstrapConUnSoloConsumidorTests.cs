using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>La raíz de bootstrap autoriza una sola cosa y tiene un solo consumidor:</b>
/// crear la concesión fundacional.
///
/// <para>
/// A2 cambió de qué está hecha esa raíz. Ya <b>no</b> consulta
/// <c>EsPlataforma</c>: ese tenant es también el operativo de la empresa, así
/// que cualquiera de sus miembros podía acuñarse autoridad — una carrera de
/// privilegios, no un bootstrap. Ahora la raíz es una <b>persona designada por
/// el despliegue</b> y el acto se consume una sola vez.
/// </para>
///
/// <para>
/// Antes de A0, <c>IAutorizacionAperturaSesion</c> servía a dos comandos y por
/// eso no era una raíz: era una autoridad operativa transversal con dos usos.
/// Abrir una sesión estaba autorizado por la pertenencia al tenant de
/// plataforma, sin que ninguna concesión nombrara a nadie.
/// </para>
/// <code>
/// antes:  EsPlataforma ─┬─→ AutoConcederPrivilegio
///                       └─→ AbrirSesionPrivilegiada     ← la segunda vía
///
/// A0:     EsPlataforma ──→ AutoConcederPrivilegio ──→ concesión ──→ abrir
///
/// A2:     identidad raíz designada ──→ política de auto-concesión
///                                  ──→ concesión fundacional (una vez)
/// </code>
///
/// <para>
/// <b>Por qué un ratchet y no solo los tests de comportamiento.</b> Volver a
/// inyectar la raíz en la apertura no rompería ninguna aserción existente: los
/// casos que hoy abren seguirían abriendo, porque el técnico de plataforma
/// cumpliría las dos condiciones a la vez. La regresión sería invisible por
/// comportamiento y solo se ve por la forma.
/// </para>
///
/// <para>
/// <b>Esto no congela el número de consumidores para siempre.</b> Congela que
/// añadir uno sea un acto deliberado que pasa por esta lista y por su
/// justificación, en vez de llegar como efecto colateral de una inyección de
/// dependencias.
/// </para>
/// </summary>
public class RaizDeBootstrapConUnSoloConsumidorTests
{
    /// <summary>
    /// El único consumidor legítimo, con su motivo. Cuando todavía no existe
    /// ninguna concesión, no hay autoridad de la que derivar la primera: esa es
    /// la única circunstancia que justifica una autoridad que no venga de una
    /// concesión.
    /// </summary>
    private static readonly string[] ConsumidoresAutorizados =
    [
        // A2 movió el consumo del handler a la política de auto-concesión, y es
        // el sitio correcto: el comando pregunta "¿puede este usuario darse esta
        // capacidad?" y no debe conocer la implementación concreta de la raíz.
        //
        // La propiedad que se vigila NO es "la interfaz aparece dentro del
        // handler" —eso ataba el ratchet a un límite de abstracción concreto—
        // sino "la autorización de bootstrap se consume desde un único sitio, y
        // ese sitio es la política de auto-concesión".
        "src/CaeManager.Infrastructure/Plataforma/AutorizacionAutoConcesionPorMatriz.cs",
    ];

    /// <summary>
    /// Lo que no es un consumidor, enumerado y con su razón — nunca una rama
    /// implícita dentro del filtro.
    ///
    /// <para>
    /// La exclusión es <b>por ruta exacta</b>, y eso importa: registrar la raíz
    /// desde cualquier otro sitio hace aparecer ese fichero como consumidor y
    /// pone el ratchet rojo. Cambiar el <i>modo</i> de registro dentro del mismo
    /// fichero —fábrica, <c>TryAddScoped</c>, lo que sea— no lo rompe, porque lo
    /// que se excluye es la raíz de composición, no una forma sintáctica.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> NoSonConsumidores = new()
    {
        ["src/CaeManager.Application/Plataforma/IRaizBootstrapPlataforma.cs"] =
            "el contrato: es la pieza, no quien la usa",
        ["src/CaeManager.Infrastructure/Plataforma/RaizBootstrapPorIdentidadDesignada.cs"] =
            "la implementación: ídem",
        ["src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs"] =
            "raíz de composición, no consumidor operativo",
    };

    /// <summary>
    /// Se buscan <b>los dos nombres</b>, el del contrato y el de la
    /// implementación, y la razón es un fallo real de la primera versión de este
    /// ratchet: buscar solo la interfaz medía una propiedad más estrecha que la
    /// que el test promete. Un consumidor que dependiera de la clase concreta no
    /// contendría el nombre de la interfaz, así que jamás habría entrado en el
    /// conjunto escaneado y el ratchet habría pasado en verde con dos
    /// consumidores.
    ///
    /// <para>
    /// La propiedad no es "nadie nombra la interfaz": es <b>nadie depende de la
    /// raíz</b>, por el camino que sea.
    /// </para>
    /// </summary>
    [Fact]
    public void La_raiz_de_bootstrap_solo_la_consume_la_politica_de_auto_concesion()
    {
        var raiz = RaizDelRepositorio();
        var origen = Path.Combine(raiz, "src");

        var consumidores = Directory
            .EnumerateFiles(origen, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(a => new
            {
                Ruta = Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'),
                Texto = File.ReadAllText(a),
            })
            .Where(a => a.Texto.Contains("IRaizBootstrapPlataforma")
                        || a.Texto.Contains("RaizBootstrapPorIdentidadDesignada"))
            .Select(a => a.Ruta)
            .Where(r => !NoSonConsumidores.ContainsKey(r))
            .OrderBy(r => r)
            .ToList();

        consumidores.Should().BeEquivalentTo(ConsumidoresAutorizados,
            "la raíz de bootstrap existe para romper el ciclo conceder↔abrir, y solo para eso: cualquier " +
            "segundo consumidor la convierte otra vez en una autoridad operativa transversal");
    }

    /// <summary>
    /// Las piezas que la lista de exclusión declara tienen que existir. Sin
    /// esto, renombrar o mover cualquiera de ellas dejaría una entrada muerta y
    /// el ratchet seguiría verde ignorando un fichero que ya no es el que se
    /// quiso excluir.
    /// </summary>
    [Fact]
    public void Las_exclusiones_del_ratchet_apuntan_a_ficheros_que_existen()
    {
        var raiz = RaizDelRepositorio();

        foreach (var (ruta, motivo) in NoSonConsumidores)
            File.Exists(Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar)))
                .Should().BeTrue($"la exclusión \"{motivo}\" apunta a {ruta}");
    }

    /// <summary>
    /// La mitad que importa del test anterior, dicha aparte porque es la
    /// propiedad concreta que A0 establece: la ceremonia de apertura no puede
    /// volver a preguntar por la pertenencia al tenant de plataforma, ni por la
    /// raíz que la representa.
    /// </summary>
    [Fact]
    public void La_apertura_de_sesiones_no_consulta_la_pertenencia_a_la_plataforma()
    {
        var raiz = RaizDelRepositorio();
        var apertura = Path.Combine(
            raiz, "src", "CaeManager.Application", "Plataforma", "Commands", "AbrirSesionPrivilegiada",
            "AbrirSesionPrivilegiadaCommand.cs");

        var texto = File.ReadAllText(apertura);

        texto.Should().NotContain("IRaizBootstrapPlataforma",
            "quien abre lo hace porque una concesión lo nombra, no porque pertenezca a la plataforma");
        texto.Should().NotContain("RaizBootstrapPorIdentidadDesignada",
            "tampoco por la clase concreta: los dos nombres son disjuntos como texto, así que comprobar " +
            "solo el de la interfaz dejaba pasar la dependencia directa sobre la implementación");
        texto.Should().NotContain("EsPlataforma",
            "la pertenencia dejó de ser suficiente y dejó de ser necesaria para abrir");
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
