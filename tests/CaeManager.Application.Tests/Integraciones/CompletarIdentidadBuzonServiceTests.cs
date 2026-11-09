using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class CompletarIdentidadBuzonServiceTests
{
    private const string Directorio = "11111111-2222-3333-4444-555555555555";

    private sealed class Escenario
    {
        public IntegracionesQueryContextFalso QueryContext { get; } = new();
        public ConexionIntegracionRepositorioFalso Conexiones { get; } = new();
        public ReclamacionBuzonIntegracionRepositorioFalso Reclamaciones { get; } = new();
        public EventoWebhookRepositorioFalso Eventos { get; } = new();
        public ConversacionRepositorioFalso Conversaciones { get; } = new();
        public Microsoft365GraphClientFalso Graph { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        /// <summary>Descartar suelta el lote fallido, como el contexto real: el siguiente guardado ya no lo arrastra.</summary>
        public DescarteQueSueltaElLote Descarte { get; }

        public Escenario() => Descarte = new DescarteQueSueltaElLote(UnitOfWork);

        public ConexionIntegracion AgregarCompartida(string direccion)
        {
            var conexion = ConexionIntegracion.CrearBuzonCompartido(direccion, "Buzón CAE", Directorio, Guid.NewGuid());
            QueryContext.ConexionesLista.Add(conexion);
            Conexiones.Agregar(conexion);
            Reclamaciones.Reclamar(new ReclamacionBuzonIntegracion(direccion, null, Guid.NewGuid(), conexion.Id, DateTime.UtcNow));
            return conexion;
        }

        /// <summary>Conexión con identidad ya fijada y verificada hace <paramref name="antiguedad"/>.</summary>
        public ConexionIntegracion AgregarVerificada(string direccion, string identidad, TimeSpan antiguedad)
        {
            var conexion = AgregarCompartida(direccion);
            conexion.FijarIdentidadBuzon(identidad);
            conexion.MarcarIdentidadVerificada(DateTime.UtcNow - antiguedad);
            Graph.IdentidadesPorDireccion[direccion] = identidad;
            return conexion;
        }

        public EventoWebhook AgregarRetenido(ConexionIntegracion conexion)
        {
            var evento = new EventoWebhook(conexion.Id, "{}");
            evento.EsperarVerificacion("en espera", DateTime.UtcNow + EventoWebhook.TopeEsperaVerificacion + TimeSpan.FromMinutes(1));
            Eventos.Agregar(evento);
            return evento;
        }

        public CompletarIdentidadBuzonService Servicio() => new(
            QueryContext, Conexiones, Reclamaciones, Eventos,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), Graph), Graph,
            UnitOfWork,
            Descarte,
            NullLogger<CompletarIdentidadBuzonService>.Instance);
    }

    public sealed class DescarteQueSueltaElLote(UnitOfWorkFalso unitOfWork) : CaeManager.Application.Common.IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes()
        {
            Veces++;
            unitOfWork.ExcepcionAlGuardar = null;
        }
    }

    private static MensajeResumenGraphDto MensajeRecibido(string id) =>
        new(id, "hilo-" + id, "Asunto", "remitente@tenant.com", DateTime.UtcNow.AddMinutes(1), false);

    [Fact]
    public async Task Completa_la_identidad_de_una_conexion_antigua_y_de_su_reclamacion_y_la_marca_verificada()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        escenario.Graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-cae";

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().Equal(conexion.Id);
        conexion.IdentidadBuzon.Should().Be("inbox-cae");
        conexion.IdentidadVerificadaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeTrue();
        escenario.Reclamaciones.Reclamaciones.Should().ContainSingle().Which.IdentidadBuzon.Should().Be("inbox-cae");
        escenario.Reclamaciones.VecesGuardado.Should().Be(1);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);

        // El compartido se lee con el token de aplicación de su directorio, no con el de una persona.
        escenario.Graph.TokensDeIdentidad.Should().Equal("token-aplicacion-falso");
    }

    /// <summary>
    /// La identidad choca con la reclamación de otro Tenant: el mismo buzón está conectado allí con otra dirección
    /// (un alias). Esta conexión deja de ingerir; la del otro Tenant no se toca.
    /// </summary>
    [Fact]
    public async Task Si_la_identidad_ya_esta_reclamada_por_otra_direccion_la_conexion_pasa_a_ConError()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("alias@operador.test");
        escenario.Reclamaciones.BuzonYaReclamado = true;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty("la renovación no debe rehabilitarla");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonDuplicado);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>Un fallo transitorio no dice nada del buzón: la conexión no cambia y no se fija ninguna identidad.</summary>
    [Fact]
    public async Task Una_lectura_transitoria_fallida_no_cambia_una_conexion_sin_identidad()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear("Integraciones.Microsoft365.TiempoAgotado", "timeout");

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.IdentidadBuzon.Should().BeNull();
        conexion.IdentidadVerificadaUtc.Should().BeNull();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
        escenario.Reclamaciones.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("Integraciones.Microsoft365.TiempoAgotado")]
    [InlineData("Integraciones.Microsoft365.ErrorApi")]
    [InlineData("Integraciones.Microsoft365.Limitado")]
    public async Task Una_lectura_transitoria_fallida_conserva_la_verificacion_anterior_y_el_estado(string codigo)
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(20));
        var verificadaAntes = conexion.IdentidadVerificadaUtc;
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear(codigo, "fallo");

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.IdentidadVerificadaUtc.Should().Be(verificadaAntes);
        conexion.IdentidadBuzon.Should().Be("inbox-cae");
    }

    [Fact]
    public async Task Sin_token_no_cambia_nada_ni_guarda()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(20));
        escenario.Graph.FallaTokenAplicacion = true;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>403/404: Microsoft dice que el buzón no se puede abrir. Es definitivo y la conexión deja de operar.</summary>
    [Fact]
    public async Task Sin_acceso_al_buzon_pasa_a_ConError()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(20));
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "403");

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        conexion.IdentidadBuzon.Should().Be("inbox-cae");
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>Sin acceso no tapa el motivo más concreto de una conexión que ya estaba en ConError.</summary>
    [Fact]
    public async Task Sin_acceso_no_cambia_el_motivo_de_buzon_cambiado()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "403");

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
    }

    [Fact]
    public async Task Una_conexion_que_lanza_no_deja_sin_comprobar_a_las_siguientes()
    {
        var escenario = new Escenario();
        escenario.AgregarCompartida("roto@operador.test");
        var sana = escenario.AgregarCompartida("sano@operador.test");
        escenario.Graph.DireccionesQueLanzanEnIdentidad.Add("roto@operador.test");

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEquivalentTo([sana.Id]);
    }

    /// <summary>
    /// Verificada en este ciclo, una conexión en ConError se rehabilita aquí mismo (no espera a la ventana de
    /// renovación) y sus eventos retenidos vuelven a la cola. No lee Graph: el hueco que dejó la caída queda en la marca,
    /// guardada en la misma transacción, para la lectura con reintentos.
    /// </summary>
    [Fact]
    public async Task Una_conexion_en_ConError_con_la_misma_identidad_se_rehabilita_libera_sus_retenidos_y_conserva_la_marca_sin_leer_Graph()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarCompartida("cae@operador.test");
        conexion.FijarIdentidadBuzon("inbox-cae");
        var caida = DateTime.UtcNow.AddHours(-3);
        conexion.MarcarConError("fallo de un ciclo anterior", caida);
        escenario.Graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-cae";
        var retenido = escenario.AgregarRetenido(conexion);
        escenario.Graph.MensajesADevolver = [MensajeRecibido("m-2"), MensajeRecibido("m-1")];
        var marcaGuardada = (DateTime?)null;
        escenario.UnitOfWork.AlGuardar = () => marcaGuardada = conexion.HuecoPendienteDesdeUtc;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().Equal(conexion.Id);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
        conexion.IdentidadVerificadaUtc.Should().NotBeNull();
        retenido.Estado.Should().Be(EstadoEventoWebhook.Pendiente);
        escenario.Graph.ListadosPedidos.Should().BeEmpty("al rehabilitar no se lee Graph en línea");
        escenario.Eventos.Eventos.Should().Equal(retenido);
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
        marcaGuardada.Should().Be(caida, "la marca sigue en la misma transacción que la rehabilitación");
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>Ya verificada y Habilitada, la ingesta no descartó nada: no hay hueco que leer.</summary>
    [Fact]
    public async Task Una_conexion_ya_verificada_no_lee_la_bandeja_de_entrada()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(20));
        escenario.Graph.MensajesADevolver = [MensajeRecibido("m-1")];

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().Equal(conexion.Id);
        conexion.IdentidadVerificadaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        escenario.Graph.ListadosPedidos.Should().BeEmpty();
        escenario.Eventos.Eventos.Should().BeEmpty();
    }

    /// <summary>
    /// Habilitada pero con la verificación caducada: el ciclo fija la marca en el momento exacto de la caducidad, no en
    /// el del ciclo, y al verificar no lee Graph.
    /// </summary>
    [Fact]
    public async Task Una_conexion_con_la_verificacion_caducada_fija_la_marca_en_la_caducidad_y_no_lee_Graph()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada(
            "cae@operador.test", "inbox-cae", ConexionIntegracion.VentanaVerificacionIdentidad + TimeSpan.FromHours(1));
        var caducidad = conexion.IdentidadVerificadaUtc!.Value + ConexionIntegracion.VentanaVerificacionIdentidad;
        escenario.Graph.MensajesADevolver = [MensajeRecibido("m-1")];

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().Be(caducidad);
        escenario.Graph.ListadosPedidos.Should().BeEmpty();
        escenario.Eventos.Eventos.Should().BeEmpty();
    }

    /// <summary>La caducidad se guarda aunque el resto del ciclo falle de forma transitoria (sin token).</summary>
    [Fact]
    public async Task La_marca_de_caducidad_se_guarda_aunque_falle_el_token()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada(
            "cae@operador.test", "inbox-cae", ConexionIntegracion.VentanaVerificacionIdentidad + TimeSpan.FromHours(5));
        var caducidad = conexion.IdentidadVerificadaUtc!.Value + ConexionIntegracion.VentanaVerificacionIdentidad;
        escenario.Graph.FallaTokenAplicacion = true;

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.HuecoPendienteDesdeUtc.Should().Be(caducidad);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>
    /// Un guardado fallido en una conexión suelta su lote antes de pasar a la siguiente: sin el descarte, el guardado
    /// de la siguiente arrastraría (y volvería a fallar por) la mutación ajena.
    /// </summary>
    [Fact]
    public async Task Tras_un_guardado_fallido_descarta_el_lote_y_la_siguiente_conexion_se_verifica()
    {
        var escenario = new Escenario();
        escenario.AgregarVerificada("cambiado@operador.test", "inbox-original", TimeSpan.FromHours(20));
        escenario.Graph.IdentidadesPorDireccion["cambiado@operador.test"] = "inbox-de-otro-buzon";
        var sana = escenario.AgregarVerificada("sano@operador.test", "inbox-sano", TimeSpan.FromHours(20));
        escenario.UnitOfWork.ExcepcionAlGuardar = new InvalidOperationException("guardado rechazado");

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        escenario.Descarte.Veces.Should().Be(1);
        verificadas.Should().BeEquivalentTo([sana.Id]);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>El directorio reasignó la dirección a otro buzón: la conexión deja de operar y no sale verificada.</summary>
    [Fact]
    public async Task Una_direccion_que_responde_con_otro_buzon_pasa_a_ConError()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-original", TimeSpan.FromHours(20));
        var verificadaAntes = conexion.IdentidadVerificadaUtc;
        escenario.Graph.IdentidadesPorDireccion["cae@operador.test"] = "inbox-de-otro-buzon";

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeBuzonCambiado);
        conexion.IdentidadBuzon.Should().Be("inbox-original");
        conexion.IdentidadVerificadaUtc.Should().Be(verificadaAntes);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task No_mira_las_conexiones_deshabilitadas()
    {
        var escenario = new Escenario();
        var deshabilitada = escenario.AgregarCompartida("off@operador.test");
        deshabilitada.Deshabilitar();

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        deshabilitada.IdentidadBuzon.Should().BeNull();
    }

    [Theory]
    [InlineData(CompletarIdentidadBuzonService.MensajeBuzonCambiado)]
    [InlineData(CompletarIdentidadBuzonService.MensajeBuzonDuplicado)]
    [InlineData(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon)]
    public void Un_fallo_de_renovacion_no_sobrescribe_el_motivo_de_identidad(string motivo)
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.MarcarConError(motivo);

        CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(conexion, "No se pudo renovar la suscripción.");

        conexion.UltimoError.Should().Be(motivo);
    }

    [Fact]
    public void Un_fallo_de_renovacion_marca_ConError_una_conexion_sin_motivo_de_identidad()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.MarcarConError("motivo anterior cualquiera");

        CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(conexion, "No se pudo renovar la suscripción.");

        conexion.UltimoError.Should().Be("No se pudo renovar la suscripción.");
    }
}
