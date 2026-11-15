using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Red de lectura de la Asignación de Cartera: toda Query nueva llama a la comprobación de alcance o
/// entra, a la vista del diff, en una lista congelada que solo encoge.</b>
///
/// <para>
/// <b>Por qué existe.</b> La escritura ya tiene su red (<see cref="LlamadaAlAlcanceDeCarteraEnEscrituraTests"/>,
/// con <see cref="AlcanceDeCarteraEnEscrituraTests"/> y <see cref="EscriturasSobreEntidadesConCarteraTests"/>
/// en el constructor); la lectura no tenía ninguna fuera de los buzones
/// (<see cref="AlcanceDeConversacionesYBuzonesTests"/>) y de la lectura Inbound
/// (<see cref="ConsumidoresDelLectorInboundAcotadosTests"/>), y la revisión de las PR encontraba a mano
/// Queries a las que les faltaba el filtro de cartera. Esto convierte ese olvido en un rojo de CI.
/// </para>
///
/// <para>
/// <b>Población.</b> Todos los handlers de petición de Application (<see cref="IRequestHandler{TRequest,TResponse}"/>,
/// <see cref="IRequestHandler{TRequest}"/> y <see cref="IStreamRequestHandler{TRequest,TResponse}"/>) cuya petición
/// no es un comando de escritura (<see cref="ICommandBase"/>), en cualquier carpeta: a diferencia de la red de
/// escritura, no se deriva de qué carpetas inyectan el alcance, para que una Query nueva en una carpeta sin
/// cartera hoy (Auditoría, Tenants…) que empiece a leer Trabajadores o Documentos también se vea. Una clase que
/// atiende varias peticiones se mide por cada <c>Handle</c>. Si Application empieza a implementar otra
/// interfaz de MediatR, <see cref="Application_no_implementa_interfaces_de_MediatR_que_la_medida_no_mira"/>
/// se pone en rojo para que se decida si entra en la población.
/// </para>
///
/// <para>
/// <b>Cómo se mide «llama».</b> Igual que la red de escritura y con el mismo criterio
/// (<see cref="LlamadaAlAlcanceDeCarteraEnEscrituraTests.MetodosQueComprueban"/>): el <c>Handle</c> alcanza
/// en el IL compilado (<see cref="GrafoDeLlamadasIl"/>), directa o transitivamente, una lista de Ids visibles
/// de <see cref="IAlcanceDatosService"/> o una de sus extensiones por entidad
/// (<c>ClienteVisibleAsync</c>, <c>ClienteOpcionalVisibleAsync</c>, <c>CentroVisibleAsync</c>…).
/// «Llama» es <b>alcanzable</b>, no «se ejecuta»: el grafo cuenta como arista la creación de un delegado
/// (<c>ldftn</c>/<c>ldvirtftn</c>) y el <c>ldtoken</c> de un método, así que un <c>Handle</c> que construye un
/// delegado o una lambda con la comprobación dentro, y no la invoca nunca (o solo en una rama que no corre),
/// figura como «llama» y no entra en la lista. Es un falso «llama» conocido y aceptado: el grafo no distingue
/// «crea el delegado» de «lo invoca» (hacerlo exigiría seguir el flujo de datos del delegado), y la escritura
/// comparte el grafo y el criterio. La lambda dentro de un <c>Handle</c> que sí la invoca (transacción con
/// callback, <c>Where</c> con predicado) es el caso habitual, y por eso la arista se mantiene.
/// </para>
///
/// <para>
/// <b>La lista.</b> <c>Congelados/Queries-sin-comprobacion-de-cartera.txt</c>, una línea por <b>petición</b>
/// cuyo handler no llama (<c>Namespace del handler :: Handler[Petición] = 1</c>, el namespace relativo a
/// <c>CaeManager.Application</c> y la petición con su nombre completo relativo a él; un nombre que no cuelga de
/// Application lleva el prefijo <c>global::</c> y su nombre completo, para no confundirse con uno relativo; la
/// petición figura siempre, también si la clase atiende una sola, y con nombres inequívocos, para que cambiar la Query por otra homónima de
/// otro namespace, o sin cambiar el nombre del handler, se vea). Una Query nueva que no llama sale <c>NUEVA</c>; una que
/// pierde la llamada, también; una listada que empieza a llamar, o que se borra, sale <c>OBSOLETA</c> y
/// obliga a quitar la línea. Estar en la lista <b>no</b> es un aval: es una medida, no una clasificación
/// (los motivos típicos de una entrada son datos del propio usuario, catálogo del Tenant, plano de Plataforma
/// o de Operador CAE, agregado del Tenant entero tras <c>AlcanzaElTenantEnteroAsync</c>, o delegación por el
/// mediador en otra Query que sí comprueba); la clasificación de cada una vive en <c>Project-Hydra-Negocio</c>.
/// Que una línea nueva lleve su motivo y que la lista solo encoja es convención de revisión: el test lo hace
/// visible en el diff, no lo impone.
/// </para>
///
/// <para>
/// <b>Lo que esto NO demuestra.</b> (1) Que la lista pedida se cruce con las filas que se devuelven, ni en
/// todas las ramas: basta una llamada alcanzable. (2) Nada de lo que la Query envía por <see cref="IMediator"/>:
/// el IL no dice qué handler lo recibe, así que una Query que solo compone otras figura como «no llama» y va a
/// la lista. (3) Que la lista de roles que promete un comentario (<c>RolesQueLeen…</c>) se aplique: eso es otra
/// guarda. (3 bis) Que el delegado con la comprobación se invoque: ver «Cómo se mide». (4) Nada de RLS ni del aislamiento entre Tenants: Propiedad ≠ Operación ≠ Plataforma; esto mira solo
/// el plano de Operación dentro de un Tenant. (5) Que una Query que hoy no llama sea segura por su llamador: si
/// un componente nuevo le pasa un Id sin filtrar, esta red no lo ve. (6) Handlers fuera del ensamblado de
/// Application: solo se buscan ahí. (7) <b>Hueco conocido: despacho virtual a una sobrescritura.</b> El grafo solo
/// considera las implementaciones alternativas de un método <i>sin cuerpo</i> (interfaz o abstracto: «llama» si
/// todas llaman). Un <c>callvirt</c> a un método base <i>con</i> cuerpo que sí comprueba se cuenta como «llama»
/// por la referencia al método base, aunque en tiempo de ejecución se despache a una sobrescritura que no
/// comprueba; el grafo no mira las sobrescrituras de un método con cuerpo. No se ha buscado si hoy hay algún
/// caso: el grafo no expone qué <c>callvirt</c> apuntan a un método con cuerpo sobrescrito. Si lo hay, la Query
/// figura como «llama» y no entra en la lista. Cerrarlo exigiría cambiar
/// <see cref="GrafoDeLlamadasIl"/>, que comparte la red de escritura, y no se ha hecho en este incremento.
/// </para>
/// </summary>
public class LlamadaAlAlcanceDeCarteraEnLecturaTests
{
    private const string NombreDeLista = "Queries-sin-comprobacion-de-cartera";

