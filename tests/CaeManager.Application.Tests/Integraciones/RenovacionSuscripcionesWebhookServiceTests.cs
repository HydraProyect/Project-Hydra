using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class RenovacionSuscripcionesWebhookServiceTests
{
    private const string Directorio = "11111111-2222-3333-4444-555555555555";
    private static readonly TimeSpan Ventana = TimeSpan.FromHours(48);
    private static readonly Error FalloDefinitivo = Error.Crear("Integraciones.Microsoft365.ErrorApi", "La suscripción ya no existe.");

    /// <summary>
    /// Base de datos simulada: el descarte suelta lo pendiente y, si se le dice, sustituye una conexión por la versión
    /// que dejó en la base el otro proceso (la que provocó la colisión). Sin esto, recargar devolvería la misma instancia
    /// ya mutada y el test no distinguiría entre reaplicar y no haber perdido nada.
    /// </summary>
    private sealed class Escenario
    {
        public ConexionIntegracionRepositorioFalso Conexiones { get; } = new();
        public SuscripcionWebhookRepositorioFalso Suscripciones { get; } = new();
        public Microsoft365GraphClientFalso Graph { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public DescarteQueRecargaDeLaBase Descarte { get; }

        /// <summary>Colisiones de concurrencia que quedan por lanzar, una por guardado.</summary>
        public int ColisionesPendientes { get; set; }

        public Escenario() => Descarte = new DescarteQueRecargaDeLaBase(this);

        public (ConexionIntegracion Conexion, SuscripcionWebhook Suscripcion) Agregar(string direccion, string graphId, DateTime expiracionUtc)
        {
            var conexion = Compartida(direccion);
            Conexiones.Agregar(conexion);
            var suscripcion = new SuscripcionWebhook(conexion.Id, graphId, "estado-cliente", expiracionUtc);
            Suscripciones.Agregar(suscripcion);
            return (conexion, suscripcion);
        }

        public RenovacionSuscripcionesWebhookService Servicio() => new(
            Suscripciones, Conexiones,
            new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), Graph), Graph,
            new UnitOfWorkConColisiones(this), Descarte,
            NullLogger<RenovacionSuscripcionesWebhookService>.Instance);

        /// <summary>Lo que otro proceso escribió en la base y se carga al recargar tras la colisión.</summary>
        public Func<ConexionIntegracion, ConexionIntegracion>? VersionEnLaBase { get; set; }

        /// <summary>Otra escritura del otro proceso que se ve al recargar (p. ej. Reactivar sustituyó la suscripción).</summary>
        public Action? AlDescartar { get; set; }
    }

    private sealed class UnitOfWorkConColisiones(Escenario escenario) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (escenario.ColisionesPendientes > 0)
            {
                escenario.ColisionesPendientes--;
                throw new DbUpdateConcurrencyException("Otro proceso cambió la versión de la conexión.");
            }

            return escenario.UnitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class DescarteQueRecargaDeLaBase(Escenario escenario) : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes()
        {
            Veces++;
            escenario.AlDescartar?.Invoke();
            if (escenario.VersionEnLaBase is not { } version)
                return;

            for (var i = 0; i < escenario.Conexiones.Conexiones.Count; i++)
                escenario.Conexiones.Conexiones[i] = version(escenario.Conexiones.Conexiones[i]);
        }
    }

    private static ConexionIntegracion Compartida(string direccion, Guid? id = null)
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(direccion, "Buzón CAE", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-" + direccion);
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        if (id is { } fijado)
            typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(conexion, fijado);
        return conexion;
    }

    /// <summary>
    /// La colisión de una conexión no se lleva por delante a las demás del Tenant (cada una se guarda por separado), y
    /// su propia observación no se pierde: se recarga la versión del otro proceso y se vuelve a aplicar, sin volver a
    /// llamar a Graph.
    /// </summary>
    [Fact]
    public async Task Una_colision_reaplica_el_ConError_y_la_marca_sobre_la_conexion_recargada_sin_tocar_las_demas()
    {
        var escenario = new Escenario();

        // Aún vigente: la colisión cae en el guardado de la observación, no en el previo (que no hace falta).
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", DateTime.UtcNow.AddHours(2));
        var (sana, suscripcionSana) = escenario.Agregar("sana@operador.test", "sub-sana", DateTime.UtcNow.AddHours(10));
        escenario.Graph.ErroresRenovacion["sub-rota"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;

        // El otro proceso (por ejemplo, la ingesta) guardó otra cosa en la conexión rota mientras se hablaba con Graph.
        escenario.VersionEnLaBase = actual => actual.Id == rota.Id ? Compartida("rota@operador.test", rota.Id) : actual;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == rota.Id);
        recargada.Should().NotBeSameAs(rota, "control: el descarte cargó la versión de la base");
        recargada.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        recargada.UltimoError.Should().Be(FalloDefinitivo.Mensaje);
        recargada.HuecoPendienteDesdeUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1),
            "aún no había expirado: la marca va en la hora de la observación (ConError y regla conservadora)");

        sana.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        suscripcionSana.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-rota", "sub-sana"], "reaplicar no vuelve a llamar a Graph");
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "la rota tras la colisión y la sana, cada una por su lado");
        escenario.Descarte.Veces.Should().Be(1);
    }

    /// <summary>Sin colisiones, cada conexión se guarda por separado: un guardado por conexión.</summary>
    [Fact]
    public async Task Sin_colisiones_guarda_una_vez_por_conexion()
    {
        var escenario = new Escenario();
        escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(10));
        escenario.Agregar("b@operador.test", "sub-b", DateTime.UtcNow.AddHours(10));

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.UnitOfWork.VecesGuardado.Should().Be(2);
        escenario.Descarte.Veces.Should().Be(0);
    }

    /// <summary>
    /// Agotados los intentos, la conexión queda para el ciclo siguiente (registrado), pero las demás del Tenant se
    /// renuevan igual: ya no se revierte el lote entero.
    /// </summary>
    [Fact]
    public async Task Colisiones_seguidas_agotan_los_intentos_de_una_conexion_y_no_impiden_renovar_las_demas()
    {
        var escenario = new Escenario();
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", DateTime.UtcNow.AddHours(2));
        var (_, suscripcionSana) = escenario.Agregar("sana@operador.test", "sub-sana", DateTime.UtcNow.AddHours(10));
        escenario.Graph.ErroresRenovacion["sub-rota"] = FalloDefinitivo;
        escenario.ColisionesPendientes = RenovacionSuscripcionesWebhookService.IntentosGuardado;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.Descarte.Veces.Should().Be(RenovacionSuscripcionesWebhookService.IntentosGuardado,
            "una por cada reintento y otra al rendirse, para no arrastrar la mutación a la siguiente conexión");
        suscripcionSana.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.UnitOfWork.VecesGuardado.Should().Be(1, "solo la sana");
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-rota", "sub-sana"]);
        rota.Estado.Should().Be(EstadoConexionIntegracion.ConError, "en memoria se aplicó; en la base no llegó a guardarse");
    }

    /// <summary>Si al recargar la conexión resulta Deshabilitada (Desconectar entretanto), no se marca ni se mueve.</summary>
    [Fact]
    public async Task Si_la_conexion_recargada_esta_Deshabilitada_no_se_reaplica_nada()
    {
        var escenario = new Escenario();
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", DateTime.UtcNow.AddHours(2));
        escenario.Graph.ErroresRenovacion["sub-rota"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;
        escenario.VersionEnLaBase = actual =>
        {
            var desconectada = Compartida("rota@operador.test", actual.Id);
            desconectada.Deshabilitar();
            return desconectada;
        };

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == rota.Id);
        recargada.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        recargada.UltimoError.Should().BeNull();
        recargada.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Ronda 11 (C3): Graph rechazó de forma definitiva la suscripción antigua, pero antes de guardarlo Reactivar creó
    /// otra. Al reaplicar tras la colisión, la observación es de una suscripción que ya no es la de la conexión: no la
    /// pasa a ConError ni marca una pérdida que no corresponde a la suscripción vigente.
    /// </summary>
    [Fact]
    public async Task Un_fallo_reaplicado_sobre_una_suscripcion_sustituida_se_descarta_sin_mutar_nada()
    {
        var escenario = new Escenario();
        var (conexion, suscripcion) = escenario.Agregar("a@operador.test", "sub-vieja", DateTime.UtcNow.AddHours(2));
        escenario.Graph.ErroresRenovacion["sub-vieja"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;
        var expiracionNueva = DateTime.UtcNow.AddDays(3);
        escenario.VersionEnLaBase = actual => Compartida("a@operador.test", actual.Id);
        escenario.AlDescartar = () => suscripcion.ActualizarTrasRenovacion("sub-nueva", expiracionNueva);

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == conexion.Id);
        recargada.Should().NotBeSameAs(conexion, "control: el descarte cargó la versión de la base");
        recargada.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        recargada.UltimoError.Should().BeNull();
        recargada.HuecoPendienteDesdeUtc.Should().BeNull();
        suscripcion.GraphSubscriptionId.Should().Be("sub-nueva");
        suscripcion.FechaExpiracionUtc.Should().Be(expiracionNueva);
    }

    /// <summary>Una renovación con éxito que colisiona se reaplica a la suscripción recargada, sin volver a Graph.</summary>
    [Fact]
    public async Task Una_renovacion_que_colisiona_se_reaplica_a_la_suscripcion()
    {
        var escenario = new Escenario();
        var (_, suscripcion) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(10));
        escenario.ColisionesPendientes = 1;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        suscripcion.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-a"]);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    // Ronda 13: regla conservadora guardada antes de Graph y hora de observación tomada tras la respuesta.

    /// <summary>
    /// La suscripción ya expiró: la marca va en su expiración y llega a la base, sin la cancelación del llamador, antes de
    /// pedir la renovación a Graph. Lo que Graph responda después no puede dejarla posterior a la pérdida.
    /// </summary>
    [Fact]
    public async Task Con_la_suscripcion_ya_expirada_la_marca_se_guarda_antes_de_llamar_a_Graph()
    {
        var escenario = new Escenario();
        var expiracion = DateTime.UtcNow.AddHours(-2);
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-a", expiracion);
        escenario.Graph.ErroresRenovacion["sub-a"] = FalloDefinitivo;
        var marcasGuardadas = new List<DateTime?>();
        escenario.UnitOfWork.AlGuardar = () => marcasGuardadas.Add(conexion.HuecoPendienteDesdeUtc);
        var guardadasAlRenovar = -1;
        escenario.Graph.AlRenovarSuscripcion = _ => guardadasAlRenovar = marcasGuardadas.Count;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        guardadasAlRenovar.Should().Be(1, "la marca ya estaba guardada cuando salió la renovación");
        marcasGuardadas[0].Should().Be(expiracion);
        escenario.UnitOfWork.TokensDeGuardado[0].Should().Be(CancellationToken.None);
        conexion.HuecoPendienteDesdeUtc.Should().Be(expiracion, "el ConError posterior no la adelanta");
    }

    /// <summary>Una conexión en ConError con la suscripción viva: la marca retrocede a la observación antes de Graph.</summary>
    [Fact]
    public async Task Una_conexion_en_ConError_sin_marca_se_marca_antes_de_llamar_a_Graph()
    {
        var escenario = new Escenario();
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(10));
        conexion.MarcarConError("caída");
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.HuecoPendienteDesdeUtc))!.SetValue(conexion, null);
        var marcaAlRenovar = (DateTime?)null;
        var guardadosAlRenovar = -1;
        escenario.Graph.AlRenovarSuscripcion = _ => (guardadosAlRenovar, marcaAlRenovar) = (escenario.UnitOfWork.VecesGuardado, conexion.HuecoPendienteDesdeUtc);

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        guardadosAlRenovar.Should().Be(1);
        marcaAlRenovar.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Caso (c) de Codex en la ronda 12: la suscripción expira mientras dura la petición y Graph responde con un fallo
    /// transitorio. La hora de la observación es la de después de la respuesta, así que cuenta como expirada y la marca
    /// va en su expiración, no queda sin poner hasta el ciclo siguiente.
    /// </summary>
    [Fact]
    public async Task Una_suscripcion_que_expira_durante_la_peticion_con_fallo_transitorio_marca_en_su_expiracion()
    {
        var escenario = new Escenario();
        var expiracion = DateTime.UtcNow.AddMilliseconds(300);
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-a", expiracion);
        escenario.Graph.ErroresRenovacion["sub-a"] = Error.Crear(CodigosErrorMicrosoft365.Transitorio, "503");
        escenario.Graph.AlRenovarSuscripcion = _ =>
        {
            while (DateTime.UtcNow <= expiracion)
                Thread.Sleep(10);
        };

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "un fallo transitorio no pasa a ConError");
        conexion.HuecoPendienteDesdeUtc.Should().Be(expiracion);
        escenario.UnitOfWork.VecesGuardado.Should().Be(1, "no había expirado antes de la llamada: solo el guardado de la observación");
    }

    /// <summary>
    /// El guardado previo colisiona en todos los intentos: la conexión no llama a Graph (ninguna llamada sale sin la marca
    /// guardada) y espera al ciclo siguiente; las demás se renuevan igual.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_previo_agota_las_colisiones_no_se_llama_a_Graph_y_las_demas_siguen()
    {
        var escenario = new Escenario();
        var (rota, _) = escenario.Agregar("rota@operador.test", "sub-rota", DateTime.UtcNow.AddHours(-2));
        var (_, suscripcionSana) = escenario.Agregar("sana@operador.test", "sub-sana", DateTime.UtcNow.AddHours(10));
        escenario.ColisionesPendientes = RenovacionSuscripcionesWebhookService.IntentosGuardado;

        // Cada recarga trae la versión de la base, sin la marca que no llegó a guardarse.
        escenario.VersionEnLaBase = actual => actual.Id == rota.Id ? Compartida("rota@operador.test", rota.Id) : actual;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-sana"]);
        suscripcionSana.FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
        escenario.UnitOfWork.VecesGuardado.Should().Be(1, "solo la sana");
        escenario.Conexiones.Conexiones.Single(c => c.Id == rota.Id).Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
    }

    /// <summary>
    /// Una colisión del guardado previo recarga la conexión, vuelve a aplicar la regla sobre la versión de la base y,
    /// guardada ya la marca, sigue con Graph: no se pierde la renovación de este ciclo.
    /// </summary>
    [Fact]
    public async Task Una_colision_del_guardado_previo_recarga_reaplica_y_sigue_con_Graph()
    {
        var escenario = new Escenario();
        var expiracion = DateTime.UtcNow.AddHours(-2);
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-a", expiracion);
        escenario.Graph.ErroresRenovacion["sub-a"] = FalloDefinitivo;
        escenario.ColisionesPendientes = 1;
        escenario.VersionEnLaBase = actual => Compartida("a@operador.test", actual.Id);

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == conexion.Id);
        recargada.Should().NotBeSameAs(conexion);
        recargada.HuecoPendienteDesdeUtc.Should().Be(expiracion);
        recargada.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-a"]);
        escenario.UnitOfWork.VecesGuardado.Should().Be(2, "la marca previa y la observación");
    }

    // Ronda 14: los medios de la ronda 13 que quedaron declarados como huecos en #2124.

    /// <summary>
    /// Codex, ronda 13, medio 1: la consulta del lote deja rastreada la suscripción y Reactivar la sustituye (mismo
    /// registro, otro identificador de Graph) antes de que el ciclo llegue a ella. La renovación lee la de la base: no
    /// pide el PATCH del identificador antiguo, y su 404 no pone en ConError la conexión recién rehabilitada.
    /// </summary>
    [Fact]
    public async Task Una_suscripcion_sustituida_tras_cargar_el_lote_se_renueva_la_nueva_y_no_pone_ConError()
    {
        var escenario = new Escenario();
        escenario.Suscripciones.SimularRastreo = true;
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-vieja", DateTime.UtcNow.AddHours(2));
        escenario.Graph.ErroresRenovacion["sub-vieja"] = FalloDefinitivo;
        escenario.Suscripciones.AlListarProximasAExpirar = () =>
            escenario.Suscripciones.SustituirEnBase(conexion.Id, "sub-nueva", DateTime.UtcNow.AddHours(20));

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-nueva"]);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.Suscripciones.Suscripciones.Single().FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
    }

    /// <summary>
    /// Codex, ronda 13, medio 2: la suscripción expira mientras se pide el token. La regla se vuelve a aplicar y a guardar
    /// justo antes del PATCH, así que la llamada de renovación ya no sale con una pérdida sin su marca en la base.
    /// </summary>
    [Fact]
    public async Task Una_suscripcion_que_expira_mientras_se_pide_el_token_guarda_la_marca_antes_del_PATCH()
    {
        var escenario = new Escenario();
        var (conexion, suscripcion) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(2));
        escenario.Graph.ErroresRenovacion["sub-a"] = Error.Crear(CodigosErrorMicrosoft365.Transitorio, "503");
        var marcasGuardadas = new List<DateTime?>();
        escenario.UnitOfWork.AlGuardar = () => marcasGuardadas.Add(conexion.HuecoPendienteDesdeUtc);
        var guardadasAlPedirToken = -1;
        var expiracion = DateTime.MinValue;

        // Sin reloj: mientras se pide el token, la expiración de la suscripción queda en el pasado, que para el servicio
        // es lo mismo que haber llegado a ella durante la petición.
        escenario.Graph.AlObtenerTokenAplicacion = () =>
        {
            guardadasAlPedirToken = marcasGuardadas.Count;
            expiracion = DateTime.UtcNow.AddSeconds(-1);
            suscripcion.ActualizarTrasRenovacion("sub-a", expiracion);
        };
        var guardadasAlRenovar = -1;
        escenario.Graph.AlRenovarSuscripcion = _ => guardadasAlRenovar = marcasGuardadas.Count;

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        guardadasAlPedirToken.Should().Be(0, "control: al pedir el token aún no había expirado y no había nada que guardar");
        guardadasAlRenovar.Should().Be(1, "la marca ya estaba guardada cuando salió el PATCH");
        marcasGuardadas[0].Should().Be(expiracion);
        escenario.UnitOfWork.TokensDeGuardado[0].Should().Be(CancellationToken.None);
        conexion.HuecoPendienteDesdeUtc.Should().Be(expiracion);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "un fallo transitorio no pasa a ConError");
    }

    /// <summary>
    /// El guardado de justo antes del PATCH colisiona: se recargan la conexión y la suscripción de la base, la regla se
    /// vuelve a aplicar sobre ellas y el PATCH va contra la suscripción recargada, no contra la que se observó antes.
    /// </summary>
    [Fact]
    public async Task Una_colision_del_guardado_previo_al_PATCH_recarga_y_renueva_la_suscripcion_recargada()
    {
        var escenario = new Escenario();
        var (conexion, suscripcion) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(2));
        var expiracionRecargada = DateTime.UtcNow.AddMinutes(-5);
        escenario.Graph.AlObtenerTokenAplicacion = () =>
        {
            suscripcion.ActualizarTrasRenovacion("sub-a", DateTime.UtcNow.AddSeconds(-1));
            escenario.ColisionesPendientes = 1;
        };
        escenario.VersionEnLaBase = actual => Compartida("a@operador.test", actual.Id);
        escenario.AlDescartar = () => escenario.Suscripciones.SustituirEnBase(conexion.Id, "sub-recargada", expiracionRecargada);

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        var recargada = escenario.Conexiones.Conexiones.Single(c => c.Id == conexion.Id);
        recargada.Should().NotBeSameAs(conexion, "control: el descarte cargó la versión de la base");
        escenario.Graph.SuscripcionesRenovadas.Should().Equal(["sub-recargada"]);
        recargada.HuecoPendienteDesdeUtc.Should().Be(expiracionRecargada);
        escenario.Suscripciones.Suscripciones.Single().FechaExpiracionUtc.Should().BeAfter(DateTime.UtcNow.AddDays(2));
    }

    [Fact]
    public async Task Una_conexion_Deshabilitada_no_se_renueva_ni_se_marca()
    {
        var escenario = new Escenario();
        var (conexion, _) = escenario.Agregar("a@operador.test", "sub-a", DateTime.UtcNow.AddHours(-2));
        conexion.Deshabilitar();

        await escenario.Servicio().RenovarProximasAExpirarAsync(Ventana, CancellationToken.None);

        escenario.Graph.SuscripcionesRenovadas.Should().BeEmpty();
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }
}
