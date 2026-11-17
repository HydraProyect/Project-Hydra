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
/// Estas pruebas fijan que las tres formas deciden igual para cada ancla, y que la rama sin ancla (la cola de triaje) la
/// decide <see cref="IAlcanceDatosService.TriajeVisibleAsync"/> y no el rol ni la cartera de Clientes. La traducción del
/// predicado a SQL se prueba bajo PostgreSQL en <c>VisibilidadTriageTests</c> (integración, solo en CI).
/// </summary>
public class VisibilidadConversacionTests
{
    private static readonly Guid ClienteDentro = Guid.NewGuid();
    private static readonly Guid ClienteFuera = Guid.NewGuid();
    private static readonly Guid EmpresaDentro = Guid.NewGuid();
    private static readonly Guid EmpresaFuera = Guid.NewGuid();

    public static TheoryData<string> Alcances => new() { "total", "cartera", "cartera-con-triaje", "sin-cartera", "total-sin-triaje" };

    private static AlcanceDatosServiceFalso Alcance(string nombre) => nombre switch
    {
        "total" => new AlcanceDatosServiceFalso(),
        "cartera" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro], empresaIdsVisibles: [EmpresaDentro]),
        "cartera-con-triaje" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [ClienteDentro],
            empresaIdsVisibles: [EmpresaDentro], triajeVisible: true),
        "sin-cartera" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false),
        // Solo para fijar que la rama sin ancla pregunta a TriajeVisibleAsync y no al acceso total.
        "total-sin-triaje" => new AlcanceDatosServiceFalso(triajeVisible: false),
        _ => throw new ArgumentOutOfRangeException(nameof(nombre)),
    };

    private static IEnumerable<Conversacion> Conversaciones() =>
    [
        new("Cliente dentro", clienteId: ClienteDentro),
        new("Cliente fuera", clienteId: ClienteFuera),
        new("Empresa dentro", empresaId: EmpresaDentro),
        new("Empresa fuera", empresaId: EmpresaFuera),
        new("Sin ancla: cola de triaje"),
    ];

    [Theory]
    [MemberData(nameof(Alcances))]
    public async Task La_lista_y_el_detalle_deciden_igual_para_cada_ancla(string nombre)
    {
        var alcance = Alcance(nombre);
        var visibilidad = await VisibilidadConversacion.ResolverAsync(alcance);
        var predicado = visibilidad.Predicado<Conversacion>(c => c.ClienteId, c => c.EmpresaId).Compile();

        foreach (var conversacion in Conversaciones())
        {
            var enLaLista = predicado(conversacion);
            var enElDetalle = await alcance.ConversacionVisibleAsync(conversacion.ClienteId, conversacion.EmpresaId, null);

            enElDetalle.Should().Be(enLaLista, $"«{conversacion.Asunto}» con el alcance «{nombre}»");
            visibilidad.Permite(conversacion.ClienteId, conversacion.EmpresaId).Should().Be(enLaLista);
        }
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
        (await VisibilidadConversacion.ResolverAsync(alcance)).Permite(null, null).Should().Be(esperado);
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

        visibilidad.Permite(ClienteDentro, EmpresaDentro).Should().BeTrue();
        visibilidad.Permite(ClienteDentro, EmpresaFuera).Should().BeFalse();
        visibilidad.Permite(ClienteFuera, EmpresaDentro).Should().BeFalse();
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