    private static readonly Lazy<Medida> MedidaActual = new(Medir);

    [Fact]
    public void Toda_query_que_no_llama_a_la_comprobacion_de_cartera_esta_en_la_lista_congelada()
    {
        var medido = MedidaActual.Value.NoLlaman.ToDictionary(q => q.Clave, _ => 1);

        var fallo = ListaCongelada.Verificar(
            NombreDeLista,
            medido,
            "NUEVA: la Query no alcanza ninguna comprobación de cartera de IAlcanceDatosService. Si lee filas con " +
            "cartera (Cliente empresarial, Centro, Empresa, Trabajador, Vehículo, Documento, Visita, Reclamación…), " +
            "filtra con la lista de Ids visibles o comprueba el Id recibido (ClienteVisibleAsync, " +
            "ClienteOpcionalVisibleAsync, CentroVisibleAsync…) y añade su test de Application con un usuario fuera de " +
            "cartera. Si de verdad no aplica, añade la línea con un comentario '#' encima que diga qué lee y quién " +
            "decide en su lugar. OBSOLETA: la Query ya llama o ya no existe; borra la línea para que la lista encoja.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void La_medida_ve_las_queries_y_distingue_las_que_llaman()
    {
        var medida = MedidaActual.Value;

        // Control positivo de la población y del grafo: si la reflexión dejara de ver los handlers, la lista
        // saldría entera OBSOLETA; si el grafo dejara de ver las llamadas, saldrían NUEVAS las que llaman. Esto
        // lo dice con un mensaje que apunta al instrumento y no a la lista.
        medida.Total.Should().BeGreaterThan(200, "hoy hay unas 290 Queries en Application; muy pocas es que la medida ya no las ve");
        medida.Llaman.Should().BeGreaterThan(100, "hoy unas 180 Queries alcanzan la comprobación; muy pocas es que el grafo perdió aristas");

        medida.CaracteristicasQueLlaman.Should().Contain(
            new[] { "Centros", "Clientes", "Documentos", "Empresas", "Reclamaciones", "Trabajadores", "Vehiculos" },
            "son las carpetas cuyo agregado tiene cartera definida en IAlcanceDatosService; alguna de sus Queries la comprueba");
    }

    [Fact]
    public void La_clave_de_cada_entrada_nombra_sin_ambiguedad_al_handler_y_a_la_peticion()
    {
        // Sin la petición en la clave, o con nombres simples, cambiar una Query congelada por otra sin comprobación
        // que conserve el nombre pasaría inadvertido (ni NUEVA ni OBSOLETA). El control usa la MISMA función que
        // construye la clave (Clave, sobre el mismo handler y la misma petición) y además deshace la clave con
        // CompletoDesdeClave y la compara con los nombres reales: ver DefectosDeLaClave.
        MedidaActual.Value.NoLlaman.Should().NotBeEmpty();

        var defectos = MedidaActual.Value.NoLlaman
            .SelectMany(q => DefectosDeLaClave(q.Clave, q.Handler, q.Peticion).Select(d => $"{q.Clave}: {d}"));

        string.Join("\n", defectos).Should().BeEmpty();
    }

    [Fact]
    public void Un_nombre_externo_a_Application_no_se_confunde_con_uno_relativo_en_la_clave()
    {
        // Control sintético: CaeManager.Application.ClaveSintetica.PeticionSintetica y ClaveSintetica.PeticionSintetica
        // daban la misma clave (ambas «ClaveSintetica.PeticionSintetica»). Sin el prefijo global:: de los externos,
        // sustituir una por la otra en el mismo handler conservaría la entrada congelada.
        var dentro = typeof(CaeManager.Application.ClaveSintetica.PeticionSintetica);
        var fuera = typeof(global::ClaveSintetica.PeticionSintetica);
        var handlerDentro = typeof(CaeManager.Application.ClaveSintetica.HandlerSintetico);
        var handlerFuera = typeof(global::ClaveSintetica.HandlerSintetico);

        // Mismo handler (el de Application), dos peticiones homónimas: las claves tienen que diferir.
        Clave(handlerDentro, dentro).Should().NotBe(Clave(handlerDentro, fuera));
        Clave(handlerDentro, fuera).Simbolo.Should().Contain("[global::");
        // Y lo mismo a la inversa: mismo nombre de handler en el namespace de Application y fuera de él.
        Clave(handlerDentro, dentro).Should().NotBe(Clave(handlerFuera, dentro));

        // Cada clave se deshace en el nombre real de su petición y de su handler.
        DefectosDeLaClave(Clave(handlerDentro, dentro), handlerDentro, dentro).Should().BeEmpty();
        DefectosDeLaClave(Clave(handlerDentro, fuera), handlerDentro, fuera).Should().BeEmpty();
        DefectosDeLaClave(Clave(handlerFuera, dentro), handlerFuera, dentro).Should().BeEmpty();
        DefectosDeLaClave(Clave(handlerFuera, fuera), handlerFuera, fuera).Should().BeEmpty();

        // Sensibilidad del control: la clave de una petición no vale para su homónima.
        DefectosDeLaClave(Clave(handlerDentro, dentro), handlerDentro, fuera).Should().NotBeEmpty();
        DefectosDeLaClave(Clave(handlerDentro, fuera), handlerDentro, dentro).Should().NotBeEmpty();
        DefectosDeLaClave(Clave(handlerFuera, dentro), handlerDentro, dentro).Should().NotBeEmpty();
    }

    [Fact]
    public void El_control_de_la_clave_acepta_un_handler_en_otro_namespace_y_una_peticion_generica_abierta()
    {
        // Hallazgo 2 (r3): el control anteponía CaeManager.Application a todo namespace, aunque el handler no
        // estuviera en Application, y para una petición genérica abierta FullName es null (la clave usaba Name y el
        // control comparaba contra null). Un handler compilado en Application con otro namespace tiene que dar una
        // clave que el control acepte, y una petición con FullName null tiene que entrar con su nombre completo.
        var handlerFuera = typeof(global::ClaveSintetica.HandlerSintetico);
        var peticionDentro = typeof(CaeManager.Application.ClaveSintetica.PeticionSintetica);
        DefectosDeLaClave(Clave(handlerFuera, peticionDentro), handlerFuera, peticionDentro).Should().BeEmpty();

        var handlerGenerico = typeof(global::ClaveSintetica.HandlerGenerico<>);
        var peticionAbierta = handlerGenerico.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
            .GetGenericArguments()[0];
        peticionAbierta.FullName.Should().BeNull("el caso que cubre este control es el de una petición genérica abierta, cuyo FullName es null");

        var clave = Clave(handlerGenerico, peticionAbierta);
        clave.Simbolo.Should().EndWith("[global::ClaveSintetica.PeticionGenerica`1]",
            "con FullName null la clave usa el nombre completo reconstruido, no el Name suelto");
        DefectosDeLaClave(clave, handlerGenerico, peticionAbierta).Should().BeEmpty();

        // Una petición homónima de otro namespace no puede dar la misma clave que la genérica abierta.
        var otraGenerica = typeof(CaeManager.Application.ClaveSintetica.PeticionGenerica<>);
        Clave(handlerGenerico, otraGenerica).Should().NotBe(clave);
    }

    [Fact]
    public void El_criterio_de_llamar_es_el_de_la_red_de_escritura()
    {
        var compartido = LlamadaAlAlcanceDeCarteraEnEscrituraTests.MetodosQueComprueban();

        // La medida guarda el criterio con el que midió: si Medir dejara de usar el compartido (un conjunto más
        // estrecho daría NUEVAS falsas que alguien taparía añadiendo líneas), esto lo dice.
        MedidaActual.Value.Criterio.Should().BeEquivalentTo(compartido.Select(ClaveMetodo.De),
            "la red de lectura mide «llama» con el mismo criterio que la red de escritura");

        // Y que ese criterio es el que se pasó al grafo: QueAlcanzan parte de sus objetivos, así que todos están en
        // el conjunto medido. Si alguien estrechara solo el argumento de QueAlcanzan, faltarían aquí.
        compartido.Select(ClaveMetodo.De).Should().BeSubsetOf(MedidaActual.Value.Alcanzan,
            "la medida tiene que haber recorrido el grafo desde todos los métodos del criterio compartido");

        var nombres = compartido.Select(m => m.Name).ToHashSet();
        nombres.Should().Contain(nameof(IAlcanceDatosService.ObtenerClienteIdsVisiblesAsync))
            .And.Contain(nameof(IAlcanceDatosService.ObtenerTrabajadorIdsVisiblesAsync));
        nombres.Should().NotContain(
            new[] { nameof(IAlcanceDatosService.TieneAccesoTotalAsync), nameof(IAlcanceDatosService.AlcanzaElTenantEnteroAsync) },
            "decir que el rol lo ve todo no decide sobre ninguna fila: una Query que solo pregunta eso no comprueba la cartera");
    }

    /// <summary>Interfaces de handler de petición que la medida recorre.</summary>
    private static readonly Type[] InterfacesDePeticion =
        [typeof(IRequestHandler<,>), typeof(IRequestHandler<>), typeof(IStreamRequestHandler<,>)];

    /// <summary>
    /// Interfaces genéricas de MediatR que Application puede implementar sin ser un handler de petición:
    /// eventos y tuberías, que no responden a una Query, y los contratos de la propia petición.
    /// </summary>
    private static readonly Type[] InterfacesDeMediatRFueraDeLaPoblacion =
    [
        typeof(IRequest<>), typeof(IStreamRequest<>),
        typeof(INotificationHandler<>), typeof(IPipelineBehavior<,>), typeof(IStreamPipelineBehavior<,>),
        typeof(MediatR.Pipeline.IRequestPreProcessor<>), typeof(MediatR.Pipeline.IRequestPostProcessor<,>),
        typeof(MediatR.Pipeline.IRequestExceptionHandler<,,>), typeof(MediatR.Pipeline.IRequestExceptionAction<,>),
    ];

    [Fact]
    public void Application_no_implementa_interfaces_de_MediatR_que_la_medida_no_mira()
    {
        var conocidas = InterfacesDePeticion.Concat(InterfacesDeMediatRFueraDeLaPoblacion).ToHashSet();

        var desconocidas = ReflexionArquitecturaHelper.TiposDe(typeof(ICommand).Assembly)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.Namespace?.StartsWith("MediatR", StringComparison.Ordinal) == true)
                .Where(i => !conocidas.Contains(i.GetGenericTypeDefinition()))
                .Select(i => $"{t.FullName} : {i.GetGenericTypeDefinition().Name}"))
            .Order(StringComparer.Ordinal);

        string.Join("\n", desconocidas).Should().BeEmpty(
            "un handler de una interfaz de MediatR que la medida no recorre quedaría fuera de la población sin que " +
            "nada lo dijera: añádela a InterfacesDePeticion si atiende peticiones, o a las de fuera si no");
    }

