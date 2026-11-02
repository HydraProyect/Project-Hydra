using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Solo con clientes falsos: ninguna prueba de este fichero envía un correo real.</summary>
public class AcuseAutomaticoBuzonServiceTests
{
    private const string Destinatario = "persona@ejemplo.test";

    private readonly ConexionIntegracion _conexion = new("cae@ejemplo.test", "Buzón CAE");
    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly CredencialIntegracionRepositorioFalso _credenciales = new();
    private readonly Microsoft365GraphClientFalso _graph = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly DescarteCambiosPendientesFalso _descarte = new();
    private readonly RegistroDeAvisos<AcuseAutomaticoBuzonService> _log = new();

    public AcuseAutomaticoBuzonServiceTests()
    {
        _credenciales.Agregar(new CredencialIntegracion(_conexion.Id, "refresh"));
    }

    private AcuseAutomaticoBuzonService Servicio(RegistroExportacionFalso? registro = null, UnitOfWorkFalso? unitOfWork = null) =>
        new(_conversaciones, _graph, new AccesoGraphService(_credenciales, _graph), registro ?? new RegistroExportacionFalso(),
            unitOfWork ?? _unitOfWork, _descarte, _log);

    private AcusePendiente Pendiente(string mensajeExternoId = "msg-1", string hilo = "hilo-1", string remitente = Destinatario)
    {
        var conversacion = new Conversacion("Asunto del correo");
        conversacion.AsociarConexion(_conexion.Id, hilo);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, remitente, "Cuerpo del correo", DateTime.UtcNow);
        _conversaciones.Agregar(conversacion);
        return new AcusePendiente(_conexion, conversacion, mensajeExternoId, remitente);
    }

    private static Mensaje MensajeDelAcuse(AcusePendiente acuse) =>
        acuse.Conversacion.Mensajes.Single(m => m.Direccion == DireccionMensaje.Saliente);

    [Fact]
    public async Task El_acuse_sale_como_respuesta_al_correo_con_la_plantilla_y_queda_como_automatico()
    {
        var acuse = Pendiente();

        await Servicio().EnviarAsync([acuse], CancellationToken.None);

        _graph.VecesEnviado.Should().Be(1);
        _graph.UltimoMensajeExternoIdRespondido.Should().Be("msg-1");
        _graph.UltimoCuerpoRespondido.Should().Be(PlantillaAcuseBuzon.CuerpoHtml);
        var mensaje = MensajeDelAcuse(acuse);
        mensaje.EsAutomatico.Should().BeTrue();
        mensaje.Remitente.Should().Be("cae@ejemplo.test");
        mensaje.Canal.Should().Be(CanalConversacion.Correo);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task El_registro_de_salida_va_antes_del_envio_y_enmascara_la_direccion()
    {
        var enviadosAlRegistrar = -1;
        var registro = new RegistroExportacionFalso { AlRegistrar = () => enviadosAlRegistrar = _graph.VecesEnviado };

        await Servicio(registro).EnviarAsync([Pendiente()], CancellationToken.None);

        enviadosAlRegistrar.Should().Be(0);
        var fila = registro.Filas.Should().ContainSingle().Subject;
        fila.Criterios["origen"].Should().Be(nameof(OrigenSalidaComunicacion.AcuseAutomatico));
        fila.Criterios["conexionId"].Should().Be(_conexion.Id.ToString());
        fila.Criterios["destinatarios"].Should().Be("p***@ejemplo.test");
    }

    [Fact]
    public async Task Si_el_registro_de_salida_falla_no_sale_nada_y_el_fallo_no_vuelve_a_la_ingesta()
    {
        var acuse = Pendiente();

        await Servicio(new RegistroExportacionFalso { Falla = true }).EnviarAsync([acuse], CancellationToken.None);

        _graph.VecesEnviado.Should().Be(0);
        acuse.Conversacion.Mensajes.Should().ContainSingle();
        _descarte.VecesDescartado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_token_no_sale_nada_y_el_fallo_no_vuelve_a_la_ingesta()
    {
        _graph.FallaRefresco = true;
        var acuse = Pendiente();

        await Servicio().EnviarAsync([acuse], CancellationToken.None);

        _graph.VecesEnviado.Should().Be(0);
        acuse.Conversacion.Mensajes.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_envio_fallido_no_deja_mensaje_y_guarda_el_token_renovado()
    {
        _graph.FallaEnvio = true;
        var acuse = Pendiente();

        await Servicio().EnviarAsync([acuse], CancellationToken.None);

        acuse.Conversacion.Mensajes.Should().ContainSingle();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Un_guardado_fallido_no_vuelve_a_la_ingesta_y_abandona_el_resto_del_lote()
    {
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("base no disponible") };

        var envio = () => Servicio(unitOfWork: unitOfWork).EnviarAsync(
            [Pendiente(), Pendiente("msg-2", "hilo-2", "otra@ejemplo.test")], CancellationToken.None);

        await envio.Should().NotThrowAsync();
        _graph.VecesEnviado.Should().Be(1);
        _descarte.VecesDescartado.Should().Be(1);
    }

    [Fact]
    public async Task El_apagado_de_la_aplicacion_se_propaga_sin_contarse_como_fallo()
    {
        using var parada = new CancellationTokenSource();
        var registro = new RegistroExportacionFalso { AlRegistrar = () => throw new OperationCanceledException(parada.Token) };
        await parada.CancelAsync();

        var envio = () => Servicio(registro).EnviarAsync([Pendiente()], parada.Token);

        await envio.Should().ThrowAsync<OperationCanceledException>();
        _descarte.VecesDescartado.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-23, false)]
    [InlineData(-25, true)]
    public async Task Un_remitente_recibe_como_mucho_un_acuse_por_buzon_en_el_intervalo(int horasDesdeElAnterior, bool sale)
    {
        var anterior = Pendiente("msg-0", "hilo-0");
        anterior.Conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, CanalConversacion.Correo, _conexion.BuzonEmail, PlantillaAcuseBuzon.CuerpoHtml,
            DateTime.UtcNow.AddHours(horasDesdeElAnterior), esAutomatico: true);

        await Servicio().EnviarAsync([Pendiente()], CancellationToken.None);

        _graph.VecesEnviado.Should().Be(sale ? 1 : 0);
    }

    [Fact]
    public async Task El_tope_no_distingue_mayusculas_ni_cuenta_las_respuestas_de_una_persona()
    {
        var conAcuse = Pendiente("msg-0", "hilo-0");
        conAcuse.Conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, CanalConversacion.Correo, _conexion.BuzonEmail, "<p>Una respuesta</p>", DateTime.UtcNow);

        await Servicio().EnviarAsync([Pendiente()], CancellationToken.None);
        _graph.VecesEnviado.Should().Be(1);

        await Servicio().EnviarAsync([Pendiente("msg-2", "hilo-2", "PERSONA@ejemplo.test")], CancellationToken.None);
        _graph.VecesEnviado.Should().Be(1);
    }

    [Fact]
    public async Task Dos_conversaciones_nuevas_del_mismo_remitente_en_un_lote_llevan_un_acuse()
    {
        await Servicio().EnviarAsync(
            [Pendiente(), Pendiente("msg-2", "hilo-2")], CancellationToken.None);

        _graph.VecesEnviado.Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(RegistroExportacionFalso.Falla))]
    [InlineData(nameof(Microsoft365GraphClientFalso.FallaEnvio))]
    [InlineData(nameof(Microsoft365GraphClientFalso.FallaRefresco))]
    public async Task Ningun_registro_lleva_la_direccion_ni_el_asunto_ni_el_cuerpo(string fallo)
    {
        _graph.FallaEnvio = fallo == nameof(Microsoft365GraphClientFalso.FallaEnvio);
        _graph.FallaRefresco = fallo == nameof(Microsoft365GraphClientFalso.FallaRefresco);
        var registro = new RegistroExportacionFalso { Falla = fallo == nameof(RegistroExportacionFalso.Falla) };

        await Servicio(registro).EnviarAsync([Pendiente()], CancellationToken.None);

        _log.Entradas.Should().NotBeEmpty();
        _log.Entradas.Should().AllSatisfy(e =>
        {
            e.Texto.Should().NotContain(Destinatario);
            e.Texto.Should().NotContain("Asunto del correo");
            e.Texto.Should().NotContain("Cuerpo del correo");
            e.Texto.Should().NotContain("Registro no disponible");
        });
    }

    [Fact]
    public void La_plantilla_es_fija_y_no_lleva_huecos_ni_datos_del_correo()
    {
        PlantillaAcuseBuzon.EsBorrador.Should().BeTrue("Chris no ha aprobado el texto definitivo");
        PlantillaAcuseBuzon.CuerpoHtml.Should().NotContainAny("{", "}", "@");
    }
}
