using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// La puerta única hacia un conector (OWASP ASVS 4.0.3 V4.1.5, V4.2.1, V7.2.2): cada condición,
/// por separado y sin ninguna otra rota, deniega sin llamar y deja rastro.
/// </summary>
public class OperacionesConectorPlataformaCaeTests
{
    private const string Secreto = "secreto-de-api-que-no-debe-aparecer";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ConexionPlataformaCaeRepositorioFalso _conexiones = new();
    private readonly ProveedorPlataformaCaeRepositorioFalso _proveedores = new();
    private readonly ConectorPlataformaCaeFalso _conector = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly FileStorageServiceFalso _archivos = new();
    private readonly LoggerQueGuarda _logger = new();
    private readonly ProveedorPlataformaCae _proveedor;
    private readonly ConexionPlataformaCae _conexion;
    private readonly Guid _acreditacion = Guid.NewGuid();
    private readonly Guid _trabajador = Guid.NewGuid();
    private readonly string _archivo;

    private SesionPrivilegiadaActiva? _sesionPrivilegiada;
    private Guid? _tenantActual;

    public OperacionesConectorPlataformaCaeTests()
    {
        _tenantActual = _tenant;

        _proveedor = new ProveedorPlataformaCae(ConectorPlataformaCaeFalso.Codigo, "Plataforma de prueba");
        _proveedor.ActivarConectorApi();
        _proveedores.Agregar(_proveedor);

        _conexion = DelTenant(new ConexionPlataformaCae(_proveedor.Id, "Acuerdo"), _tenant);
        _conexiones.Agregar(_conexion);
        _conexiones.AgregarCredencial(DelTenant(new CredencialConexionPlataformaCae(_conexion.Id, Secreto, DateTime.UtcNow), _tenant));
        _conexiones.Habilitacion = new HabilitacionComercialConector(true, EstadoComercialTenant.Activa);

        _archivo = _archivos.GuardarAsync(new MemoryStream(Encoding.UTF8.GetBytes("pdf")), "doc.pdf").Result;
        _conexiones.Acreditaciones.Add(new AcreditacionParaConector(_acreditacion, _tenant, Guid.NewGuid(), _proveedor.Id, _archivo));
        _conexiones.Trabajadores.Add(new TrabajadorParaConector(_trabajador, _tenant, "Ana", "Pérez", "12345678Z"));
    }

