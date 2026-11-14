using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// La proyección <see cref="ConexionIntegracionListaDto.AcuseAutomaticoActivo"/> de
/// <see cref="ObtenerConexionesIntegracionQueryHandler"/>, que es lo que enseña el interruptor de la página de Buzones: nace
/// desactivada, refleja lo guardado y solo se lista lo que está dentro del alcance de quien pregunta (una Empresa fuera de
/// su alcance, la de otro Tenant incluida, no llega al listado). El aislamiento entre Tenants lo hace RLS
/// (<c>AcuseAutomaticoBuzonBajoRlsTests</c>, integración); aquí se fija que la consulta no se salta el alcance.
/// </summary>
public class ObtenerConexionesIntegracionAcuseTests
{
    private readonly IntegracionesQueryContextFalso _integraciones = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly Empresa _propia = Empresa.CrearComoCliente("Montajes Springfield", "B12345674", false, null, null);
    private readonly Empresa _ajena = Empresa.CrearComoCliente("Obras Capilares", "B12345674", false, null, null);

    public ObtenerConexionesIntegracionAcuseTests()
    {
        _empresas.ListaEmpresas.Add(_propia);
        _empresas.ListaEmpresas.Add(_ajena);
    }

    private ConexionIntegracion Buzon(string email, Guid? empresaId = null)
    {
        var conexion = new ConexionIntegracion(email, "Buzón " + email, empresaId);
        _integraciones.ConexionesLista.Add(conexion);
        return conexion;
    }

    private Task<IReadOnlyList<ConexionIntegracionListaDto>> ListarAsync(AlcanceDatosServiceFalso? alcance = null) =>
        new ObtenerConexionesIntegracionQueryHandler(
                _integraciones, _empresas, alcance ?? new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid()),
                new Seleccion())
            .Handle(new ObtenerConexionesIntegracionQuery(), CancellationToken.None);

    [Fact]
    public async Task El_acuse_nace_desactivado_y_el_listado_refleja_lo_guardado()
    {
        var activo = Buzon("activo@operador.test", _propia.Id);
        var apagado = Buzon("apagado@operador.test", _propia.Id);
        apagado.AcuseAutomaticoActivo.Should().BeFalse("control: nace desactivado");
        activo.FijarAcuseAutomatico(true);

        var listado = await ListarAsync();

        listado.Should().HaveCount(2, "control: el listado trae los dos buzones");
        listado.ToDictionary(c => c.Id, c => c.AcuseAutomaticoActivo).Should().BeEquivalentTo(
            new Dictionary<Guid, bool> { [activo.Id] = true, [apagado.Id] = false });
    }

    [Fact]
    public async Task Un_buzon_de_una_Empresa_fuera_del_alcance_no_se_lista_aunque_tenga_el_acuse_activo()
    {
        var visible = Buzon("visible@operador.test", _propia.Id);
        var ajeno = Buzon("ajeno@operador.test", _ajena.Id);
        ajeno.FijarAcuseAutomatico(true);

        var listado = await ListarAsync(new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_propia.Id]));

        listado.Select(c => c.Id).Should().Equal(visible.Id);
        listado.Should().NotContain(c => c.Id == ajeno.Id);
    }

    private sealed class Seleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
