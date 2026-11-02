using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Monta la clase real con dobles en memoria: las demás pruebas de buzón no replican su regla.</summary>
public static class ResponsableBuzonElegibleDePrueba
{
    public static ResponsableBuzonElegible Con(
        DestinoCartera? destino, IOperacionesQueryContext? operaciones = null, Guid? tenantActual = null,
        IReadOnlyList<Guid>? equipo = null, IEmpresasQueryContext? empresas = null) =>
        new(new DirectorioDestinosCarteraFalso(destino), new EquipoFalso(equipo ?? []),
            operaciones ?? new OperacionesEnMemoria([], []), empresas ?? new EmpresasQueryContextFalso(),
            new TenantActualFijo(tenantActual));

    public static Empresa ClienteEmpresarial() => Empresa.CrearComoCliente("Montajes Springfield", "B12345674", false, null, null);

    /// <summary>Una Empresa del Tenant que no es Cliente empresarial: <c>EsCritico</c> queda sin informar.</summary>
    public static Empresa SinDiscriminador() => Empresa.CrearComoSubcontrata("Transportes Shelbyville", null, "Estandar");

    public static EmpresasQueryContextFalso EmpresasDelTenant(params Empresa[] empresas)
    {
        var contexto = new EmpresasQueryContextFalso();
        contexto.ListaEmpresas.AddRange(empresas);
        return contexto;
    }

    /// <summary>Las asignaciones nacen Programadas si su inicio es pasado: se activan como en producción.</summary>
    public static T Vigente<T>(T asignacion) where T : class
    {
        typeof(T).GetProperty("Estado")!.SetValue(asignacion, EstadoAsignacion.Vigente);
        return asignacion;
    }

