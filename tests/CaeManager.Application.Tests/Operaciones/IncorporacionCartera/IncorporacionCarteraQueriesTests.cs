using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Operaciones.IncorporacionCartera;

public class IncorporacionCarteraQueriesTests
{
    private readonly Guid _operador = Guid.NewGuid();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _coordinador = Guid.NewGuid();
    private readonly Tenant _empresa = new("Refrigeración Levante Demo");
    private readonly AsignacionOperacion _operacion;

    private readonly CatalogoIncorporacionCarteraFalso _catalogo = new();
    private readonly SolicitudIncorporacionCarteraRepositorioFalso _repositorio = new();
    private readonly TenantsQueryContextFalso _tenants = new();
    private readonly DirectorioRolesEnOrigen _directorio = new();

    public IncorporacionCarteraQueriesTests()
    {
        _operacion = AsignacionOperacion.Externa(
            _empresa.Id, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            DateTime.UtcNow.AddDays(-30), null, DateTime.UtcNow);
        _tenants.ListaTenants.Add(_empresa);
    }

    // rolEnOrigen es el de la cuenta en su organización (Identity); rolFuera, el de la cartera en el
    // Workspace operativo derivado abierto. Con uno abierto, el claim de sesión ya es rolFuera
    // (RolEfectivoDelWorkspaceMiddleware), también dentro del ámbito de origen.
    private CurrentUserServicePorAmbito Como(Guid usuario, string? rolEnOrigen, string? rolFuera = null)
    {
        _directorio.Asignar(usuario, _operador, rolEnOrigen);
        return new(usuario, _operador, rolFuera ?? rolEnOrigen, rolFuera);
    }

    private ObtenerSolicitudesIncorporacionCarteraQueryHandler Bandeja(CurrentUserServicePorAmbito usuario) =>
        new(usuario, _catalogo, _repositorio, _directorio, _tenants);

    private SolicitudIncorporacionCartera Nueva(Guid solicitante)
    {
        var solicitud = SolicitudIncorporacionCartera.Crear(_operacion, solicitante, "Llevo sus centros", DateTime.UtcNow);
        _repositorio.Agregar(solicitud);
        return solicitud;
    }

    private AsignacionCartera CarteraDe(Guid usuario) =>
        AsignacionCartera.Externa(_operacion, usuario, "GestorCae", AmbitoAsignacion.Universal, DateTime.UtcNow, null, DateTime.UtcNow);