    private static T DelTenant<T>(T entidad, Guid tenant) where T : EntidadConTenant
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);
        return entidad;
    }

    private OperacionesConectorPlataformaCae Crear(params IConectorPlataformaCae[] conectores) => new(
        _conexiones, _proveedores, conectores.Length == 0 ? [_conector] : conectores,
        new SesionFalsa(() => _sesionPrivilegiada), new TenantFalso(() => _tenantActual),
        new CurrentUserServiceFalso(_usuario), _archivos, _unitOfWork, _logger);

    private Task<Result<EstadoDocumentoEnPlataforma>> LeerAsync() =>
        Crear().LeerEstadoDocumentoAsync(_conexion.Id, _acreditacion);

    private void DebeDenegarSinLlamar(Result resultado, MotivoUsoConector motivo)
    {
        resultado.EsExitoso.Should().BeFalse();
        _conector.Llamadas.Should().Be(0);
        var uso = _conexiones.Usos.Should().ContainSingle().Subject;
        uso.Resultado.Should().Be(ResultadoUsoConector.Denegado);
        uso.Motivo.Should().Be(motivo);
        uso.UsuarioId.Should().Be(_usuario);
    }

    [Fact]
    public async Task Con_todo_en_regla_lee_el_estado_con_la_credencial_de_la_conexion_y_deja_el_uso_cerrado()
    {
        var resultado = await LeerAsync();

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(EstadoDocumentoEnPlataforma.Validado);
        _conector.UltimaCredencial.Should().Be(Secreto);
        var uso = _conexiones.Usos.Should().ContainSingle().Subject;
        uso.Resultado.Should().Be(ResultadoUsoConector.Exito);
        uso.Operacion.Should().Be(OperacionConectorPlataformaCae.LeerEstadoDocumento);
        uso.AcreditacionId.Should().Be(_acreditacion);
        uso.FinalizadoEnUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task El_alta_de_Trabajador_pasa_sus_datos_y_la_clave_de_idempotencia()
    {
        var resultado = await Crear().DarDeAltaTrabajadorAsync(_conexion.Id, _trabajador, "clave-1");

        resultado.EsExitoso.Should().BeTrue();
        var solicitud = _conector.UltimaSolicitud.Should().BeOfType<SolicitudAltaTrabajador>().Subject;
        solicitud.Dni.Should().Be("12345678Z");
        solicitud.ClaveIdempotencia.Should().Be("clave-1");
        _conexiones.Usos.Single().TrabajadorId.Should().Be(_trabajador);
    }

    [Fact]
    public async Task La_subida_entrega_el_contenido_del_archivo_de_la_Acreditacion()
    {
        var resultado = await Crear().SubirDocumentoAsync(_conexion.Id, _acreditacion, "clave-2");

        resultado.EsExitoso.Should().BeTrue();
        _conector.UltimoContenido.Should().Equal(Encoding.UTF8.GetBytes("pdf"));
    }

    [Fact]
    public async Task El_uso_ya_esta_guardado_cuando_sale_la_llamada()
    {
        var guardadosAlLlamar = -1;
        ResultadoUsoConector? resultadoAlLlamar = null;
        _conector.AlLlamar = () =>
        {
            guardadosAlLlamar = _unitOfWork.VecesGuardado;
            resultadoAlLlamar = _conexiones.Usos.Single().Resultado;
        };

        await LeerAsync();

        guardadosAlLlamar.Should().Be(1);
        resultadoAlLlamar.Should().Be(ResultadoUsoConector.EnCurso);
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_el_uso_no_sale_la_llamada()
    {
        _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("base caída");

        var leer = LeerAsync;

        await leer.Should().ThrowAsync<InvalidOperationException>();
        _conector.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_un_Actor_de_Plataforma_nunca_opera_con_la_credencial()
    {
        _sesionPrivilegiada = new SesionPrivilegiadaActiva(
            Guid.NewGuid(), Guid.NewGuid(), _tenant, CapacidadPrivilegio.SoporteLectura, null);

        var resultado = await LeerAsync();

        resultado.EsExitoso.Should().BeFalse();
        _conector.Llamadas.Should().Be(0);
        _unitOfWork.VecesGuardado.Should().Be(0, "cae_app_soporte no tiene INSERT sobre los usos");
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning);
    }

    [Fact]
    public async Task Sin_Tenant_resuelto_no_opera()
    {
        _tenantActual = null;

        (await LeerAsync()).EsExitoso.Should().BeFalse();

        _conector.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task La_conexion_de_otro_Tenant_se_deniega_aunque_la_lectura_la_devolviera()
    {
        // El filtro de Tenant y RLS ya la ocultan: esto mide la segunda comprobación, por si un día
        // la lectura pasa por un contexto sin filtro.
        var ajena = DelTenant(new ConexionPlataformaCae(_proveedor.Id, "Ajena"), Guid.NewGuid());
        _conexiones.Agregar(ajena);

        var resultado = await Crear().LeerEstadoDocumentoAsync(ajena.Id, _acreditacion);

        resultado.EsExitoso.Should().BeFalse();
        _conector.Llamadas.Should().Be(0);
        _conexiones.Usos.Should().BeEmpty("no hay fila de uso que colgar de la conexión de otro Tenant");
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Warning);
    }

    [Fact]
    public async Task La_credencial_de_otro_Tenant_no_se_usa()
    {
        _conexiones.Credenciales.Clear();
        _conexiones.AgregarCredencial(DelTenant(new CredencialConexionPlataformaCae(_conexion.Id, "ajeno", DateTime.UtcNow), Guid.NewGuid()));

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.SinCredencial);
    }

    [Fact]
    public async Task El_kill_switch_de_la_API_corta_la_llamada()
    {
        _proveedor.DesactivarConectorApi();

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.PlataformaDesactivada);
    }

    [Fact]
    public async Task El_kill_switch_de_la_plataforma_tambien_corta_la_llamada()
    {
        _proveedor.Desactivar();

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.PlataformaDesactivada);
    }

    [Fact]
    public async Task Sin_el_Add_on_de_subida_automatica_no_opera()
    {
        _conexiones.Habilitacion = new HabilitacionComercialConector(false, EstadoComercialTenant.Activa);

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.AddOnInactivo);
    }

    [Fact]
    public async Task Sin_fila_comercial_del_Tenant_falla_en_cerrado()
    {
        _conexiones.Habilitacion = null;

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.AddOnInactivo);
    }

    [Theory]
    [InlineData(EstadoComercialTenant.SoloLectura)]
    [InlineData(EstadoComercialTenant.Suspendida)]
    public async Task Un_Tenant_restringido_no_opera_aunque_tenga_el_Add_on(EstadoComercialTenant estado)
    {
        _conexiones.Habilitacion = new HabilitacionComercialConector(true, estado);

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.EstadoComercialRestringido);
    }

    [Fact]
    public async Task Sin_conector_registrado_para_la_plataforma_no_opera()
    {
        var resultado = await Crear(new ConectorPlataformaCaeFalso("otra-plataforma")).LeerEstadoDocumentoAsync(_conexion.Id, _acreditacion);

        DebeDenegarSinLlamar(resultado, MotivoUsoConector.SinConector);
    }

    [Fact]
    public async Task Sin_credencial_no_opera()
    {
        _conexiones.Credenciales.Clear();

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.SinCredencial);
    }

    [Fact]
    public async Task La_Acreditacion_de_otro_Tenant_se_deniega()
    {
        _conexiones.Acreditaciones.Clear();
        _conexiones.Acreditaciones.Add(new AcreditacionParaConector(_acreditacion, Guid.NewGuid(), Guid.NewGuid(), _proveedor.Id, _archivo));

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.ObjetoNoEncontrado);
    }

    [Fact]
    public async Task La_Acreditacion_de_otra_plataforma_se_deniega()
    {
        _conexiones.Acreditaciones.Clear();
        _conexiones.Acreditaciones.Add(new AcreditacionParaConector(_acreditacion, _tenant, Guid.NewGuid(), Guid.NewGuid(), _archivo));

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.ObjetoDeOtraPlataforma);
    }

    [Fact]
    public async Task La_Acreditacion_sin_plataforma_identificada_se_deniega()
    {
        _conexiones.Acreditaciones.Clear();
        _conexiones.Acreditaciones.Add(new AcreditacionParaConector(_acreditacion, _tenant, Guid.NewGuid(), null, _archivo));

        DebeDenegarSinLlamar(await LeerAsync(), MotivoUsoConector.ObjetoDeOtraPlataforma);
    }

    [Fact]
    public async Task El_Trabajador_de_otro_Tenant_se_deniega()
    {
        _conexiones.Trabajadores.Clear();
        _conexiones.Trabajadores.Add(new TrabajadorParaConector(_trabajador, Guid.NewGuid(), "Ana", "Pérez", "12345678Z"));

        DebeDenegarSinLlamar(
            await Crear().DarDeAltaTrabajadorAsync(_conexion.Id, _trabajador, "clave"), MotivoUsoConector.ObjetoNoEncontrado);
    }

    [Fact]
    public async Task El_Trabajador_sin_documento_de_identidad_se_deniega()
    {
        _conexiones.Trabajadores.Clear();
        _conexiones.Trabajadores.Add(new TrabajadorParaConector(_trabajador, _tenant, "Ana", "Pérez", null));

        DebeDenegarSinLlamar(
            await Crear().DarDeAltaTrabajadorAsync(_conexion.Id, _trabajador, "clave"), MotivoUsoConector.DatosInsuficientes);
    }

    [Fact]
    public async Task La_subida_sin_archivo_se_deniega()
    {
        _conexiones.Acreditaciones.Clear();
        _conexiones.Acreditaciones.Add(new AcreditacionParaConector(_acreditacion, _tenant, Guid.NewGuid(), _proveedor.Id, null));

        DebeDenegarSinLlamar(
            await Crear().SubirDocumentoAsync(_conexion.Id, _acreditacion, "clave"), MotivoUsoConector.DatosInsuficientes);
    }

    [Fact]
    public async Task Un_fallo_de_la_plataforma_cierra_el_uso_con_su_motivo()
    {
        _conector.Fallo = new MotivoUsoConectorFalso(MotivoUsoConector.CredencialRechazada);

        var resultado = await LeerAsync();

        resultado.EsExitoso.Should().BeFalse();
        var uso = _conexiones.Usos.Single();
        uso.Resultado.Should().Be(ResultadoUsoConector.Fallo);
        uso.Motivo.Should().Be(MotivoUsoConector.CredencialRechazada);
    }

    [Fact]
    public async Task Una_excepcion_del_conector_cierra_el_uso_y_no_lleva_su_mensaje_al_log()
    {
        _conector.Excepcion = new HttpRequestException($"401 en https://plataforma/?clave={Secreto}");

        var resultado = await LeerAsync();

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Mensaje.Should().NotContain(Secreto);
        var uso = _conexiones.Usos.Single();
        uso.Resultado.Should().Be(ResultadoUsoConector.Fallo);
        uso.Motivo.Should().Be(MotivoUsoConector.Inesperado);
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error)
            .Which.Mensaje.Should().Contain(nameof(HttpRequestException));
        _logger.Entradas.Should().OnlyContain(e => !e.Mensaje.Contains(Secreto) && e.Excepcion == null);
    }

    [Fact]
    public async Task Cancelar_durante_la_llamada_cierra_el_uso_y_propaga_la_cancelacion()
    {
        using var cancelacion = new CancellationTokenSource();
        _conector.AlLlamar = cancelacion.Cancel;

        var leer = () => Crear().LeerEstadoDocumentoAsync(_conexion.Id, _acreditacion, cancelacion.Token);

        await leer.Should().ThrowAsync<OperationCanceledException>();
        var uso = _conexiones.Usos.Single();
        uso.Resultado.Should().Be(ResultadoUsoConector.Fallo);
        uso.Motivo.Should().Be(MotivoUsoConector.Cancelado);
    }

    [Fact]
    public void Ni_la_credencial_ni_el_trabajador_se_imprimen()
    {
        new CredencialApiConector(Secreto).ToString().Should().NotContain(Secreto);
        new TrabajadorParaConector(_trabajador, _tenant, "Ana", "Pérez", "12345678Z").ToString().Should().NotContain("12345678Z");
    }

    private sealed class SesionFalsa(Func<SesionPrivilegiadaActiva?> sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion());

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion());
    }

    private sealed class TenantFalso(Func<Guid?> tenant) : ITenantActual
    {
        public Guid? TenantId => tenant();
    }

    private sealed record EntradaLog(LogLevel Nivel, string Mensaje, Exception? Excepcion);

    private sealed class LoggerQueGuarda : ILogger<OperacionesConectorPlataformaCae>
    {
        public List<EntradaLog> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add(new EntradaLog(logLevel, formatter(state, exception), exception));
    }
}