    /// <summary>Una Query cuyo handler no alcanza la comprobación, con su clave y los tipos que la componen.</summary>
    private sealed record QueQueNoLlama(Ubicacion Clave, Type Handler, Type Peticion);

    private sealed record Medida(
        int Total, int Llaman, IReadOnlyList<QueQueNoLlama> NoLlaman, IReadOnlySet<string> CaracteristicasQueLlaman,
        IReadOnlyCollection<ClaveMetodo> Criterio, IReadOnlySet<ClaveMetodo> Alcanzan);

    private static Medida Medir()
    {
        var application = typeof(ICommand).Assembly;
        var grafo = new GrafoDeLlamadasIl(
        [
            application,
            typeof(CaeManager.Infrastructure.Persistence.CaeManagerDbContext).Assembly,
            ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web"),
        ]);
        var criterio = LlamadaAlAlcanceDeCarteraEnEscrituraTests.MetodosQueComprueban();
        var alcanzan = grafo.QueAlcanzan(criterio);

        var handlers = ReflexionArquitecturaHelper.TiposDe(application)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && InterfacesDePeticion.Contains(i.GetGenericTypeDefinition()))
                .Select(i => (Handler: t, Interfaz: i, Peticion: i.GetGenericArguments()[0])))
            .ToList();

        var total = 0;
        var llaman = 0;
        var noLlaman = new List<QueQueNoLlama>();
        var caracteristicasQueLlaman = new HashSet<string>(StringComparer.Ordinal);

        foreach (var grupo in handlers.GroupBy(h => h.Handler))
        {
            foreach (var (handler, interfaz, peticion) in grupo)
            {
                if (typeof(ICommandBase).IsAssignableFrom(peticion))
                    continue;

                total++;
                var handle = (MethodInfo)handler.GetInterfaceMap(interfaz).TargetMethods
                    .Single(m => m.Name.EndsWith("Handle", StringComparison.Ordinal));
                var caracteristica = LlamadaAlAlcanceDeCarteraEnEscrituraTests.Caracteristica(handler);

                if (alcanzan.Contains(ClaveMetodo.De(handle)))
                {
                    llaman++;
                    caracteristicasQueLlaman.Add(caracteristica);
                    continue;
                }

                // La clave nombra SIEMPRE la petición, también cuando la clase atiende una sola, y con nombres
                // inequívocos (namespace del handler y nombre completo de la petición, relativos a Application): si no,
                // sustituir una Query congelada por otra sin comprobación que conserve el nombre (en otro namespace) no
                // saldría ni NUEVA ni OBSOLETA, y la lista aprobaría una Query distinta de la que se congeló.
                noLlaman.Add(new QueQueNoLlama(Clave(handler, peticion), handler, peticion));
            }
        }

        noLlaman.Select(q => q.Clave).Should().OnlyHaveUniqueItems(
            "la clave Namespace del handler :: Handler[Petición] tiene que ser única");
        return new Medida(total, llaman, noLlaman, caracteristicasQueLlaman, criterio.Select(ClaveMetodo.De).ToList(), alcanzan);
    }

    private const string RaizDeApplication = "CaeManager.Application";

    private const string PrefijoDeNombreExterno = "global::";

    /// <summary>
    /// Clave inequívoca de una Query congelada: <c>namespace del handler :: Handler[Petición]</c>, con cada nombre
    /// por <see cref="NombreEnClave"/>. Con nombres simples, cambiar una Query congelada por otra homónima de otro
    /// namespace (mismo handler, misma petición, otra carpeta) no salía NUEVA ni OBSOLETA; con el namespace del
    /// handler y el nombre completo de la petición, sí. El tipo anidado conserva su <c>+</c>.
    /// </summary>
    private static Ubicacion Clave(Type handler, Type peticion) =>
        new(NombreEnClave(handler.Namespace ?? string.Empty),
            $"{NombreSinNamespace(handler)}[{NombreEnClave(NombreCompleto(peticion))}]");

    /// <summary>
    /// Nombre completo relativo a Application si cuelga de ella (<c>CaeManager.Application.Foo.Query</c> →
    /// <c>Foo.Query</c>); si no, con el prefijo <c>global::</c> y el nombre completo (<c>Foo.Query</c> →
    /// <c>global::Foo.Query</c>). Sin el prefijo, un tipo externo <c>Foo.Query</c> y uno de Application
    /// <c>CaeManager.Application.Foo.Query</c> daban la misma clave y la sustitución de uno por otro pasaba inadvertida.
    /// Es inyectiva: un nombre relativo nunca empieza por <c>global::</c> (no es un identificador). Su inversa es
    /// <see cref="CompletoDesdeClave"/>.
    /// </summary>
    private static string NombreEnClave(string nombreCompleto) =>
        nombreCompleto.StartsWith(RaizDeApplication + ".", StringComparison.Ordinal)
            ? nombreCompleto[(RaizDeApplication.Length + 1)..]
            : PrefijoDeNombreExterno + nombreCompleto;

    private static string CompletoDesdeClave(string nombreEnClave) =>
        nombreEnClave.StartsWith(PrefijoDeNombreExterno, StringComparison.Ordinal)
            ? nombreEnClave[PrefijoDeNombreExterno.Length..]
            : $"{RaizDeApplication}.{nombreEnClave}";

    /// <summary>
    /// Nombre completo de un tipo, también cuando <see cref="Type.FullName"/> es <c>null</c> (genérico construido sobre
    /// parámetros sin resolver, como la petición de un handler genérico abierto): se reconstruye con el namespace, el
    /// tipo que lo contiene (separado por <c>+</c>) y el nombre.
    /// </summary>
    private static string NombreCompleto(Type tipo)
    {
        if (tipo.FullName is { } completo)
            return completo;
        if (tipo.IsGenericParameter)
            return tipo.Name;
        if (tipo.DeclaringType is { } contenedor)
            return $"{NombreCompleto(contenedor)}+{tipo.Name}";
        return tipo.Namespace is { Length: > 0 } ns ? $"{ns}.{tipo.Name}" : tipo.Name;
    }

    private static string NombreSinNamespace(Type tipo)
    {
        var completo = NombreCompleto(tipo);
        return tipo.Namespace is { Length: > 0 } ns && completo.StartsWith(ns + ".", StringComparison.Ordinal)
            ? completo[(ns.Length + 1)..]
            : completo;
    }

    /// <summary>
    /// El control de la clave: es la que construye <see cref="Clave"/> para ese handler y esa petición (la misma
    /// función sobre los mismos datos) y, deshecha con <see cref="CompletoDesdeClave"/>, da los nombres reales de
    /// ambos. Devuelve los defectos; vacío si la clave es inequívoca y correcta.
    /// </summary>
    private static List<string> DefectosDeLaClave(Ubicacion clave, Type handler, Type peticion)
    {
        var defectos = new List<string>();

        var esperada = Clave(handler, peticion);
        if (clave != esperada)
            defectos.Add($"la clave no es la que construye Clave para ese handler y esa petición ({esperada})");

        var apertura = clave.Simbolo.IndexOf('[');
        if (apertura < 0 || !clave.Simbolo.EndsWith(']'))
            return [.. defectos, "el símbolo no tiene la forma Handler[Petición]"];

        var nombreHandler = clave.Simbolo[..apertura];
        var peticionEnClave = clave.Simbolo[(apertura + 1)..^1];

        var espacio = CompletoDesdeClave(clave.Lugar);
        var handlerCompleto = espacio.Length > 0 ? $"{espacio}.{nombreHandler}" : nombreHandler;
        if (handlerCompleto != NombreCompleto(handler))
            defectos.Add($"la clave identifica al handler como «{handlerCompleto}» y es «{NombreCompleto(handler)}»");

        var peticionCompleta = CompletoDesdeClave(peticionEnClave);
        if (peticionCompleta != NombreCompleto(peticion))
            defectos.Add($"la clave identifica la petición como «{peticionCompleta}» y es «{NombreCompleto(peticion)}»");

        return defectos;
    }
}
