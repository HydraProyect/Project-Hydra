using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// D-6 b, R-3a: la visibilidad de una conversación de Comunicaciones es una sola regla
/// (<see cref="VisibilidadConversacion"/>) que usan la lista (<c>ObtenerConversacionesQuery</c>, por
/// <see cref="VisibilidadConversacion.Predicado"/>) y el detalle y los comandos
/// (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>, por <see cref="VisibilidadConversacion.Permite"/>).
/// Estas pruebas fijan que las tres formas deciden igual para cada ancla, y que la rama sin ancla la deciden
/// <see cref="IAlcanceDatosService.TriajeVisibleAsync"/> (la cola de triaje) o
/// <see cref="IAlcanceDatosService.BuzonesPersonalesPropiosAsync"/> (el buzón personal propio, DECISION_LOG 2026-11-14,
/// decisión 2), y no el rol ni la cartera de Clientes. La traducción del
/// predicado a SQL se prueba bajo PostgreSQL en <c>VisibilidadTriageTests</c> (integración, solo en CI).
/// </summary>
public class VisibilidadConversacionTests
{
    private static readonly Guid ClienteDentro = Guid.NewGuid();
    private static readonly Guid ClienteFuera = Guid.NewGuid();
    private static readonly Guid EmpresaDentro = Guid.NewGuid();
    private static readonly Guid EmpresaFuera = Guid.NewGuid();

    // Tres buzones del mismo Tenant: el personal de quien mira, el personal de otro Gestor CAE y uno compartido.
    private static readonly Guid BuzonPersonalPropio = Guid.NewGuid();
    private static readonly Guid BuzonPersonalAjeno = Guid.NewGuid();
    private static readonly Guid BuzonCompartido = Guid.NewGuid();

    public static TheoryData<string> Alcances => new()
    {
        "total", "cartera", "cartera-con-triaje", "sin-cartera", "total-sin-triaje", "propietario", "otro-gestor",
        "administrador",
    };

