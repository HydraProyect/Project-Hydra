using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.GuardarConexionPlataformaCae;
using CaeManager.Application.Integraciones.Commands.RetirarConexionPlataformaCae;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class ConexionPlataformaCaeCommandsTests
{
    private readonly ConexionPlataformaCaeRepositorioFalso _conexiones = new();
    private readonly ProveedorPlataformaCaeRepositorioFalso _proveedores = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly ProveedorPlataformaCae _proveedor = new("plataforma-x", "Plataforma X");
    private readonly Guid _tenant = Guid.NewGuid();

    public ConexionPlataformaCaeCommandsTests() => _proveedores.Agregar(_proveedor);

    private GuardarConexionPlataformaCaeCommandHandler Guardar(string? rol = "Administrador", Guid? tenantOrigen = null) =>
        new(_conexiones, _proveedores, new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenantOrigen ?? _tenant), new TenantFalso(_tenant));

    private RetirarConexionPlataformaCaeCommandHandler Retirar(string? rol = "Administrador", Guid? tenantOrigen = null) =>
        new(_conexiones, new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenantOrigen ?? _tenant), new TenantFalso(_tenant), _unitOfWork);

    [Fact]
    public void Guardar_y_retirar_exigen_el_segundo_factor_de_las_escrituras_de_credencial()
    {
        typeof(IEscrituraDeDatosDeCredencial).IsAssignableFrom(typeof(GuardarConexionPlataformaCaeCommand)).Should().BeTrue();
        typeof(IEscrituraDeDatosDeCredencial).IsAssignableFrom(typeof(RetirarConexionPlataformaCaeCommand)).Should().BeTrue();
    }

    [Fact]
    public void El_command_no_imprime_el_secreto()
    {
        new GuardarConexionPlataformaCaeCommand(Guid.NewGuid(), "Acuerdo", "secreto").ToString().Should().NotContain("secreto");
    }

    [Fact]
    public async Task Guardar_crea_la_conexion_con_su_credencial()
    {
        var resultado = await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "uno"), default);

        resultado.EsExitoso.Should().BeTrue();
        _conexiones.Conexiones.Should().ContainSingle(c => c.Id == resultado.Valor);
        _conexiones.Credenciales.Should().ContainSingle(c => c.SecretoApi == "uno" && c.ConexionPlataformaCaeId == resultado.Valor);
    }

    [Fact]
    public async Task Guardar_otra_vez_rota_la_credencial_sin_duplicar()
    {
        var primera = await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "uno"), default);

        var segunda = await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "dos"), default);

        segunda.Valor.Should().Be(primera.Valor);
        _conexiones.Conexiones.Should().ContainSingle();
        _conexiones.Credenciales.Should().ContainSingle().Which.SecretoApi.Should().Be("dos");
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Solo lectura")]
    [InlineData(null)]
    public async Task Sin_ser_Administrador_no_se_guarda_ni_se_retira(string? rol)
    {
        var guardado = await Guardar(rol).Handle(new(_proveedor.Id, "Acuerdo", "uno"), default);
        guardado.EsExitoso.Should().BeFalse();
        _conexiones.Conexiones.Should().BeEmpty();

        var conexion = new ConexionPlataformaCae(_proveedor.Id, "Acuerdo");
        _conexiones.Agregar(conexion);
        var retirada = await Retirar(rol).Handle(new(conexion.Id), default);
        retirada.EsExitoso.Should().BeFalse();
        conexion.EstaEliminado.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_se_conecta_a_una_plataforma_que_no_existe()
    {
        var resultado = await Guardar().Handle(new(Guid.NewGuid(), "Acuerdo", "uno"), default);

        resultado.EsExitoso.Should().BeFalse();
    }

    [Fact]
    public async Task Un_nombre_en_uso_en_otra_plataforma_no_rota_aquella_credencial()
    {
        var otra = new ProveedorPlataformaCae("plataforma-y", "Plataforma Y");
        _proveedores.Agregar(otra);
        await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "uno"), default);

        var resultado = await Guardar().Handle(new(otra.Id, "Acuerdo", "dos"), default);

        resultado.EsExitoso.Should().BeFalse();
        _conexiones.Credenciales.Should().ContainSingle().Which.SecretoApi.Should().Be("uno");
    }

    [Fact]
    public async Task Retirar_destruye_la_credencial_y_elimina_la_conexion()
    {
        var conexionId = (await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "uno"), default)).Valor;

        var resultado = await Retirar().Handle(new(conexionId), default);

        resultado.EsExitoso.Should().BeTrue();
        _conexiones.Credenciales.Should().BeEmpty();
        _conexiones.Conexiones.Single().EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task El_Administrador_del_Operador_CAE_externo_no_sustituye_ni_retira_la_credencial_del_Tenant_propietario()
    {
        // En un Workspace operativo derivado su rol efectivo es "Administrador": el perfil de su Tenant de origen.
        var operador = Guid.NewGuid();
        var conexionId = (await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "uno"), default)).Valor;

        var guardado = await Guardar(tenantOrigen: operador).Handle(new(_proveedor.Id, "Acuerdo", "de-su-cuenta"), default);
        var retirada = await Retirar(tenantOrigen: operador).Handle(new(conexionId), default);

        guardado.EsExitoso.Should().BeFalse();
        retirada.EsExitoso.Should().BeFalse();
        _conexiones.Credenciales.Should().ContainSingle().Which.SecretoApi.Should().Be("uno");
        _conexiones.Conexiones.Single().EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Guardar_recorta_lo_pegado_alrededor_del_secreto()
    {
        await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "  uno\n"), default);

        _conexiones.Credenciales.Should().ContainSingle().Which.SecretoApi.Should().Be("uno");
    }

    [Fact]
    public async Task Perder_la_carrera_por_el_nombre_es_un_error_y_no_una_excepcion()
    {
        _conexiones.ConflictoAlGuardar = true;

        var resultado = await Guardar().Handle(new(_proveedor.Id, "Acuerdo", "uno"), default);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be("ConexionPlataformaCae.Conflicto");
    }

    private sealed class TenantFalso(Guid tenant) : ITenantActual
    {
        public Guid? TenantId => tenant;
    }
    [Fact]
    public async Task Retirar_una_conexion_que_no_existe_falla()
    {
        (await Retirar().Handle(new(Guid.NewGuid()), default)).EsExitoso.Should().BeFalse();
    }
}