    /// <summary>Una Asignación de Cartera universal bajo la raíz del Tenant propietario.</summary>
    public static OperacionesEnMemoria CarteraUniversal(Guid propietarioTenantId, Guid usuarioId)
    {
        var ahora = DateTime.UtcNow;
        var raiz = Vigente(AsignacionOperacion.Raiz(propietarioTenantId, ServicioCae.Outbound, ahora.AddDays(-30), ahora));
        var cartera = Vigente(AsignacionCartera.Interna(raiz, usuarioId, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        return new OperacionesEnMemoria([raiz], [cartera]);
    }

    /// <summary>Una Asignación de Operación del Tenant entero a un Operador CAE externo, sin ninguna cartera.</summary>
    public static OperacionesEnMemoria OperacionUniversal(Guid propietarioTenantId, Guid operadorTenantId)
    {
        var ahora = DateTime.UtcNow;
        return new OperacionesEnMemoria(
            [Vigente(AsignacionOperacion.Externa(
                propietarioTenantId, operadorTenantId, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddDays(-30), null, ahora))],
            []);
    }

    private sealed class EquipoFalso(IReadOnlyList<Guid> equipo) : IDirectorioEquipoCoordinador
    {
        public Task<IReadOnlyList<MiembroDeEquipo>> ObtenerEquipoAsync(
            Guid coordinadorUsuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MiembroDeEquipo>>(
                equipo.Select(id => new MiembroDeEquipo(id, "gestor@ejemplo.test", "Gestor", true, false)).ToList());
    }
}

public sealed class OperacionesEnMemoria(IEnumerable<AsignacionOperacion> operaciones, IEnumerable<AsignacionCartera> carteras)
    : IOperacionesQueryContext
{
    public IQueryable<AsignacionOperacion> AsignacionesOperacion =>
        new TestAsyncQueryable<AsignacionOperacion>(operaciones.AsQueryable());

    public IQueryable<AsignacionCartera> AsignacionesCartera =>
        new TestAsyncQueryable<AsignacionCartera>(carteras.AsQueryable());
}

public class ResponsableBuzonElegibleTests
{
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly Guid OperadorExterno = Guid.NewGuid();
    private static readonly EmpresasQueryContextFalso EmpresasDelTenant = ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(
        ResponsableBuzonElegibleDePrueba.ClienteEmpresarial(), ResponsableBuzonElegibleDePrueba.SinDiscriminador());
    private static readonly Guid Empresa = EmpresasDelTenant.ListaEmpresas[0].Id;
    private static readonly Guid EmpresaSinDiscriminador = EmpresasDelTenant.ListaEmpresas[1].Id;
    private static readonly Guid Gestor = Guid.NewGuid();
    private static readonly DateTime Ahora = DateTime.UtcNow;

    private static DestinoCartera Propio(string? rol) => new(true, rol, null, false, Propietario);

    private static DestinoCartera Delegado(string? rol) => new(true, rol, null, true, OperadorExterno);

    private static T Vigente<T>(T asignacion) where T : class => ResponsableBuzonElegibleDePrueba.Vigente(asignacion);

    private static AsignacionOperacion Raiz(Guid? propietario = null) =>
        Vigente(AsignacionOperacion.Raiz(propietario ?? Propietario, ServicioCae.Outbound, Ahora.AddDays(-30), Ahora));

    private static AsignacionOperacion Externa(AmbitoAsignacion ambito, DateTime? hasta = null) =>
        Vigente(AsignacionOperacion.Externa(
            Propietario, OperadorExterno, ServicioCae.Outbound, ambito, Ahora.AddDays(-30), hasta, Ahora));

    private static AsignacionCartera Interna(
        AsignacionOperacion raiz, Guid usuario, AmbitoAsignacion? ambito = null, DateTime? hasta = null) =>
        Vigente(AsignacionCartera.Interna(
            raiz, usuario, ambito ?? AmbitoAsignacion.Universal, Ahora.AddDays(-10), hasta, Ahora));

    private static AsignacionCartera Externa(AsignacionOperacion operacion, Guid usuario) =>
        Vigente(AsignacionCartera.Externa(
            operacion, usuario, "GestorCae", AmbitoAsignacion.Universal, Ahora.AddDays(-10), null, Ahora));

    private static ConexionIntegracion Buzon(Guid? empresa) => new("cae@ejemplo.test", "Buzón CAE", empresa);

    private static Task<MotivoResponsableNoElegible?> EvaluarAsync(
        DestinoCartera? destino, IEnumerable<AsignacionOperacion>? operaciones = null,
        IEnumerable<AsignacionCartera>? carteras = null, Guid? cliente = null, bool conEmpresa = true,
        IReadOnlyList<Guid>? equipo = null) =>
        ResponsableBuzonElegibleDePrueba
            .Con(destino, new OperacionesEnMemoria(operaciones ?? [], carteras ?? []), Propietario, equipo, EmpresasDelTenant)
            .EvaluarAsync(Gestor, Buzon(conEmpresa ? cliente ?? Empresa : null), default);

    [Fact]
    public async Task Una_cuenta_no_alcanzable_desde_el_Tenant_no_es_elegible()
    {
        (await EvaluarAsync(null)).Should().Be(MotivoResponsableNoElegible.NoAlcanzable);
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_es_elegible_ni_con_rol_de_Administrador()
    {
        (await EvaluarAsync(Propio("Administrador") with { Activa = false }))
            .Should().Be(MotivoResponsableNoElegible.Desactivado);
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Un_rol_sin_gestion_no_es_elegible_ni_en_un_buzon_sin_Empresa(string? rol)
    {
        var raiz = Raiz();
        var cartera = Interna(raiz, Gestor);

        (await EvaluarAsync(Propio(rol), [raiz], [cartera])).Should().Be(MotivoResponsableNoElegible.RolSinGestion);
        (await EvaluarAsync(Propio(rol), [raiz], [cartera], conEmpresa: false))
            .Should().Be(MotivoResponsableNoElegible.RolSinGestion);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Administrador_y_Direccion_CAE_atienden_por_su_rol_sin_cartera(string rol)
    {
        (await EvaluarAsync(Propio(rol))).Should().BeNull();
    }

    // AlcanceDatosService.ClienteVisibleAsync: con acceso total no hay lista y no se mira el discriminador.
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Administrador_y_Direccion_CAE_no_dependen_del_discriminador_de_la_Empresa(string rol)
    {
        (await EvaluarAsync(Propio(rol), cliente: EmpresaSinDiscriminador)).Should().BeNull();
        (await EvaluarAsync(Delegado(rol), cliente: EmpresaSinDiscriminador)).Should().BeNull();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    public async Task En_un_buzon_sin_Empresa_decide_solo_el_rol(string rol)
    {
        (await EvaluarAsync(Propio(rol), conEmpresa: false)).Should().BeNull();
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_cartera_no_atiende_un_buzon_de_una_Empresa()
    {
        (await EvaluarAsync(Propio("GestorCae"))).Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_cartera_interna_vigente_atiende_el_buzon()
    {
        var raiz = Raiz();

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [Interna(raiz, Gestor)])).Should().BeNull();
    }

    [Fact]
    public async Task Una_cartera_universal_no_alcanza_una_Empresa_sin_discriminador()
    {
        var raiz = Raiz();

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [Interna(raiz, Gestor)], cliente: EmpresaSinDiscriminador))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task La_cartera_universal_de_un_Gestor_CAE_delegado_solo_alcanza_Clientes_empresariales()
    {
        var operacion = Externa(AmbitoAsignacion.Universal);
        var cartera = Externa(operacion, Gestor);

        (await EvaluarAsync(Delegado("GestorCae"), [operacion], [cartera], cliente: Empresa)).Should().BeNull();
        (await EvaluarAsync(Delegado("GestorCae"), [operacion], [cartera], cliente: EmpresaSinDiscriminador))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_Empresa_que_no_se_puede_leer_no_se_da_por_alcanzable_bajo_cartera_universal()
    {
        var raiz = Raiz();

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [Interna(raiz, Gestor)], cliente: Guid.NewGuid()))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_operacion_acotada_alcanza_la_Empresa_que_nombra_aunque_no_tenga_discriminador()
    {
        var operacion = Externa(AmbitoAsignacion.DeRelacionCliente(EmpresaSinDiscriminador));

        (await EvaluarAsync(Delegado("GestorCae"), [operacion], [Externa(operacion, Gestor)], cliente: EmpresaSinDiscriminador))
            .Should().BeNull();
    }

    [Fact]
    public async Task Con_una_cartera_universal_una_acotada_no_suma_la_Empresa_sin_discriminador()
    {
        var universal = Externa(AmbitoAsignacion.Universal);
        var acotada = Externa(AmbitoAsignacion.DeRelacionCliente(EmpresaSinDiscriminador));

        (await EvaluarAsync(
                Delegado("GestorCae"), [universal, acotada], [Externa(universal, Gestor), Externa(acotada, Gestor)],
                cliente: EmpresaSinDiscriminador))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_cartera_mal_formada_acotada_por_Cliente_empresarial_no_concede_nada()
    {
        var raiz = Raiz();
        var malFormada = Interna(raiz, Gestor);
        typeof(AsignacionCartera).GetProperty(nameof(AsignacionCartera.AmbitoRelacionClienteId))!.SetValue(malFormada, Empresa);

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [malFormada]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task La_cartera_de_otra_persona_no_ampara_a_un_Gestor_CAE()
    {
        var raiz = Raiz();

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [Interna(raiz, Guid.NewGuid())]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_cartera_caducada_no_ampara()
    {
        var raiz = Raiz();

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [Interna(raiz, Gestor, hasta: Ahora.AddDays(-1))]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_cartera_programada_todavia_no_ampara()
    {
        var raiz = Raiz();
        var programada = AsignacionCartera.Interna(raiz, Gestor, AmbitoAsignacion.Universal, Ahora.AddDays(5), null, Ahora);

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [programada]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_dimension_diferida_de_la_cartera_no_concede_nada()
    {
        var raiz = Raiz();
        var porCentro = Interna(raiz, Gestor, new AmbitoAsignacion(CentroId: Guid.NewGuid()));

        (await EvaluarAsync(Propio("GestorCae"), [raiz], [porCentro]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task La_cartera_de_otro_Tenant_propietario_no_ampara()
    {
        var ajena = Raiz(Guid.NewGuid());

        (await EvaluarAsync(Propio("GestorCae"), [ajena], [Interna(ajena, Gestor)]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Bajo_una_operacion_de_otro_operador_la_cartera_no_ampara()
    {
        var operacion = Externa(AmbitoAsignacion.Universal);

        // La cuenta es propia del Tenant: la Asignación de Operación la opera otra organización.
        (await EvaluarAsync(Propio("GestorCae"), [operacion], [Externa(operacion, Gestor)]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_operacion_acotada_solo_alcanza_su_Cliente_empresarial()
    {
        var operacion = Externa(AmbitoAsignacion.DeRelacionCliente(Empresa));
        var cartera = Externa(operacion, Gestor);

        (await EvaluarAsync(Delegado("GestorCae"), [operacion], [cartera], cliente: Empresa)).Should().BeNull();
        (await EvaluarAsync(Delegado("GestorCae"), [operacion], [cartera], cliente: Guid.NewGuid()))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Una_operacion_caducada_no_ampara_su_cartera_vigente()
    {
        var operacion = Externa(AmbitoAsignacion.Universal, hasta: Ahora.AddDays(-1));

        (await EvaluarAsync(Delegado("GestorCae"), [operacion], [Externa(operacion, Gestor)]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_atiende_por_la_cartera_de_su_equipo()
    {
        var raiz = Raiz();
        var gestora = Guid.NewGuid();
        var cartera = Interna(raiz, gestora);

        (await EvaluarAsync(Propio("CoordinadorCae"), [raiz], [cartera], equipo: [gestora])).Should().BeNull();
        (await EvaluarAsync(Propio("CoordinadorCae"), [raiz], [cartera]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task La_cartera_universal_de_su_equipo_no_lleva_al_Coordinador_CAE_a_una_Empresa_sin_discriminador()
    {
        var raiz = Raiz();
        var gestora = Guid.NewGuid();

        (await EvaluarAsync(
                Propio("CoordinadorCae"), [raiz], [Interna(raiz, gestora)], cliente: EmpresaSinDiscriminador, equipo: [gestora]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_delegado_atiende_si_su_organizacion_opera_el_Tenant_entero()
    {
        (await EvaluarAsync(Delegado("CoordinadorCae"), [Externa(AmbitoAsignacion.Universal)])).Should().BeNull();
    }

    [Fact]
    public async Task Una_operacion_universal_no_lleva_al_Coordinador_CAE_delegado_a_una_Empresa_sin_discriminador()
    {
        (await EvaluarAsync(Delegado("CoordinadorCae"), [Externa(AmbitoAsignacion.Universal)], cliente: EmpresaSinDiscriminador))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Con_operacion_universal_una_cartera_acotada_no_suma_la_Empresa_sin_discriminador()
    {
        var universal = Externa(AmbitoAsignacion.Universal);
        var acotada = Externa(AmbitoAsignacion.DeRelacionCliente(EmpresaSinDiscriminador));

        (await EvaluarAsync(
                Delegado("CoordinadorCae"), [universal, acotada], [Externa(acotada, Gestor)], cliente: EmpresaSinDiscriminador))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Un_Coordinador_CAE_delegado_bajo_operacion_acotada_necesita_cartera()
    {
        var operacion = Externa(AmbitoAsignacion.DeRelacionCliente(Empresa));

        (await EvaluarAsync(Delegado("CoordinadorCae"), [operacion]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
        (await EvaluarAsync(Delegado("CoordinadorCae"), [operacion], [Externa(operacion, Gestor)])).Should().BeNull();
    }

    [Fact]
    public async Task Un_Gestor_CAE_delegado_no_hereda_la_operacion_del_Tenant_entero_sin_cartera()
    {
        (await EvaluarAsync(Delegado("GestorCae"), [Externa(AmbitoAsignacion.Universal)]))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }

    [Fact]
    public async Task Sin_Tenant_activo_o_sin_Tenant_de_origen_falla_cerrado()
    {
        var raiz = Raiz();
        var operaciones = new OperacionesEnMemoria([raiz], [Interna(raiz, Gestor)]);

        (await ResponsableBuzonElegibleDePrueba.Con(Propio("GestorCae"), operaciones, tenantActual: null)
                .EvaluarAsync(Gestor, Buzon(Empresa), default))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
        (await ResponsableBuzonElegibleDePrueba.Con(Propio("GestorCae") with { TenantOrigenId = null }, operaciones, Propietario)
                .EvaluarAsync(Gestor, Buzon(Empresa), default))
            .Should().Be(MotivoResponsableNoElegible.SinCarteraSobreLaEmpresa);
    }
}
