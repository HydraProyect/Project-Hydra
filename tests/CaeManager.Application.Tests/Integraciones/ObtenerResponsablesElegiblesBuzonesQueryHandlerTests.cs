using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerResponsablesElegiblesBuzones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// La consulta que alimenta el selector de Responsable de Buzones. La regla de fondo (<see cref="ResponsableBuzonElegible"/>) se
/// prueba aparte; aquí se fija que la consulta la aplica cuenta a cuenta y buzón a buzón, con el alcance del comando
/// <c>FijarResponsableBuzonCommand</c>: solo Administrador, solo buzones compartidos de Microsoft 365, visibles y con su
/// Cliente empresarial dentro del alcance de quien consulta.
/// </summary>
public class ObtenerResponsablesElegiblesBuzonesQueryHandlerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();

    private sealed class DirectorioPorUsuario(IReadOnlyDictionary<Guid, DestinoCartera> destinos) : IDirectorioDestinosCartera
    {
        public int Lecturas { get; private set; }

        public Task<DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
        {
            Lecturas++;
            return Task.FromResult(destinos.TryGetValue(usuarioId, out var destino) ? destino : null);
        }
    }

    private DirectorioPorUsuario? _directorio;

    private sealed class EquipoVacio : IDirectorioEquipoCoordinador
    {
        public Task<IReadOnlyList<MiembroDeEquipo>> ObtenerEquipoAsync(
            Guid coordinadorUsuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MiembroDeEquipo>>([]);
    }

    private static DestinoCartera Cuenta(string? rol, bool activa = true) => new(activa, rol, null, false, Tenant, null);

    private ObtenerResponsablesElegiblesBuzonesQueryHandler CrearHandler(
        IReadOnlyDictionary<Guid, DestinoCartera> cuentas, string? rolActual = "Administrador", bool buzonVisible = true,
        IAlcanceDatosService? alcance = null) =>
        new(_conexiones,
            alcance ?? new AlcanceDatosServiceFalso(conexionIntegracionVisible: buzonVisible),
            new ResponsableBuzonElegible(
                _directorio = new DirectorioPorUsuario(cuentas), new EquipoVacio(), new OperacionesEnMemoria([], []),
                DelegacionesEnMemoria.VigentesDe(new OperacionesEnMemoria([], [])),
                new EmpresasQueryContextFalso(), new TenantActualFijo(Tenant)),
            new CurrentUserServiceFalso(Guid.NewGuid(), rolActual));

    private ConexionIntegracion Buzon(Guid? empresa = null, Guid? propietario = null,
        ProveedorIntegracion proveedor = ProveedorIntegracion.Microsoft365)
    {
        var conexion = new ConexionIntegracion(
            "cae@ejemplo.test", "Buzón CAE", empresa, proveedor, propietario);
        _conexiones.Agregar(conexion);
        return conexion;
    }

    [Fact]
    public async Task En_un_buzon_de_triage_compartido_decide_solo_el_rol_y_entran_los_cuatro_roles_de_gestion()
    {
        var admin = Guid.NewGuid();
        var direccion = Guid.NewGuid();
        var coordinador = Guid.NewGuid();
        var gestor = Guid.NewGuid();
        var consulta = Guid.NewGuid();
        var portal = Guid.NewGuid();
        var desactivada = Guid.NewGuid();
        var desconocida = Guid.NewGuid();
        var cuentas = new Dictionary<Guid, DestinoCartera>
        {
            [admin] = Cuenta("Administrador"),
            [direccion] = Cuenta("DireccionCae"),
            [coordinador] = Cuenta("CoordinadorCae"),
            [gestor] = Cuenta("GestorCae"),
            [consulta] = Cuenta("Consulta"),
            [portal] = Cuenta("Cliente"),
            [desactivada] = Cuenta("GestorCae", activa: false)
        };
        var buzon = Buzon();

        var resultado = await CrearHandler(cuentas).Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery(
                [buzon.Id], [admin, direccion, coordinador, gestor, consulta, portal, desactivada, desconocida]),
            default);

        resultado[buzon.Id].Should().BeEquivalentTo(
            new[] { admin, direccion, coordinador, gestor },
            "Administrador, Dirección CAE, Coordinador CAE y Gestor CAE, y no Consulta, portal, desactivada ni ajena");
    }

    [Fact]
    public async Task Con_Cliente_empresarial_sin_cartera_solo_entran_Administrador_y_Direccion_CAE()
    {
        var admin = Guid.NewGuid();
        var direccion = Guid.NewGuid();
        var coordinador = Guid.NewGuid();
        var gestor = Guid.NewGuid();
        var cuentas = new Dictionary<Guid, DestinoCartera>
        {
            [admin] = Cuenta("Administrador"),
            [direccion] = Cuenta("DireccionCae"),
            [coordinador] = Cuenta("CoordinadorCae"),
            [gestor] = Cuenta("GestorCae")
        };
        var buzon = Buzon(empresa: Guid.NewGuid());

        var resultado = await CrearHandler(cuentas).Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery([buzon.Id], [admin, direccion, coordinador, gestor]), default);

        resultado[buzon.Id].Should().BeEquivalentTo(
            new[] { admin, direccion }, "Coordinador y Gestor CAE sin Asignación de Cartera sobre la Empresa los rechazaría el comando");
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Quien_no_es_Administrador_no_obtiene_elegibles(string? rol)
    {
        var admin = Guid.NewGuid();
        var buzon = Buzon();

        var resultado = await CrearHandler(
                new Dictionary<Guid, DestinoCartera> { [admin] = Cuenta("Administrador") }, rolActual: rol)
            .Handle(new ObtenerResponsablesElegiblesBuzonesQuery([buzon.Id], [admin]), default);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_buzon_personal_uno_de_otro_proveedor_inexistente_o_no_visible_queda_fuera()
    {
        var admin = Guid.NewGuid();
        var cuentas = new Dictionary<Guid, DestinoCartera> { [admin] = Cuenta("Administrador") };
        var personal = Buzon(propietario: Guid.NewGuid());
        var whatsApp = Buzon(proveedor: ProveedorIntegracion.WhatsApp);
        var compartido = Buzon();

        var resultado = await CrearHandler(cuentas).Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery([personal.Id, whatsApp.Id, Guid.NewGuid(), compartido.Id], [admin]),
            default);

        resultado.Keys.Should().BeEquivalentTo(
            new[] { compartido.Id }, "control positivo: el compartido sí; un personal responde a su propietario");

        var invisible = await CrearHandler(cuentas, buzonVisible: false).Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery([compartido.Id], [admin]), default);
        invisible.Should().BeEmpty("quien no ve el buzón no averigua nada de él");
    }

    /// <summary>
    /// El acceso a un buzón compartido lo da la Asignación de Cartera (DECISION_LOG 2026-11-14), que aquí es el mismo alcance
    /// que aplican el listado de buzones y el comando sobre su Cliente empresarial: fuera de él, el buzón no aparece, aunque
    /// <c>ConexionIntegracionVisibleAsync</c> lo dé por visible.
    /// </summary>
    [Fact]
    public async Task Un_buzon_cuyo_Cliente_empresarial_queda_fuera_de_la_cartera_de_quien_consulta_no_aparece()
    {
        var admin = Guid.NewGuid();
        var cuentas = new Dictionary<Guid, DestinoCartera> { [admin] = Cuenta("Administrador") };
        var empresaEnCartera = Guid.NewGuid();
        var enCartera = Buzon(empresa: empresaEnCartera);
        var fueraDeCartera = Buzon(empresa: Guid.NewGuid());
        var triage = Buzon();
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [empresaEnCartera], conexionIntegracionVisible: true);

        var resultado = await CrearHandler(cuentas, alcance: alcance).Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery([enCartera.Id, fueraDeCartera.Id, triage.Id], [admin]), default);

        resultado.Keys.Should().BeEquivalentTo(
            new[] { enCartera.Id, triage.Id },
            "control positivo: el de su cartera y el de triage sí; el de fuera de su cartera no se distingue de uno inexistente");
    }

    /// <summary>
    /// La consulta aplica la regla entera, no una lista de roles: una cuenta de un Operador CAE externo con rol de origen
    /// Consulta y rol guardado Gestor CAE entra por la vía heredada (sin Asignación de Operación) y es elegible en un buzón de
    /// triage, como la aceptaría <c>FijarResponsableBuzonCommand</c>.
    /// </summary>
    [Fact]
    public async Task Una_cuenta_delegada_con_rol_de_origen_Consulta_y_rol_guardado_Gestor_CAE_es_elegible_por_la_via_heredada()
    {
        var delegada = Guid.NewGuid();
        var cuentas = new Dictionary<Guid, DestinoCartera>
        {
            [delegada] = new(true, "GestorCae", null, true, Guid.NewGuid(), "Consulta")
        };
        var buzon = Buzon();

        var resultado = await CrearHandler(cuentas).Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery([buzon.Id], [delegada]), default);

        resultado[buzon.Id].Should().BeEquivalentTo(new[] { delegada });
    }

    /// <summary>
    /// Coste: el universo son todas las cuentas visibles, portal y Consulta incluidas, así que cada cuenta se lee una vez para
    /// todos los buzones, no una por buzón; y el resultado por buzón es el mismo que el de la evaluación de uno solo.
    /// </summary>
    [Fact]
    public async Task Cada_cuenta_se_lee_una_vez_para_todos_los_buzones_con_el_mismo_resultado_que_buzon_a_buzon()
    {
        var admin = Guid.NewGuid();
        var gestor = Guid.NewGuid();
        var portal = Guid.NewGuid();
        var cuentas = new Dictionary<Guid, DestinoCartera>
        {
            [admin] = Cuenta("Administrador"),
            [gestor] = Cuenta("GestorCae"),
            [portal] = Cuenta("Cliente")
        };
        var empresa = Guid.NewGuid();
        var buzones = new[] { Buzon(), Buzon(empresa: empresa), Buzon(empresa: empresa) };
        var handler = CrearHandler(cuentas);

        var resultado = await handler.Handle(
            new ObtenerResponsablesElegiblesBuzonesQuery(buzones.Select(b => b.Id).ToList(), [admin, gestor, portal]), default);

        _directorio!.Lecturas.Should().Be(3, "una lectura por cuenta, no una por cuenta y buzón (serían 9)");
        resultado[buzones[0].Id].Should().BeEquivalentTo(new[] { admin, gestor }, "triage: decide solo el rol");
        resultado[buzones[1].Id].Should().BeEquivalentTo(new[] { admin }, "Gestor CAE sin cartera sobre la Empresa");
        resultado[buzones[2].Id].Should().BeEquivalentTo(new[] { admin });

        foreach (var buzon in buzones)
        {
            var uno = await CrearHandler(cuentas).Handle(
                new ObtenerResponsablesElegiblesBuzonesQuery([buzon.Id], [admin, gestor, portal]), default);
            uno[buzon.Id].Should().BeEquivalentTo(resultado[buzon.Id], "la evaluación conjunta no cambia la regla");
        }
    }
}
