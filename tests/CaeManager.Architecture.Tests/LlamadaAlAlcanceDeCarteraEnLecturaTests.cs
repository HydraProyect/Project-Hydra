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
/// </para>
///
/// <para>
/// <b>La lista.</b> <c>Congelados/Queries-sin-comprobacion-de-cartera.txt</c>, una línea por handler que no
/// llama (<c>Característica :: Handler = 1</c>). Una Query nueva que no llama sale <c>NUEVA</c>; una que
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
/// guarda. (4) Nada de RLS ni del aislamiento entre Tenants: Propiedad ≠ Operación ≠ Plataforma; esto mira solo
/// el plano de Operación dentro de un Tenant. (5) Que una Query que hoy no llama sea segura por su llamador: si
/// un componente nuevo le pasa un Id sin filtrar, esta red no lo ve. (6) Handlers fuera del ensamblado de
/// Application: solo se buscan ahí.
/// </para>
/// </summary>
public class LlamadaAlAlcanceDeCarteraEnLecturaTests
{
    private const string NombreDeLista = "Queries-sin-comprobacion-de-cartera";

    private static readonly Lazy<Medida> MedidaActual = new(Medir);

    [Fact]
    public void Toda_query_que_no_llama_a_la_comprobacion_de_cartera_esta_en_la_lista_congelada()
    {
        var medido = MedidaActual.Value.NoLlaman.ToDictionary(clave => clave, _ => 1);

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

    private sealed record Medida(
        int Total, int Llaman, IReadOnlyList<Ubicacion> NoLlaman, IReadOnlySet<string> CaracteristicasQueLlaman,
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
        var noLlaman = new List<Ubicacion>();
        var caracteristicasQueLlaman = new HashSet<string>(StringComparer.Ordinal);

        foreach (var grupo in handlers.GroupBy(h => h.Handler))
        {
            var varios = grupo.Count() > 1;
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

                noLlaman.Add(new Ubicacion(caracteristica, handler.Name + (varios ? $"[{peticion.Name}]" : string.Empty)));
            }
        }

        noLlaman.Should().OnlyHaveUniqueItems("la clave Característica :: Handler[Petición] tiene que ser única");
        return new Medida(total, llaman, noLlaman, caracteristicasQueLlaman, criterio.Select(ClaveMetodo.De).ToList(), alcanzan);
    }
}
