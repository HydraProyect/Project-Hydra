using System.Net;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Integraciones.ConectoresPlataformaCae.Dokify;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// La puerta (<see cref="OperacionesConectorPlataformaCae"/>) con el conector dokify real y con todo lo demás en regla:
/// el alta y la subida, que dokify no hace, se deniegan como <see cref="MotivoUsoConector.SinConector"/> sin abrir un uso
/// con la clave de idempotencia, igual que una plataforma sin conector. La lectura, como control, sí pasa la puerta. Vive
/// aquí y no en Application.Tests porque el conector es <c>internal</c> de Infrastructure; los falsos son los de allí.
/// </summary>
public class PuertaConConectorDokifyTests
{
    private const string Clave = "6f1d3c2a-7b4e-4f0a-9c55-2a1e8d3b7c10";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ConexionPlataformaCaeRepositorioFalso _conexiones = new();
    private readonly ProveedorPlataformaCaeRepositorioFalso _proveedores = new();
    private readonly FileStorageServiceFalso _archivos = new();
    private readonly ConexionPlataformaCae _conexion;
    private readonly Guid _acreditacion = Guid.NewGuid();
    private readonly Guid _trabajador = Guid.NewGuid();
    private int _peticionesHttp;

    public PuertaConConectorDokifyTests()
    {
        var proveedor = new ProveedorPlataformaCae(ConectorDokify.Codigo, "dokify");
        proveedor.ActivarConectorApi();
        _proveedores.Agregar(proveedor);

        _conexion = DelTenant(new ConexionPlataformaCae(proveedor.Id, "Acuerdo"), _tenant);
        _conexiones.Agregar(_conexion);
        _conexiones.AgregarCredencial(DelTenant(new CredencialConexionPlataformaCae(_conexion.Id, "token-de-prueba", DateTime.UtcNow), _tenant));
        _conexiones.Habilitacion = new HabilitacionComercialConector(true, EstadoComercialTenant.Activa);

        var archivo = _archivos.GuardarAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "doc.pdf").Result;
        _conexiones.Acreditaciones.Add(new AcreditacionParaConector(_acreditacion, _tenant, Guid.NewGuid(), proveedor.Id, archivo));
        _conexiones.Trabajadores.Add(new TrabajadorParaConector(_trabajador, _tenant, "Nombre", "Apellido", "TEST0001A"));
    }

    private static T DelTenant<T>(T entidad, Guid tenant) where T : EntidadConTenant
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);
        return entidad;
    }

    private OperacionesConectorPlataformaCae Crear()
    {
        var http = new HttpClient(new HandlerQueCuenta(() => Interlocked.Increment(ref _peticionesHttp)));
        var conector = new ConectorDokify(new DokifyClient(http, Options.Create(new DokifyOptions()), NullLogger<DokifyClient>.Instance));
        return new OperacionesConectorPlataformaCae(
            _conexiones, _proveedores, [conector], new SinSesionPrivilegiada(), new SeleccionDelTenant(_tenant), new TenantFijo(_tenant),
            new CurrentUserServiceFalso(_usuario, "GestorCae"), _archivos, new UnitOfWorkFalso(),
            NullLogger<OperacionesConectorPlataformaCae>.Instance);
    }

    [Fact]
    public async Task El_alta_de_Trabajador_con_dokify_se_deniega_sin_conector_y_sin_iniciar_un_uso()
    {
        var resultado = await Crear().DarDeAltaTrabajadorAsync(_conexion.Id, _trabajador, Clave);

        DebeDenegarSinConector(resultado, OperacionConectorPlataformaCae.DarDeAltaTrabajador);
    }

    [Fact]
    public async Task La_subida_de_documento_con_dokify_se_deniega_sin_conector_y_sin_iniciar_un_uso()
    {
        var resultado = await Crear().SubirDocumentoAsync(_conexion.Id, _acreditacion, Clave);

        DebeDenegarSinConector(resultado, OperacionConectorPlataformaCae.SubirDocumento);
    }

    [Fact]
    public async Task Control_la_lectura_con_dokify_si_pasa_la_puerta_y_abre_su_uso()
    {
        var resultado = await Crear().LeerEstadoDocumentoAsync(_conexion.Id, _acreditacion);

        resultado.EsExitoso.Should().BeFalse("el conector aún no sabe leer el estado con los Id de TALVEG");
        var uso = _conexiones.Usos.Should().ContainSingle().Subject;
        uso.Resultado.Should().NotBe(ResultadoUsoConector.Denegado, "con todo en regla la puerta llega al conector");
        uso.Motivo.Should().Be(MotivoUsoConector.DatosInsuficientes);
        _peticionesHttp.Should().Be(0);
    }

    private void DebeDenegarSinConector(Result resultado, OperacionConectorPlataformaCae operacion)
    {
        resultado.EsExitoso.Should().BeFalse();
        var uso = _conexiones.Usos.Should().ContainSingle().Subject;
        uso.Operacion.Should().Be(operacion);
        uso.Resultado.Should().Be(ResultadoUsoConector.Denegado);
        uso.Motivo.Should().Be(MotivoUsoConector.SinConector);
        uso.ClaveIdempotencia.Should().BeNull("una denegación no registra una clave de idempotencia que nunca salió");
        _peticionesHttp.Should().Be(0);
    }

    private sealed class HandlerQueCuenta(Action contar) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            contar();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SeleccionDelTenant(Guid tenant) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class TenantFijo(Guid tenant) : ITenantActual
    {
        public Guid? TenantId => tenant;
    }
}