    [Fact]
    public async Task Los_candidatos_llevan_solo_el_nombre_y_la_solicitud_pendiente_si_la_hay()
    {
        _catalogo.RegistrarCandidato(_operador, _gestor, _operacion, _empresa.Nombre);
        var pendiente = Nueva(_gestor);

        var resultado = await new ObtenerCandidatosIncorporacionCarteraQueryHandler(Como(_gestor, "GestorCae"), _directorio, _catalogo, _repositorio)
            .Handle(new ObtenerCandidatosIncorporacionCarteraQuery(), CancellationToken.None);

        resultado.Valor.Should().Equal(new CandidatoIncorporacionCarteraDto(_empresa.Id, _empresa.Nombre, pendiente.Id));
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Solo_un_Gestor_CAE_consulta_candidatos(string? rol)
    {
        _catalogo.RegistrarCandidato(_operador, _gestor, _operacion, _empresa.Nombre);

        var resultado = await new ObtenerCandidatosIncorporacionCarteraQueryHandler(Como(_gestor, rol, rolFuera: "GestorCae"), _directorio, _catalogo, _repositorio)
            .Handle(new ObtenerCandidatosIncorporacionCarteraQuery(), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
        _catalogo.ConsultasDeCandidatos.Should().BeEmpty();
    }

    /// <summary>
    /// Hallazgo de Codex (P1): el aviso y la bandeja del Coordinador CAE no desaparecen porque
    /// tenga abierto un Tenant propietario donde su cartera es de Gestor CAE.
    /// </summary>
    [Fact]
    public async Task Un_Coordinador_CAE_que_opera_un_Tenant_como_Gestor_CAE_ve_la_bandeja_de_Coordinador_CAE()
    {
        var pendiente = Nueva(_gestor);

        var bandeja = (await Bandeja(Como(_coordinador, "CoordinadorCae", rolFuera: "GestorCae"))
            .Handle(new ObtenerSolicitudesIncorporacionCarteraQuery(), CancellationToken.None)).Valor;

        bandeja.EsCoordinadorCae.Should().BeTrue();
        bandeja.Pendientes.Should().ContainSingle().Which.Id.Should().Be(pendiente.Id);
    }

    [Theory]
    [InlineData("CoordinadorCae", "Consulta", true)]
    [InlineData("GestorCae", "Consulta", true)]
    [InlineData("GestorCae", null, true)]
    [InlineData("Consulta", "CoordinadorCae", false)]
    [InlineData("Administrador", null, false)]
    [InlineData(null, "GestorCae", false)]
    public async Task Participa_quien_es_Gestor_o_Coordinador_CAE_en_su_organizacion_no_en_el_Tenant_abierto(
        string? rolEnOrigen, string? rolFuera, bool participa)
    {
        var resultado = await new ParticipaEnIncorporacionCarteraQueryHandler(Como(_coordinador, rolEnOrigen, rolFuera), _directorio)
            .Handle(new ParticipaEnIncorporacionCarteraQuery(), CancellationToken.None);

        resultado.Should().Be(participa);
    }

    [Fact]
    public async Task El_Coordinador_CAE_ve_las_pendientes_de_su_Operador_CAE_y_no_puede_resolver_la_suya()
    {
        var ajena = Nueva(_gestor);
        var suya = Nueva(_coordinador);

        var bandeja = (await Bandeja(Como(_coordinador, "CoordinadorCae"))
            .Handle(new ObtenerSolicitudesIncorporacionCarteraQuery(), CancellationToken.None)).Valor;

        bandeja.EsCoordinadorCae.Should().BeTrue();
        bandeja.Pendientes.Should().HaveCount(2);
        bandeja.Pendientes.Single(p => p.Id == ajena.Id).PuedeResolver.Should().BeTrue();
        bandeja.Pendientes.Single(p => p.Id == suya.Id).PuedeResolver.Should().BeFalse();
        bandeja.Pendientes.Should().OnlyContain(p => p.NombreEmpresa == _empresa.Nombre);
        bandeja.Propias.Should().BeEmpty();
    }

    [Fact]
    public async Task Las_incorporaciones_listadas_son_solo_las_de_cartera_vigente()
    {
        var vigente = Nueva(_gestor);
        var carteraVigente = CarteraDe(_gestor);
        vigente.Aceptar(_coordinador, carteraVigente, null, DateTime.UtcNow);
        _catalogo.CarterasVigentes.Add(carteraVigente.Id);

        var otroGestor = Guid.NewGuid();
        var cerradaPorOtraVia = Nueva(otroGestor);
        cerradaPorOtraVia.Aceptar(_coordinador, CarteraDe(otroGestor), null, DateTime.UtcNow);

        var bandeja = (await Bandeja(Como(_coordinador, "CoordinadorCae"))
            .Handle(new ObtenerSolicitudesIncorporacionCarteraQuery(), CancellationToken.None)).Valor;

        bandeja.Incorporaciones.Should().ContainSingle().Which.Id.Should().Be(vigente.Id);
        bandeja.Incorporaciones[0].PuedeRevocar.Should().BeTrue();
    }

    [Fact]
    public async Task El_Gestor_CAE_ve_solo_las_suyas_y_revoca_las_aceptadas()
    {
        var suya = Nueva(_gestor);
        suya.Aceptar(_coordinador, CarteraDe(_gestor), null, DateTime.UtcNow);
        Nueva(Guid.NewGuid());

        var bandeja = (await Bandeja(Como(_gestor, "GestorCae"))
            .Handle(new ObtenerSolicitudesIncorporacionCarteraQuery(), CancellationToken.None)).Valor;

        bandeja.EsCoordinadorCae.Should().BeFalse();
        bandeja.Pendientes.Should().BeEmpty();
        bandeja.Incorporaciones.Should().BeEmpty();
        var propia = bandeja.Propias.Should().ContainSingle().Subject;
        propia.Id.Should().Be(suya.Id);
        propia.PuedeRevocar.Should().BeTrue();
        propia.PuedeResolver.Should().BeFalse();
    }

    [Fact]
    public async Task Solo_pendientes_para_el_aviso_no_carga_el_resto()
    {
        var aceptada = Nueva(_gestor);
        var cartera = CarteraDe(_gestor);
        aceptada.Aceptar(_coordinador, cartera, null, DateTime.UtcNow);
        _catalogo.CarterasVigentes.Add(cartera.Id);
        Nueva(Guid.NewGuid());

        var bandeja = (await Bandeja(Como(_coordinador, "CoordinadorCae"))
            .Handle(new ObtenerSolicitudesIncorporacionCarteraQuery(SoloPendientes: true), CancellationToken.None)).Valor;

        bandeja.Pendientes.Should().ContainSingle();
        bandeja.Incorporaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("DireccionCae")]
    [InlineData(null)]
    public async Task Otros_roles_no_ven_la_bandeja(string? rol)
    {
        Nueva(_gestor);

        var resultado = await Bandeja(Como(_coordinador, rol, rolFuera: "CoordinadorCae"))
            .Handle(new ObtenerSolicitudesIncorporacionCarteraQuery(), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SolicitudCartera.SinPermiso");
    }
}