    private static AlcanceDatosServiceFalso Alcance(string nombre) => nombre switch
    {
        "total" => new AlcanceDatosServiceFalso(),
        "cartera" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro], empresaIdsVisibles: [EmpresaDentro]),
        "cartera-con-triaje" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro],
            empresaIdsVisibles: [EmpresaDentro], triajeVisible: true),
        "sin-cartera" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false),
        // Solo para fijar que la rama sin ancla pregunta a TriajeVisibleAsync y no al acceso total.
        "total-sin-triaje" => new AlcanceDatosServiceFalso(triajeVisible: false),
        // Gestor CAE con cartera acotada, sin el Tenant entero (sin triaje), propietario de BuzonPersonalPropio.
        "propietario" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro],
            empresaIdsVisibles: [EmpresaDentro], conexionesIntegracionAjenas: [BuzonPersonalAjeno],
            buzonesPersonalesPropios: [BuzonPersonalPropio]),
        // Otro Gestor CAE de la misma cartera: para él, BuzonPersonalPropio es un buzón personal ajeno.
        "otro-gestor" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro],
            empresaIdsVisibles: [EmpresaDentro], conexionesIntegracionAjenas: [BuzonPersonalPropio, BuzonPersonalAjeno]),
        // Administrador: acceso total, triaje y ningún buzón personal propio; los dos personales son ajenos.
        "administrador" => new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [BuzonPersonalPropio, BuzonPersonalAjeno]),
        _ => throw new ArgumentOutOfRangeException(nameof(nombre)),
    };

    private static Conversacion PorBuzon(string asunto, Guid buzon)
    {
        var conversacion = new Conversacion(asunto);
        conversacion.AsociarConexion(buzon, $"hilo-{buzon:N}");
        return conversacion;
    }

    private static IEnumerable<Conversacion> Conversaciones() =>
    [
        new("Cliente dentro", clienteId: ClienteDentro),
        new("Cliente fuera", clienteId: ClienteFuera),
        new("Empresa dentro", empresaId: EmpresaDentro),
        new("Empresa fuera", empresaId: EmpresaFuera),
        new("Sin ancla: cola de triaje"),
        PorBuzon("Sin ancla: buzón compartido", BuzonCompartido),
    ];

    /// <summary>
    /// La lista y el detalle deciden igual. El buzón personal ajeno no entra aquí: la lista lo excluye fuera de la
    /// expresión (contra la tabla de conexiones) y lo fija <c>VisibilidadTriageTests</c> (integración); en este doble, el
    /// detalle lo cierra por <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/>, y lo fija
    /// <see cref="Un_hilo_de_un_buzon_personal_ajeno_no_se_abre_ni_con_el_triaje_visible"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Alcances))]
    public async Task La_lista_y_el_detalle_deciden_igual_para_cada_ancla(string nombre)
    {
        var alcance = Alcance(nombre);
        var visibilidad = await VisibilidadConversacion.ResolverAsync(alcance);
        var predicado = visibilidad.Predicado<Conversacion>(c => c.ClienteId, c => c.EmpresaId, c => c.ConexionIntegracionId).Compile();

        var conversaciones = Conversaciones().ToList();
        if (nombre == "propietario")
            conversaciones.Add(PorBuzon("Sin ancla: buzón personal propio", BuzonPersonalPropio));

        foreach (var conversacion in conversaciones)
        {
            var enLaLista = predicado(conversacion);
            var enElDetalle = await alcance.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId);

            enElDetalle.Should().Be(enLaLista, $"«{conversacion.Asunto}» con el alcance «{nombre}»");
            visibilidad.Permite(conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId)
                .Should().Be(enLaLista);
        }
    }

    [Fact]
    public async Task El_propietario_sin_el_Tenant_entero_ve_su_hilo_sin_ancla_en_la_lista_y_en_el_detalle()
    {
        // DECISION_LOG 2026-11-14, decisión 2: el correo de un buzón personal no registrado solo vive en la cuenta de su
        // Gestor CAE propietario, que lo ve siempre, aunque no pase TriajeVisibleAsync.
        var alcance = Alcance("propietario");
        var hilo = PorBuzon("Sin ancla: buzón personal propio", BuzonPersonalPropio);

        (await alcance.TriajeVisibleAsync()).Should().BeFalse("control: sin el Tenant entero no hay triaje");
        var visibilidad = await VisibilidadConversacion.ResolverAsync(alcance);
        visibilidad.Predicado<Conversacion>(c => c.ClienteId, c => c.EmpresaId, c => c.ConexionIntegracionId).Compile()(hilo)
            .Should().BeTrue("la lista");
        (await alcance.ConversacionVisibleAsync(null, null, BuzonPersonalPropio)).Should().BeTrue("el detalle");
    }

    [Fact]
    public async Task Otro_Gestor_CAE_de_la_misma_cartera_no_ve_el_hilo_sin_ancla_del_buzon_personal_ajeno()
    {
        var alcance = Alcance("otro-gestor");
        var hilo = PorBuzon("Sin ancla: buzón personal de otro", BuzonPersonalPropio);

        var visibilidad = await VisibilidadConversacion.ResolverAsync(alcance);
        visibilidad.Predicado<Conversacion>(c => c.ClienteId, c => c.EmpresaId, c => c.ConexionIntegracionId).Compile()(hilo)
            .Should().BeFalse("la lista");
        (await alcance.ConversacionVisibleAsync(null, null, BuzonPersonalPropio)).Should().BeFalse("el detalle");
        (await alcance.ConversacionVisibleAsync(ClienteDentro, null, null)).Should().BeTrue("control: su cartera sí la ve");
    }

    [Fact]
    public async Task El_propietario_no_ve_por_su_buzon_un_buzon_compartido_sin_ancla_ni_el_personal_ajeno()
    {
        // La condición 2 solo abre el buzón personal propio: un compartido sin ancla sigue exigiendo el triaje, y un
        // personal ajeno no se abre por ninguna vía.
        var propietario = Alcance("propietario");

        (await propietario.ConversacionVisibleAsync(null, null, BuzonCompartido)).Should().BeFalse();
        (await propietario.ConversacionVisibleAsync(null, null, BuzonPersonalAjeno)).Should().BeFalse();
        (await propietario.ConversacionVisibleAsync(null, null, null)).Should().BeFalse("sin buzón es la cola de triaje");

        var conTriaje = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro],
            triajeVisible: true, buzonesPersonalesPropios: [BuzonPersonalPropio]);
        (await conTriaje.ConversacionVisibleAsync(null, null, BuzonCompartido)).Should().BeTrue("control: con triaje, sí");
    }

    [Fact]
    public async Task El_buzon_propio_no_abre_un_hilo_anclado_fuera_de_la_cartera()
    {
        // La condición 2 vive solo en la rama sin ancla: no se suma a las anclas.
        var propietario = Alcance("propietario");

        (await propietario.ConversacionVisibleAsync(ClienteFuera, null, BuzonPersonalPropio)).Should().BeFalse();
        (await propietario.ConversacionVisibleAsync(null, EmpresaFuera, BuzonPersonalPropio)).Should().BeFalse();
        (await propietario.ConversacionVisibleAsync(ClienteDentro, null, BuzonPersonalPropio)).Should().BeTrue("control");
    }

    [Fact]
    public async Task El_Administrador_ve_los_hilos_sin_ancla_de_un_buzon_compartido_y_no_los_de_un_buzon_personal_ajeno()
    {
        var administrador = Alcance("administrador");

        (await administrador.ConversacionVisibleAsync(null, null, BuzonCompartido)).Should().BeTrue();
        (await administrador.ConversacionVisibleAsync(null, null, BuzonPersonalPropio)).Should().BeFalse();
        (await administrador.ConversacionVisibleAsync(null, null, BuzonPersonalAjeno)).Should().BeFalse();
    }

    [Theory]
    [InlineData("total", true)]
    [InlineData("cartera-con-triaje", true)]
    [InlineData("cartera", false)]
    [InlineData("sin-cartera", false)]
    [InlineData("total-sin-triaje", false)]
    public async Task La_cola_de_triaje_la_decide_TriajeVisibleAsync(string nombre, bool esperado)
    {
        var alcance = Alcance(nombre);

        (await alcance.ConversacionVisibleAsync(null, null, null)).Should().Be(esperado);
        (await VisibilidadConversacion.ResolverAsync(alcance)).Permite(null, null, null).Should().Be(esperado);
    }

    [Theory]
    [InlineData("cartera")]
    [InlineData("cartera-con-triaje")]
    public async Task Con_una_cartera_acotada_se_ve_lo_de_dentro_y_nada_de_fuera(string nombre)
    {
        var alcance = Alcance(nombre);

        (await alcance.ConversacionVisibleAsync(ClienteDentro, null, null)).Should().BeTrue();
        (await alcance.ConversacionVisibleAsync(ClienteFuera, null, null)).Should().BeFalse();
        (await alcance.ConversacionVisibleAsync(null, EmpresaDentro, null)).Should().BeTrue();
        (await alcance.ConversacionVisibleAsync(null, EmpresaFuera, null)).Should().BeFalse();
    }

    [Fact]
    public async Task Con_las_dos_anclas_se_exigen_las_dos()
    {
        var visibilidad = await VisibilidadConversacion.ResolverAsync(Alcance("cartera-con-triaje"));

        visibilidad.Permite(ClienteDentro, EmpresaDentro, null).Should().BeTrue();
        visibilidad.Permite(ClienteDentro, EmpresaFuera, null).Should().BeFalse();
        visibilidad.Permite(ClienteFuera, EmpresaDentro, null).Should().BeFalse();
    }

    [Fact]
    public async Task Un_hilo_de_un_buzon_personal_ajeno_no_se_abre_ni_con_el_triaje_visible()
    {
        var conexionAjena = Guid.NewGuid();
        var alcance = new AlcanceDatosServiceFalso(triajeVisible: true, conexionesIntegracionAjenas: [conexionAjena]);

        (await alcance.ConversacionVisibleAsync(null, null, conexionAjena)).Should().BeFalse();
        (await alcance.ConversacionVisibleAsync(ClienteDentro, null, conexionAjena)).Should().BeFalse();
        (await alcance.ConversacionVisibleAsync(null, null, Guid.NewGuid())).Should().BeTrue("control: un buzón visible");
    }

    [Fact]
    public async Task El_detalle_no_pregunta_por_el_triaje_si_el_hilo_tiene_ancla()
    {
        var alcance = new AlcanceQueCuentaTriaje();

        await alcance.ConversacionVisibleAsync(ClienteDentro, null, null);
        await alcance.ConversacionVisibleAsync(null, EmpresaDentro, null);

        alcance.PreguntasPorTriaje.Should().Be(0);
        await alcance.ConversacionVisibleAsync(null, null, null);
        alcance.PreguntasPorTriaje.Should().Be(1);
    }

    /// <summary>Reimplementa la interfaz para que la llamada por <see cref="IAlcanceDatosService"/> llegue al contador.</summary>
    private sealed class AlcanceQueCuentaTriaje : AlcanceDatosServiceFalso, IAlcanceDatosService
    {
        public int PreguntasPorTriaje { get; private set; }

        public AlcanceQueCuentaTriaje() : base(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro], empresaIdsVisibles: [EmpresaDentro])
        {
        }

        public new Task<bool> TriajeVisibleAsync(CancellationToken cancellationToken = default)
        {
            PreguntasPorTriaje++;
            return Task.FromResult(false);
        }
    }
}
