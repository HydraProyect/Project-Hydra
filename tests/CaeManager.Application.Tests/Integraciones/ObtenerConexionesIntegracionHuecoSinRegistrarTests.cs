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
/// La proyección <see cref="ConexionIntegracionListaDto.HuecoSinRegistrar"/> de
/// <see cref="ObtenerConexionesIntegracionQueryHandler"/>: true solo cuando la conexión tiene puesta
/// <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/>. No mira el estado: una Deshabilitada sin la marca
/// (desconexión limpia) no avisa, y una Habilitada no puede tenerla porque <see cref="ConexionIntegracion.Rehabilitar"/>
/// la registra antes de volver a Habilitada. Conexiones usa este valor para el aviso «Hueco sin registrar» y el botón
/// Reparar de la fila Deshabilitada.
/// </summary>
public class ObtenerConexionesIntegracionHuecoSinRegistrarTests
{
    private readonly IntegracionesQueryContextFalso _integraciones = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly Empresa _cliente = Empresa.CrearComoCliente("Montajes Springfield", "B12345674", false, null, null);

    public ObtenerConexionesIntegracionHuecoSinRegistrarTests() => _empresas.ListaEmpresas.Add(_cliente);

    private ConexionIntegracion Buzon(string email)
    {
        var conexion = new ConexionIntegracion(email, "Buzón " + email, _cliente.Id);
        _integraciones.ConexionesLista.Add(conexion);
        return conexion;
    }

    private Task<IReadOnlyList<ConexionIntegracionListaDto>> ListarAsync(Guid? sesionPrivilegiadaId = null) =>
        new ObtenerConexionesIntegracionQueryHandler(
                _integraciones, _empresas, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid()),
                new Seleccion(sesionPrivilegiadaId))
            .Handle(new ObtenerConexionesIntegracionQuery(), CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Avisa_del_hueco_sin_registrar_solo_en_la_conexion_que_tiene_la_marca(bool enSesionPrivilegiada)
    {
        var conMarca = Buzon("marcada@operador.test");
        conMarca.Deshabilitar(DateTime.UtcNow.AddMinutes(-5));
        var deshabilitadaSinMarca = Buzon("limpia@operador.test");
        deshabilitadaSinMarca.Deshabilitar();
        var habilitada = Buzon("activa@operador.test");
        conMarca.MarcaHuecoPorRegistrarDesdeUtc.Should().NotBeNull("control: la marca está puesta");
        deshabilitadaSinMarca.MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull("control");

        var listado = await ListarAsync(enSesionPrivilegiada ? Guid.NewGuid() : null);

        listado.Should().HaveCount(3, "control: el listado trae las tres conexiones");
        listado.ToDictionary(c => c.Id, c => (c.Estado, c.HuecoSinRegistrar)).Should().BeEquivalentTo(
            new Dictionary<Guid, (EstadoConexionIntegracion, bool)>
            {
                [conMarca.Id] = (EstadoConexionIntegracion.Deshabilitada, true),
                [deshabilitadaSinMarca.Id] = (EstadoConexionIntegracion.Deshabilitada, false),
                [habilitada.Id] = (EstadoConexionIntegracion.Habilitada, false),
            });
    }

    /// <summary>
    /// Reactivar la Deshabilitada con la marca la registra y la vacía (<see cref="ConexionIntegracion.Rehabilitar"/>): la
    /// fila ya no avisa de un hueco sin registrar, sino del correo no recuperado.
    /// </summary>
    [Fact]
    public async Task Tras_reactivar_la_conexion_marcada_deja_de_avisar_del_hueco_sin_registrar()
    {
        var desde = DateTime.UtcNow.AddMinutes(-5);
        var conexion = Buzon("marcada@operador.test");
        conexion.Deshabilitar(desde);
        conexion.Rehabilitar();

        var fila = (await ListarAsync()).Should().ContainSingle().Subject;

        fila.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        fila.HuecoSinRegistrar.Should().BeFalse();
        fila.CorreoNoRecuperadoDesdeUtc.Should().Be(desde, "control: la marca se registró al reactivar");
    }

    private sealed class Seleccion(Guid? sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
