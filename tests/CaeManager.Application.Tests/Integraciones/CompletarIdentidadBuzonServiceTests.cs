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
        public CerrojoVerificacionBuzonFalso Cerrojo { get; } = new();

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
            Cerrojo,
            new SuscripcionAlRehabilitarService(Suscripciones, Graph, NullLogger<SuscripcionAlRehabilitarService>.Instance),
            NullLogger<CompletarIdentidadBuzonService>.Instance);

        public SuscripcionWebhookRepositorioFalso Suscripciones { get; } = new();
    }

    // Ronda 10, hallazgo alto: el ciclo no rehabilita una ConError sin suscripción vigente.

    [Fact]
    public async Task ConErrorVerificada_SinSuscripcionLocal_LaCreaYRehabilita()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@tenant.com", "inbox-1", TimeSpan.FromDays(3));
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        var retenido = escenario.AgregarRetenido(conexion);

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        escenario.Suscripciones.Suscripciones.Should().ContainSingle(s => s.ConexionIntegracionId == conexion.Id);
        retenido.Estado.Should().NotBe(EstadoEventoWebhook.Retenido);
    }

    [Fact]
    public async Task ConErrorVerificada_SuscripcionRenovable_LaRenuevaSinCrearOtra()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@tenant.com", "inbox-1", TimeSpan.FromDays(3));
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        escenario.Suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddHours(5)));

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        escenario.Graph.SuscripcionesRenovadas.Should().Contain("sub-1");
        escenario.Graph.VecesSuscrito.Should().Be(0);
    }

    [Fact]
    public async Task ConErrorVerificada_SuscripcionPerdida_LaRecreaConElMismoClientState()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@tenant.com", "inbox-1", TimeSpan.FromDays(3));
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        escenario.Suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado-propio", DateTime.UtcNow.AddHours(-5)));
        escenario.Graph.ErroresRenovacion["sub-1"] = Error.Crear("Integraciones.Microsoft365.ErrorRenovacion", "no existe");

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        escenario.Graph.ClientStatesSuscritos.Should().Equal("estado-propio");
        escenario.Suscripciones.Suscripciones.Single().GraphSubscriptionId.Should().Be("suscripcion-falsa");
    }

    [Fact]
    public async Task ConErrorVerificada_CreacionTransitoria_SigueEnConErrorYRetenidosEsperan()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@tenant.com", "inbox-1", TimeSpan.FromDays(3));
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        var retenido = escenario.AgregarRetenido(conexion);
        escenario.Graph.ErrorCreacionSuscripcion = Error.Crear(CodigosErrorMicrosoft365.Transitorio, "503");

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        retenido.Estado.Should().Be(EstadoEventoWebhook.Retenido);
        escenario.Suscripciones.Suscripciones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConErrorVerificada_CreacionDefinitiva_SigueEnConErrorConElMotivoDeLaSuscripcion()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@tenant.com", "inbox-1", TimeSpan.FromDays(3));
        conexion.MarcarConError(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon);
        escenario.Graph.FallaCreacionSuscripcion = true;

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be("fallo simulado");
    }

    // Ronda 10, guarda transitoria: el ciclo saca de la guarda las heredadas de conexiones terminales.

    [Fact]
    public async Task Barrido_HeredadaDeConexionConErrorODesconectada_SaleDeLaGuarda()
    {
        var escenario = new Escenario();
        var enError = ConexionIntegracion.CrearBuzonCompartido("error@tenant.com", "Error", Directorio, Guid.NewGuid());
        escenario.Conexiones.Agregar(enError);
        enError.MarcarConError("caída");
        var desconectada = ConexionIntegracion.CrearBuzonCompartido("baja@tenant.com", "Baja", Directorio, Guid.NewGuid());
        escenario.Conexiones.Agregar(desconectada);
        desconectada.Deshabilitar();
        var habilitada = ConexionIntegracion.CrearBuzonCompartido("viva@tenant.com", "Viva", Directorio, Guid.NewGuid());
        escenario.Conexiones.Agregar(habilitada);
        var deError = escenario.Reclamaciones.AgregarHeredada("error@tenant.com", enError.Id);
        var deBaja = escenario.Reclamaciones.AgregarHeredada("baja@tenant.com", desconectada.Id);
        var deViva = escenario.Reclamaciones.AgregarHeredada("viva@tenant.com", habilitada.Id);

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        deError.CuentaParaGuardaTransitoria.Should().BeFalse();
        deBaja.CuentaParaGuardaTransitoria.Should().BeFalse();
        deViva.CuentaParaGuardaTransitoria.Should().BeTrue("una Habilitada sin identidad aún puede completarla y sigue contando");
    }

    // Ronda 10, guarda transitoria: la renovación omite los Tenants propietarios no activos, así que su conexión no es
    // operativa aunque siga Habilitada. Sin esto, su heredada contaría para siempre en toda la plataforma.

    [Fact]
    public async Task Barrido_TenantNoActivo_HeredadaDeConexionHabilitada_SaleDeLaGuardaSinVerificarNada()
    {
        var escenario = new Escenario();
        var habilitada = ConexionIntegracion.CrearBuzonCompartido("viva@tenant.com", "Viva", Directorio, Guid.NewGuid());
        escenario.Conexiones.Agregar(habilitada);
        var heredada = escenario.Reclamaciones.AgregarHeredada("viva@tenant.com", habilitada.Id);

        await escenario.Servicio().SacarTerminalesDeGuardaTransitoriaAsync(tenantActivo: false, CancellationToken.None);

        heredada.CuentaParaGuardaTransitoria.Should().BeFalse();
        heredada.FueraDeGuardaTransitoriaUtc.Should().NotBeNull();
        heredada.IdentidadBuzon.Should().BeNull("el barrido no completa la identidad");
        habilitada.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "el barrido no cambia la conexión");
        habilitada.IdentidadVerificadaUtc.Should().BeNull();
        escenario.Graph.BuzonesUsados.Should().BeEmpty("en un Tenant no activo no se llama a Microsoft");
        escenario.Graph.VecesRefrescado.Should().Be(0);
        escenario.Graph.SuscripcionesRenovadas.Should().BeEmpty();
        escenario.Graph.VecesSuscrito.Should().Be(0);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Barrido_TenantActivo_HeredadaDeConexionHabilitada_SigueContando()
    {
        var escenario = new Escenario();
        var habilitada = ConexionIntegracion.CrearBuzonCompartido("viva@tenant.com", "Viva", Directorio, Guid.NewGuid());
        escenario.Conexiones.Agregar(habilitada);
        var heredada = escenario.Reclamaciones.AgregarHeredada("viva@tenant.com", habilitada.Id);

        await escenario.Servicio().SacarTerminalesDeGuardaTransitoriaAsync(tenantActivo: true, CancellationToken.None);

        heredada.CuentaParaGuardaTransitoria.Should().BeTrue();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
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
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "la programación antes de Graph y el ConError después");
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
    public async Task Sin_token_no_cambia_el_estado_y_solo_guarda_la_programacion_del_reintento()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(20));
        var verificadaAntes = conexion.IdentidadVerificadaUtc;
        escenario.Graph.FallaTokenAplicacion = true;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.IdentidadVerificadaUtc.Should().Be(verificadaAntes);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1, "la programación se guarda antes de llamar a Microsoft");
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
        conexion.HuecoPendienteDesdeUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "pasar a ConError marca (disparador c)");
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "la programación antes de Graph y el resultado después");
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
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "la programación antes de Graph y el resultado después");
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
    /// Habilitada pero con la verificación caducada: bloquea y se muestra «sin verificar», pero no marca hueco. Mientras
    /// tanto el correo espera en la cola; si algo se descarta, marca la ingesta (disparador a). Al verificar no lee Graph.
    /// </summary>
    [Fact]
    public async Task Una_verificacion_caducada_sin_perdida_no_fija_marca_y_no_lee_Graph()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada(
            "cae@operador.test", "inbox-cae", ConexionIntegracion.VentanaVerificacionIdentidad + TimeSpan.FromHours(1));
        escenario.Graph.MensajesADevolver = [MensajeRecibido("m-1")];

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.Graph.ListadosPedidos.Should().BeEmpty();
        escenario.Eventos.Eventos.Should().BeEmpty();
    }

    /// <summary>Caducada y sin token: sigue sin verificar y sin marca; solo se guarda la programación del reintento.</summary>
    [Fact]
    public async Task Una_verificacion_caducada_sin_token_no_fija_marca()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada(
            "cae@operador.test", "inbox-cae", ConexionIntegracion.VentanaVerificacionIdentidad + TimeSpan.FromHours(5));
        escenario.Graph.FallaTokenAplicacion = true;

        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.OperativaVerificada(DateTime.UtcNow).Should().BeFalse();
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().NotBeNull();
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
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "solo los dos guardados de la sana");
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
        escenario.UnitOfWork.VecesGuardado.Should().Be(2);
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

        CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(conexion, FalloDefinitivo, DateTime.UtcNow.AddDays(1), DateTime.UtcNow)
            .Should().BeFalse();

        conexion.UltimoError.Should().Be(motivo);
    }

    private static readonly Error FalloDefinitivo =
        Error.Crear("Integraciones.Microsoft365.ErrorSuscripcion", "No se pudo renovar la suscripción.");

    [Fact]
    public void Un_fallo_de_renovacion_marca_ConError_una_conexion_sin_motivo_de_identidad()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.MarcarConError("motivo anterior cualquiera");

        CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(conexion, FalloDefinitivo, DateTime.UtcNow.AddDays(1), DateTime.UtcNow)
            .Should().BeTrue();

        conexion.UltimoError.Should().Be("No se pudo renovar la suscripción.");
    }

    /// <summary>
    /// Un fallo transitorio del token o de la renovación (429, 5xx, red, tiempo agotado, cuerpo ilegible) con la
    /// suscripción aún vigente no dice nada de la conexión: no la pasa a ConError ni fija la marca. Se reintenta en el
    /// siguiente ciclo.
    /// </summary>
    [Theory]
    [InlineData(CodigosErrorMicrosoft365.Transitorio)]
    [InlineData(CodigosErrorMicrosoft365.ErrorRed)]
    [InlineData(CodigosErrorMicrosoft365.TiempoAgotado)]
    public void Un_fallo_transitorio_de_renovacion_no_pasa_la_conexion_a_ConError(string codigo)
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));

        var marcada = CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(
            conexion, Error.Crear(codigo, "transitorio"), DateTime.UtcNow.AddHours(2), DateTime.UtcNow);

        marcada.Should().BeFalse();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    /// <summary>
    /// Transitorio, pero la suscripción ya expiró: desde la expiración no llegan notificaciones aunque la conexión siga
    /// Habilitada (disparador b). La marca va en la expiración.
    /// </summary>
    [Fact]
    public void Un_fallo_transitorio_con_la_suscripcion_ya_expirada_marca_en_la_expiracion_sin_pasar_a_ConError()
    {
        var ahora = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(ahora.AddHours(-1));

        var marcada = CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(
            conexion, Error.Crear(CodigosErrorMicrosoft365.Transitorio, "transitorio"), ahora.AddHours(-2), ahora);

        marcada.Should().BeFalse();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora.AddHours(-2));
    }

    /// <summary>
    /// P: la conexión ya estaba en ConError por un motivo de identidad (marca en el momento de esa caída) y la renovación
    /// falla de forma definitiva con la suscripción expirada antes: la marca retrocede hasta la expiración y el motivo
    /// de identidad se conserva.
    /// </summary>
    [Theory]
    [InlineData(CompletarIdentidadBuzonService.MensajeBuzonCambiado)]
    [InlineData(CompletarIdentidadBuzonService.MensajeSinAccesoBuzon)]
    public void Un_fallo_definitivo_de_renovacion_retrocede_la_marca_aunque_haya_motivo_de_identidad(string motivo)
    {
        var ahora = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(ahora.AddDays(-2));
        conexion.MarcarConError(motivo, ahora.AddHours(-1));

        CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(conexion, FalloDefinitivo, ahora.AddHours(-20), ahora)
            .Should().BeFalse();

        conexion.UltimoError.Should().Be(motivo);
        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora.AddHours(-20));
    }

    /// <summary>
    /// Un fallo definitivo con la suscripción ya expirada: desde la expiración no llegan notificaciones, así que la
    /// marca del hueco va en la expiración y no en el momento del ciclo. Con la suscripción aún vigente, va en ahora.
    /// </summary>
    [Theory]
    [InlineData(-3, -3)]
    [InlineData(5, 0)]
    public void Un_fallo_definitivo_de_renovacion_pasa_a_ConError_con_la_marca_en_la_expiracion_si_ya_paso(
        int horasHastaExpiracion, int horasEsperadasDesdeAhora)
    {
        var ahora = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var conexion = ConexionIntegracion.CrearBuzonCompartido("cae@operador.test", "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(ahora.AddHours(-4));

        var marcada = CompletarIdentidadBuzonService.MarcarConErrorDeRenovacion(
            conexion, FalloDefinitivo, ahora.AddHours(horasHastaExpiracion), ahora);

        marcada.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.HuecoPendienteDesdeUtc.Should().Be(ahora.AddHours(horasEsperadasDesdeAhora));
    }

    /// <summary>Candidata al reintento ya programada para antes de ahora: el siguiente sondeo la intenta.</summary>
    private static ConexionIntegracion AgregarVencidaParaReintento(Escenario escenario, string direccion, string identidad)
    {
        var conexion = escenario.AgregarVerificada(direccion, identidad, ConexionIntegracion.UmbralReintentoVerificacion + TimeSpan.FromHours(1));
        conexion.ProgramarPrimerReintentoVerificacion(DateTime.UtcNow.AddHours(-1), TimeSpan.Zero);
        return conexion;
    }

    [Fact]
    public async Task El_reintento_solo_atiende_a_las_Habilitadas_con_la_verificacion_vieja_o_sin_verificar()
    {
        var escenario = new Escenario();
        var reciente = escenario.AgregarVerificada("reciente@operador.test", "inbox-reciente", TimeSpan.FromHours(10));
        var vieja = AgregarVencidaParaReintento(escenario, "vieja@operador.test", "inbox-vieja");
        var caida = escenario.AgregarVerificada("caida@operador.test", "inbox-caida", TimeSpan.FromHours(30));
        caida.MarcarConError("caída anterior");
        caida.ProgramarPrimerReintentoVerificacion(DateTime.UtcNow.AddHours(-1), TimeSpan.Zero);
        var verificadaReciente = reciente.IdentidadVerificadaUtc;

        var verificadas = await escenario.Servicio().ReintentarAsync(CancellationToken.None);

        verificadas.Should().Be(1);
        vieja.IdentidadVerificadaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        vieja.ReintentoVerificacionNoAntesDeUtc.Should().BeNull("verificar vacía la espera");
        reciente.IdentidadVerificadaUtc.Should().Be(verificadaReciente);
        caida.Estado.Should().Be(EstadoConexionIntegracion.ConError, "las ConError las levantan el ciclo diario o Reactivar");
        escenario.Cerrojo.Pedidos.Should().Equal(vieja.Id);
    }

    /// <summary>
    /// La primera vez que una conexión entra en el reintento no se intenta ya: se programa tras un retraso aleatorio de
    /// hasta IntervaloReintentoVerificacion, para que no reintenten todas a la vez.
    /// </summary>
    [Fact]
    public async Task La_primera_entrada_en_el_reintento_solo_programa_un_intento_aleatorio_sin_llamar_a_Microsoft()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada(
            "cae@operador.test", "inbox-cae", ConexionIntegracion.UmbralReintentoVerificacion + TimeSpan.FromHours(1));
        var antes = DateTime.UtcNow;

        var verificadas = await escenario.Servicio().ReintentarAsync(CancellationToken.None);

        verificadas.Should().Be(0);
        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().NotBeNull();
        conexion.ReintentoVerificacionNoAntesDeUtc!.Value.Should().BeOnOrAfter(antes)
            .And.BeOnOrBefore(DateTime.UtcNow + ConexionIntegracion.IntervaloReintentoVerificacion);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task El_reintento_respeta_la_espera_programada()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada(
            "cae@operador.test", "inbox-cae", ConexionIntegracion.UmbralReintentoVerificacion + TimeSpan.FromHours(1));
        conexion.ProgramarPrimerReintentoVerificacion(DateTime.UtcNow, TimeSpan.FromHours(1));

        await escenario.Servicio().ReintentarAsync(CancellationToken.None);
        await escenario.Servicio().CompletarAsync(CancellationToken.None);

        escenario.Graph.TokensDeIdentidad.Should().BeEmpty("ni el reintento ni el ciclo diario se adelantan a la espera");
    }

    /// <summary>Un fallo transitorio deja el siguiente intento a IntervaloReintentoVerificacion más el margen aleatorio.</summary>
    [Fact]
    public async Task Tras_un_fallo_transitorio_el_siguiente_reintento_va_a_unas_dos_horas_con_margen_aleatorio()
    {
        var escenario = new Escenario();
        var conexion = AgregarVencidaParaReintento(escenario, "cae@operador.test", "inbox-cae");
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.Transitorio, "503");
        var antes = DateTime.UtcNow;

        await escenario.Servicio().ReintentarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.ReintentoVerificacionNoAntesDeUtc!.Value.Should().BeOnOrAfter(antes + ConexionIntegracion.IntervaloReintentoVerificacion)
            .And.BeOnOrBefore(DateTime.UtcNow + ConexionIntegracion.IntervaloReintentoVerificacion + CompletarIdentidadBuzonService.MargenAleatorioMaximo);
    }

    /// <summary>
    /// 429 con Retry-After: ninguna verificación automática vuelve a intentarlo antes de lo que pidió Microsoft, hasta
    /// el tope de <see cref="ConexionIntegracion.EsperaMaximaRetryAfter"/>; un Retry-After de días no la aparca más allá
    /// de la ventana de 26 h.
    /// </summary>
    [Theory]
    [InlineData(3.0, 3.0)]
    [InlineData(6.0, 3.0)]
    [InlineData(120.0, 3.0)]
    public async Task Un_429_con_Retry_After_aplaza_el_siguiente_intento_hasta_lo_pedido_con_tope(double pedidaHoras, double esperadaHoras)
    {
        var escenario = new Escenario();
        var conexion = AgregarVencidaParaReintento(escenario, "cae@operador.test", "inbox-cae");
        escenario.Graph.ErrorIdentidadBuzon =
            Error.Crear(CodigosErrorMicrosoft365.Transitorio, "429").ConReintentarTras(TimeSpan.FromHours(pedidaHoras));

        await escenario.Servicio().ReintentarAsync(CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().BeCloseTo(DateTime.UtcNow.AddHours(esperadaHoras), TimeSpan.FromMinutes(1));
        conexion.PuedeReintentarVerificacion(DateTime.UtcNow.AddHours(esperadaHoras - 0.25)).Should().BeFalse();
    }

    /// <summary>
    /// Desconectar toma el cerrojo de la verificación, pero la selección va fuera de él: entre la selección y la
    /// comprobación puede dejar la conexión Deshabilitada y liberar su reclamación. La comprobación lo ve al releerla y sale sin leer Graph ni mutar nada; un
    /// 403/404 posterior no la deja en ConError, desde donde una verificación la rehabilitaría sin reclamación.
    /// </summary>
    [Fact]
    public async Task Desconectar_entre_la_seleccion_y_la_comprobacion_no_deja_ConError_ni_lee_Graph()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(24));
        escenario.Graph.ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.SinAccesoBuzon, "403");
        escenario.Cerrojo.AlTomar = _ => conexion.Deshabilitar();
        var reintentoAntes = conexion.ReintentoVerificacionNoAntesDeUtc;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        escenario.Cerrojo.Pedidos.Should().Equal([conexion.Id], "control positivo: la conexión sí se seleccionó");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexion.UltimoError.Should().BeNull();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().Be(reintentoAntes);
        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        escenario.Graph.DirectoriosConTokenDeAplicacion.Should().BeEmpty();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Ronda 12, R1: la comprobación relee la conexión de la base dentro del cerrojo aunque el contexto del ámbito ya la
    /// tuviera rastreada; con la instancia rastreada vería la Habilitada de antes de un Desconectar y leería Graph.
    /// </summary>
    [Fact]
    public async Task La_comprobacion_relee_de_la_base_la_conexion_que_el_contexto_ya_tenia_rastreada()
    {
        var escenario = new Escenario();
        var conexion = escenario.AgregarVerificada("cae@operador.test", "inbox-cae", TimeSpan.FromHours(24));
        escenario.Conexiones.SimularRastreo = true;
        (await escenario.Conexiones.ObtenerPorIdAsync(conexion.Id)).Should().BeSameAs(conexion, "control: queda rastreada");
        ConexionIntegracion? desconectada = null;
        escenario.Cerrojo.AlTomar = id => desconectada = escenario.Conexiones.SustituirEnBase(id, c => c.Deshabilitar());

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEmpty();
        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        escenario.Graph.DirectoriosConTokenDeAplicacion.Should().BeEmpty();
        desconectada!.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        desconectada.ReintentoVerificacionNoAntesDeUtc.Should().BeNull("no se programó ningún intento sobre ella");
    }

    /// <summary>El reintento solo toca Habilitadas: una que pasó a ConError tras la selección tampoco se comprueba.</summary>
    [Theory]
    [InlineData(EstadoConexionIntegracion.Deshabilitada)]
    [InlineData(EstadoConexionIntegracion.ConError)]
    public async Task El_reintento_no_comprueba_una_conexion_que_dejo_de_estar_Habilitada_tras_la_seleccion(EstadoConexionIntegracion estado)
    {
        var escenario = new Escenario();
        var conexion = AgregarVencidaParaReintento(escenario, "cae@operador.test", "inbox-cae");
        escenario.Cerrojo.AlTomar = _ =>
        {
            if (estado == EstadoConexionIntegracion.Deshabilitada)
                conexion.Deshabilitar();
            else
                conexion.MarcarConError("caída de la renovación");
        };

        var verificadas = await escenario.Servicio().ReintentarAsync(CancellationToken.None);

        verificadas.Should().Be(0);
        escenario.Cerrojo.Pedidos.Should().Equal([conexion.Id]);
        conexion.Estado.Should().Be(estado);
        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Una Habilitada verificada hace menos de 20 h (por el reintento o por Reactivar) no se vuelve a leer en el ciclo
    /// diario; una de 20 h o más, y una ConError reciente (esa lectura es su vía de vuelta), sí.
    /// </summary>
    [Fact]
    public async Task El_ciclo_diario_salta_las_Habilitadas_verificadas_hace_menos_de_veinte_horas()
    {
        var escenario = new Escenario();
        var reciente = escenario.AgregarVerificada("reciente@operador.test", "inbox-reciente", TimeSpan.FromHours(19));
        var vieja = escenario.AgregarVerificada("vieja@operador.test", "inbox-vieja", TimeSpan.FromHours(20));
        var caida = escenario.AgregarVerificada("caida@operador.test", "inbox-caida", TimeSpan.FromHours(1));
        caida.MarcarConError("caída de la renovación");
        var verificadaReciente = reciente.IdentidadVerificadaUtc;
        var reintentoReciente = reciente.ReintentoVerificacionNoAntesDeUtc;

        var verificadas = await escenario.Servicio().CompletarAsync(CancellationToken.None);

        verificadas.Should().BeEquivalentTo([vieja.Id, caida.Id]);
        reciente.IdentidadVerificadaUtc.Should().Be(verificadaReciente);
        reciente.ReintentoVerificacionNoAntesDeUtc.Should().Be(reintentoReciente, "ni siquiera se programa un intento");
        caida.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        escenario.Graph.TokensDeIdentidad.Should().HaveCount(2);
    }

    /// <summary>Otra verificación de la misma conexión en curso (otra réplica, el ciclo diario o Reactivar): no se toca.</summary>
    [Fact]
    public async Task Con_el_cerrojo_ocupado_no_se_comprueba_la_conexion()
    {
        var escenario = new Escenario();
        var conexion = AgregarVencidaParaReintento(escenario, "cae@operador.test", "inbox-cae");
        var esperaAntes = conexion.ReintentoVerificacionNoAntesDeUtc;
        escenario.Cerrojo.Ocupadas.Add(conexion.Id);

        (await escenario.Servicio().ReintentarAsync(CancellationToken.None)).Should().Be(0);
        (await escenario.Servicio().CompletarAsync(CancellationToken.None)).Should().BeEmpty();

        escenario.Graph.TokensDeIdentidad.Should().BeEmpty();
        conexion.ReintentoVerificacionNoAntesDeUtc.Should().Be(esperaAntes);
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_cancelacion_pedida_se_propaga_desde_el_reintento()
    {
        var escenario = new Escenario();
        AgregarVencidaParaReintento(escenario, "cae@operador.test", "inbox-cae");
        using var cancelacion = new CancellationTokenSource();
        escenario.Graph.AlLeerIdentidad = () =>
        {
            cancelacion.Cancel();
            cancelacion.Token.ThrowIfCancellationRequested();
        };

        await FluentActions.Awaiting(() => escenario.Servicio().ReintentarAsync(cancelacion.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}