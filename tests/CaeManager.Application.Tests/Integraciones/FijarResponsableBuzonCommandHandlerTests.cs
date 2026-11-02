using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class FijarResponsableBuzonCommandHandlerTests
{
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private FijarResponsableBuzonCommandHandler CrearHandler(
        string? rol = "Administrador", AlcanceDatosServiceFalso? alcance = null, bool responsableVisible = true) =>
        new(_conexiones, alcance ?? new AlcanceDatosServiceFalso(), new DirectorioUsuariosServiceFalso(responsableVisible),
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), _unitOfWork);

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
