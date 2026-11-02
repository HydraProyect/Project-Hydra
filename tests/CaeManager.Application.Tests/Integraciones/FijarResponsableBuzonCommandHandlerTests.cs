using CaeManager.Application.Clientes;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class FijarResponsableBuzonCommandHandlerTests
{
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OperadorExterno = Guid.NewGuid();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly TransaccionDeComandoFalsa _transaccion = new();
    private BloqueoCarteraUsuarioFalso _bloqueo = new();

    private FijarResponsableBuzonCommandHandler CrearHandler(
        string? rol = "Administrador", AlcanceDatosServiceFalso? alcance = null, bool responsableVisible = true,
        bool responsableActivo = true, string? rolResponsable = "GestorCae", List<string>? eventos = null,
        bool delegado = false, IOperacionesQueryContext? operaciones = null, IEmpresasQueryContext? empresas = null) =>
        new(_conexiones, alcance ?? new AlcanceDatosServiceFalso(),
            new ResponsableBuzonElegible(
                new DirectorioDestinosCarteraFalso(responsableVisible
                    ? new DestinoCartera(responsableActivo, rolResponsable, null, delegado, delegado ? OperadorExterno : Tenant)
                    : null)
                { Eventos = eventos },
                new EquipoVacio(), operaciones ?? new OperacionesEnMemoria([], []), empresas ?? new EmpresasQueryContextFalso(),
                new TenantActualFijo(Tenant)),
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), _unitOfWork, _transaccion, CrearBloqueo(eventos));

    private BloqueoCarteraUsuarioFalso CrearBloqueo(List<string>? eventos)
    {
        _bloqueo = new BloqueoCarteraUsuarioFalso { Eventos = eventos };
        return _bloqueo;
    }

    private sealed class EquipoVacio : IDirectorioEquipoCoordinador
    {
        public Task<IReadOnlyList<MiembroDeEquipo>> ObtenerEquipoAsync(
            Guid coordinadorUsuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MiembroDeEquipo>>([]);
    }

    private ConexionIntegracion Buzon(ConexionIntegracion? conexion = null)
    {
        conexion ??= new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE");
        _conexiones.Agregar(conexion);
        return conexion;
    }

    [Fact]
    public async Task El_Administrador_fija_el_responsable()
    {
        var conexion = Buzon();
        var responsable = Guid.NewGuid();

        var resultado = await CrearHandler().Handle(new FijarResponsableBuzonCommand(conexion.Id, responsable), default);

        resultado.EsExitoso.Should().BeTrue();
        conexion.ResponsableUsuarioId.Should().Be(responsable);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task El_Administrador_puede_retirar_el_responsable()
    {
        var conexion = Buzon();
        conexion.FijarResponsable(Guid.NewGuid());

        var resultado = await CrearHandler().Handle(new FijarResponsableBuzonCommand(conexion.Id, null), default);

        resultado.EsExitoso.Should().BeTrue();
        conexion.ResponsableUsuarioId.Should().BeNull();
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Ningun_otro_rol_cambia_el_responsable(string? rol)
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(rol).Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.SoloAdministrador");
        conexion.ResponsableUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_responsable_de_otro_Tenant_se_rechaza()
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(responsableVisible: false)
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableNoValido");
        conexion.ResponsableUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_responsable_desactivado_se_rechaza()
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(responsableActivo: false)
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableNoValido");
        conexion.ResponsableUsuarioId.Should().BeNull();
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Un_rol_sin_gestion_se_rechaza(string? rol)
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(rolResponsable: rol)
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableSinGestion");
        conexion.ResponsableUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_CAE_sin_cartera_sobre_la_Empresa_del_buzon_se_rechaza()
    {
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", Guid.NewGuid()));

        var resultado = await CrearHandler()
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableSinCartera");
        conexion.ResponsableUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_cartera_universal_se_fija_sobre_un_Cliente_empresarial()
    {
        var empresa = ResponsableBuzonElegibleDePrueba.ClienteEmpresarial();
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", empresa.Id));
        var responsable = Guid.NewGuid();

        var resultado = await CrearHandler(
                operaciones: ResponsableBuzonElegibleDePrueba.CarteraUniversal(Tenant, responsable),
                empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, responsable), default);

        resultado.EsExitoso.Should().BeTrue();
        conexion.ResponsableUsuarioId.Should().Be(responsable);
    }

    [Fact]
    public async Task Con_cartera_universal_no_se_fija_sobre_una_Empresa_sin_discriminador()
    {
        var empresa = ResponsableBuzonElegibleDePrueba.SinDiscriminador();
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", empresa.Id));
        var responsable = Guid.NewGuid();

        var resultado = await CrearHandler(
                operaciones: ResponsableBuzonElegibleDePrueba.CarteraUniversal(Tenant, responsable),
                empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, responsable), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableSinCartera");
        conexion.ResponsableUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_operacion_universal_se_fija_al_Coordinador_CAE_delegado_sobre_un_Cliente_empresarial()
    {
        var empresa = ResponsableBuzonElegibleDePrueba.ClienteEmpresarial();
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", empresa.Id));
        var responsable = Guid.NewGuid();

        var resultado = await CrearHandler(
                rolResponsable: "CoordinadorCae", delegado: true,
                operaciones: ResponsableBuzonElegibleDePrueba.OperacionUniversal(Tenant, OperadorExterno),
                empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, responsable), default);

        resultado.EsExitoso.Should().BeTrue();
        conexion.ResponsableUsuarioId.Should().Be(responsable);
    }

    [Fact]
    public async Task Con_operacion_universal_no_se_fija_al_Coordinador_CAE_delegado_sobre_una_Empresa_sin_discriminador()
    {
        var empresa = ResponsableBuzonElegibleDePrueba.SinDiscriminador();
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", empresa.Id));

        var resultado = await CrearHandler(
                rolResponsable: "CoordinadorCae", delegado: true,
                operaciones: ResponsableBuzonElegibleDePrueba.OperacionUniversal(Tenant, OperadorExterno),
                empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.ResponsableSinCartera");
        conexion.ResponsableUsuarioId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task El_Administrador_se_fija_sobre_una_Empresa_sin_discriminador()
    {
        var empresa = ResponsableBuzonElegibleDePrueba.SinDiscriminador();
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", empresa.Id));
        var responsable = Guid.NewGuid();

        var resultado = await CrearHandler(
                rolResponsable: "Administrador", empresas: ResponsableBuzonElegibleDePrueba.EmpresasDelTenant(empresa))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, responsable), default);

        resultado.EsExitoso.Should().BeTrue();
        conexion.ResponsableUsuarioId.Should().Be(responsable);
    }

    [Fact]
    public async Task El_candado_compartido_se_toma_antes_de_leer_la_cuenta_y_dentro_de_la_transaccion()
    {
        var conexion = Buzon();
        var responsable = Guid.NewGuid();
        var eventos = new List<string>();

        await CrearHandler(eventos: eventos).Handle(new FijarResponsableBuzonCommand(conexion.Id, responsable), default);

        eventos.Should().Equal("compartido", "leer-destino");
        _bloqueo.Compartidos.Should().Equal(responsable);
        _bloqueo.Exclusivos.Should().BeEmpty();
        _transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Retirar_el_responsable_no_toma_el_candado()
    {
        var conexion = Buzon();

        await CrearHandler().Handle(new FijarResponsableBuzonCommand(conexion.Id, null), default);

        _bloqueo.Compartidos.Should().BeEmpty();
    }

    [Fact]
    public void Un_responsable_con_Guid_vacio_no_pasa_la_validacion()
    {
        new FijarResponsableBuzonCommandValidator()
            .Validate(new FijarResponsableBuzonCommand(Guid.NewGuid(), Guid.Empty)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Una_conexion_que_no_es_del_Tenant_no_se_encuentra()
    {
        var resultado = await CrearHandler().Handle(new FijarResponsableBuzonCommand(Guid.NewGuid(), Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
    }

    [Fact]
    public async Task Un_buzon_fuera_de_alcance_no_se_encuentra()
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(alcance: new AlcanceDatosServiceFalso(conexionIntegracionVisible: false))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexion.ResponsableUsuarioId.Should().BeNull();
    }

    [Fact]
    public async Task Un_buzon_de_un_Cliente_empresarial_fuera_de_alcance_no_se_encuentra()
    {
        var conexion = Buzon(new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE", Guid.NewGuid()));

        var resultado = await CrearHandler(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []))
            .Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexion.ResponsableUsuarioId.Should().BeNull();
    }

    [Fact]
    public async Task Una_linea_de_WhatsApp_no_tiene_responsable_de_buzon()
    {
        var conexion = Buzon(new ConexionIntegracion("+34600000000", "Línea", proveedor: ProveedorIntegracion.WhatsApp));

        var resultado = await CrearHandler().Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
    }

    [Fact]
    public async Task Un_buzon_personal_no_admite_otro_responsable()
    {
        var conexion = Buzon(new ConexionIntegracion("gestor@ejemplo.test", "Personal", gestorPropietarioId: Guid.NewGuid()));

        var resultado = await CrearHandler().Handle(new FijarResponsableBuzonCommand(conexion.Id, Guid.NewGuid()), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.PersonalSinResponsable");
        conexion.ResponsableUsuarioId.Should().BeNull();
    }
}
