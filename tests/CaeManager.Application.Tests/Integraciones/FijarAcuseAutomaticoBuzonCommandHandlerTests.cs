using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class FijarAcuseAutomaticoBuzonCommandHandlerTests
{
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private FijarAcuseAutomaticoBuzonCommandHandler CrearHandler(
        string? rol = "Administrador", AlcanceDatosServiceFalso? alcance = null) =>
        new(_conexiones, alcance ?? new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol), _unitOfWork);

    private ConexionIntegracion Buzon(ConexionIntegracion? conexion = null)
    {
        conexion ??= new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE");
        _conexiones.Agregar(conexion);
        return conexion;
    }

    [Fact]
    public void Un_buzon_nace_con_el_acuse_desactivado() =>
        new ConexionIntegracion("cae@ejemplo.test", "Buzón CAE").AcuseAutomaticoActivo.Should().BeFalse();

    [Fact]
    public async Task El_Administrador_activa_y_desactiva_el_acuse()
    {
        var conexion = Buzon();

        (await CrearHandler().Handle(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, true), default)).EsExitoso.Should().BeTrue();
        conexion.AcuseAutomaticoActivo.Should().BeTrue();

        (await CrearHandler().Handle(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, false), default)).EsExitoso.Should().BeTrue();
        conexion.AcuseAutomaticoActivo.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Ningun_otro_rol_cambia_el_acuse(string? rol)
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(rol).Handle(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, true), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.SoloAdministrador");
        conexion.AcuseAutomaticoActivo.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_buzon_fuera_del_alcance_no_se_distingue_de_uno_inexistente()
    {
        var conexion = Buzon();

        var resultado = await CrearHandler(alcance: new AlcanceDatosServiceFalso(conexionIntegracionVisible: false))
            .Handle(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, true), default);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexion.AcuseAutomaticoActivo.Should().BeFalse();
    }

    /// <summary>Administrar por encargo eleva el rol efectivo: sin esta exclusión, el handler dejaría pasar a quien solo opera.</summary>
    [Fact]
    public void Un_Encargo_de_administracion_no_cambia_el_acuse_igual_que_no_fija_el_responsable()
    {
        ActosExcluidosDelEncargo.PeticionesExcluidas.Should().Contain(typeof(FijarAcuseAutomaticoBuzonCommand));
        ActosExcluidosDelEncargo.Excluye(new FijarAcuseAutomaticoBuzonCommand(Guid.NewGuid(), true)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_linea_de_WhatsApp_no_tiene_acuse_de_correo()
    {
        var conexion = Buzon(new ConexionIntegracion("+34600000000", "Línea", proveedor: ProveedorIntegracion.WhatsApp));

        var resultado = await CrearHandler().Handle(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, true), default);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
    }

    [Fact]
    public async Task Un_buzon_personal_no_admite_acuse()
    {
        var conexion = Buzon(new ConexionIntegracion("gestora@ejemplo.test", "Personal", gestorPropietarioId: Guid.NewGuid()));

        var resultado = await CrearHandler().Handle(new FijarAcuseAutomaticoBuzonCommand(conexion.Id, true), default);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.PersonalSinAcuse");
        conexion.AcuseAutomaticoActivo.Should().BeFalse();
        conexion.Invoking(c => c.FijarAcuseAutomatico(true)).Should().Throw<InvalidOperationException>();
    }
}
