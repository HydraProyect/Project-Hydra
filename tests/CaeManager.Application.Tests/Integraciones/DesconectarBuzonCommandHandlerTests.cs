using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class DesconectarBuzonCommandHandlerTests
{
    private static DesconectarBuzonCommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio, SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        ReclamacionBuzonIntegracionRepositorioFalso reclamacionRepositorio,
        AlcanceDatosServiceFalso alcanceDatos, Microsoft365GraphClientFalso graphClient, CredencialIntegracionRepositorioFalso credencialRepositorio,
        IUnitOfWork unitOfWork, CerrojoVerificacionBuzonFalso? cerrojo = null, IDescarteCambiosPendientes? descarte = null,
        TimeProvider? reloj = null, ILogger<DesconectarBuzonCommandHandler>? logger = null) =>
        CrearHandlerCon(conexionRepositorio, suscripcionRepositorio, reclamacionRepositorio, alcanceDatos, graphClient,
            credencialRepositorio, unitOfWork, cerrojo ?? new CerrojoVerificacionBuzonFalso(), descarte ?? new DescarteContado(),
            reloj, logger);

    private static DesconectarBuzonCommandHandler CrearHandlerCon(
        ConexionIntegracionRepositorioFalso conexionRepositorio, SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        ReclamacionBuzonIntegracionRepositorioFalso reclamacionRepositorio,
        AlcanceDatosServiceFalso alcanceDatos, Microsoft365GraphClientFalso graphClient, CredencialIntegracionRepositorioFalso credencialRepositorio,
        IUnitOfWork unitOfWork, CerrojoVerificacionBuzonFalso cerrojo, IDescarteCambiosPendientes descarte,
        TimeProvider? reloj, ILogger<DesconectarBuzonCommandHandler>? logger) =>
        new(conexionRepositorio, suscripcionRepositorio, reclamacionRepositorio, alcanceDatos, graphClient,
            new AccesoGraphService(credencialRepositorio, graphClient), unitOfWork, cerrojo, descarte,
            new ReparacionMarcaHuecoService(conexionRepositorio, unitOfWork, cerrojo, descarte, NullLogger<ReparacionMarcaHuecoService>.Instance),
            reloj ?? TimeProvider.System, logger ?? NullLogger<DesconectarBuzonCommandHandler>.Instance);

    private sealed class LoggerCaptura : ILogger<DesconectarBuzonCommandHandler>
    {
        public List<(LogLevel Nivel, string Mensaje)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Reloj que solo avanza cuando el test lo dice, para fijar en qué paso expira la suscripción.</summary>
    private sealed class RelojFalso(DateTime ahoraUtc) : TimeProvider
    {
        private DateTimeOffset _ahora = new(ahoraUtc, TimeSpan.Zero);
        public DateTime AhoraUtc => _ahora.UtcDateTime;
        public override DateTimeOffset GetUtcNow() => _ahora;
        public void Avanzar(TimeSpan tiempo) => _ahora += tiempo;
    }

    /// <summary>Buzón compartido Habilitado y verificado con una suscripción que expira en <paramref name="expiracion"/>.</summary>
    private static (ConexionIntegracion Conexion, ConexionIntegracionRepositorioFalso Conexiones, SuscripcionWebhookRepositorioFalso Suscripciones)
        BuzonCompartidoQueExpiraEn(DateTime expiracion, DateTime verificadaUtc)
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "cae@operador.test", "Buzón CAE", "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(verificadaUtc);
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", expiracion));
        return (conexion, conexiones, suscripciones);
    }

    /// <summary>Lanza una colisión de concurrencia en los guardados indicados (1 = el primero) y delega el resto.</summary>
    private sealed class UnitOfWorkConColisiones(UnitOfWorkFalso interno, params int[] guardadosQueColisionan) : IUnitOfWork
    {
        private int _llamadas;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _llamadas++;
            if (guardadosQueColisionan.Contains(_llamadas))
                throw new DbUpdateConcurrencyException("Otro proceso cambió la versión de la conexión.");

            return interno.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Cuenta los descartes y, si se le dice, carga en el repositorio la versión de la base (otra instancia).</summary>
    private sealed class DescarteContado(Action? alDescartar = null) : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes()
        {
            Veces++;
            alDescartar?.Invoke();
        }
    }

    /// <param name="porRegistrarDesdeUtc">Lo que la desconexión guardó en
    /// <see cref="ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc"/> junto a la Deshabilitada.</param>
    private static ConexionIntegracion Copia(ConexionIntegracion original, bool deshabilitada, DateTime? porRegistrarDesdeUtc = null)
    {
        var copia = new ConexionIntegracion(original.BuzonEmail, original.Nombre);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(copia, original.Id);
        if (deshabilitada)
            copia.Deshabilitar(porRegistrarDesdeUtc);
        return copia;
    }

    private static (ConexionIntegracion Conexion, ConexionIntegracionRepositorioFalso Conexiones, SuscripcionWebhookRepositorioFalso Suscripciones) ConSuscripcion(string graphId = "sub-1")
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, graphId, "estado", DateTime.UtcNow.AddDays(2)));
        return (conexion, conexiones, suscripciones);
    }

    // Ronda 11, C1: Desconectar comparte el cerrojo por conexión de Reactivar y de la verificación.

    [Fact]
    public async Task Con_el_cerrojo_ocupado_pide_volver_a_intentarlo_sin_guardar_ni_borrar()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.Ocupadas.Add(conexion.Id);
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork, cerrojo);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoVerificacionEnCurso);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        unitOfWork.VecesGuardado.Should().Be(0);
        graph.SuscripcionesEliminadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Guarda_y_borra_dentro_del_cerrojo_de_la_conexion()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var dentro = false;
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        var cerrojoTomadoAlGuardar = false;
        var cerrojoTomadoAlBorrar = false;
        cerrojo.AlTomar = _ => dentro = true;
        cerrojo.AlSalir = _ => dentro = false;
        unitOfWork.AlGuardar = () => cerrojoTomadoAlGuardar = dentro;
        graph.AlEliminarSuscripcion = () => cerrojoTomadoAlBorrar = dentro;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork, cerrojo);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cerrojo.Pedidos.Should().Equal(conexion.Id);
        cerrojoTomadoAlGuardar.Should().BeTrue();
        cerrojoTomadoAlBorrar.Should().BeTrue();
    }

    [Fact]
    public async Task Borra_solo_la_suscripcion_que_se_guardo_junto_a_la_Deshabilitada()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion("sub-guardada");
        var suscripcion = suscripciones.Suscripciones.Single();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        // Lo que la entidad diga después del guardado no cambia a cuál se borra.
        unitOfWork.AlGuardar = () => suscripcion.ActualizarTrasRenovacion("sub-otra", DateTime.UtcNow.AddDays(3));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        graph.SuscripcionesEliminadas.Should().Equal("sub-guardada");
    }

    // Ronda 11, C4: un borrado fallido o incierto deja siempre la marca, también tras una colisión.

    [Fact]
    public async Task Una_colision_al_guardar_la_marca_recarga_la_conexion_y_la_vuelve_a_poner()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var antes = DateTime.UtcNow;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 2), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var recargada = conexiones.Conexiones.Single();
        recargada.Should().NotBeSameAs(conexion, "control: se recargó la versión de la base");
        recargada.HuecoPendienteDesdeUtc.Should().BeOnOrAfter(antes).And.BeOnOrBefore(DateTime.UtcNow);
        recargada.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        descarte.Veces.Should().Be(1);
        interno.VecesGuardado.Should().Be(2, "la desconexión y la marca reaplicada");
    }

    /// <summary>
    /// Codex, ronda 16 (variante): la marca del borrado fallido agota las colisiones. No se devuelve éxito sin ella: se
    /// reintenta sobre la versión recargada antes de devolver y llega a la base.
    /// </summary>
    [Fact]
    public async Task Agotados_los_intentos_de_la_marca_del_borrado_fallido_se_reintenta_sobre_la_version_recargada()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var antes = DateTime.UtcNow;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 2, 3, 4), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        descarte.Veces.Should().Be(DesconectarBuzonCommandHandler.IntentosGuardadoMarca, "control: se agotaron las colisiones");
        interno.VecesGuardado.Should().Be(2, "la desconexión y la marca reintentada");
        var recargada = conexiones.Conexiones[0];
        recargada.Should().NotBeSameAs(conexion, "control: el reintento va sobre la versión de la base");
        recargada.HuecoPendienteDesdeUtc.Should().BeOnOrAfter(antes).And.BeOnOrBefore(DateTime.UtcNow);
    }

    /// <summary>
    /// Codex, ronda 16: si la marca del borrado fallido tampoco llega a la base al reintentarla, la conexión queda
    /// Deshabilitada sin declarar el hueco. El comando no devuelve un éxito silencioso: lo registra como crítico, con el
    /// Id de la conexión, y devuelve un fallo propio.
    /// </summary>
    [Fact]
    public async Task Si_la_marca_tampoco_llega_al_reintentarla_lo_registra_como_critico_y_devuelve_un_fallo()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var logger = new LoggerCaptura();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 2, 3, 4, 5, 6, 7), descarte: descarte, logger: logger);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada);
        interno.VecesGuardado.Should().Be(1, "solo la desconexión");
        descarte.Veces.Should().Be(2 * DesconectarBuzonCommandHandler.IntentosGuardadoMarca, "el intento y el reintento");
        logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Mensaje.Should().Contain(conexion.Id.ToString());
        conexiones.Conexiones[0].Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
    }

    /// <summary>Lanza <paramref name="excepcion"/> en el guardado número <paramref name="guardado"/> y delega el resto.</summary>
    private sealed class UnitOfWorkQueFallaEn(UnitOfWorkFalso interno, int guardado, Exception excepcion) : IUnitOfWork
    {
        private int _llamadas;

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ++_llamadas == guardado ? throw excepcion : interno.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Bajo B4 del revisor-puente (ronda 13): un fallo del guardado de la marca que no es de concurrencia se registra y
    /// se descarta sin lanzar. Codex, ronda 16 (variante): tampoco se da por perdido; se reintenta sobre la versión
    /// recargada de la base y la marca llega.
    /// </summary>
    [Fact]
    public async Task Un_fallo_ajeno_a_la_concurrencia_al_guardar_la_marca_del_borrado_fallido_se_reintenta_sobre_la_version_recargada()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        // La base guarda la Deshabilitada sin marca: es lo que se recarga tras descartar.
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        interno.VecesGuardado.Should().Be(2, "la desconexión y la marca reintentada");
        descarte.Veces.Should().Be(1);
        var recargada = conexiones.Conexiones[0];
        recargada.Should().NotBeSameAs(conexion, "control: el reintento va sobre la versión de la base");
        recargada.HuecoPendienteDesdeUtc.Should().NotBeNull();
    }

    /// <summary>Ronda 13: con la suscripción ya expirada, la marca del borrado fallido queda en su expiración.</summary>
    [Fact]
    public async Task Con_la_suscripcion_ya_expirada_la_marca_del_borrado_fallido_queda_en_su_expiracion()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var expiracion = DateTime.UtcNow.AddHours(-3);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", expiracion));
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "404") };
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, new UnitOfWorkFalso());

        await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        conexion.HuecoPendienteDesdeUtc.Should().Be(expiracion);
    }

    /// <summary>
    /// Medio del revisor-puente (ronda 13): con la suscripción ya expirada, Graph responde 404 al DELETE y el cliente lo
    /// cuenta como borrado con éxito. La pérdida entre la expiración y la desconexión se marca igual, en la expiración, y
    /// llega a la base antes de pedir el token y de borrar.
    /// </summary>
    [Fact]
    public async Task Con_la_suscripcion_ya_expirada_y_un_borrado_con_exito_la_marca_se_guarda_en_su_expiracion_antes_de_Graph()
    {
        var expiracion = DateTime.UtcNow.AddHours(-3);
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "cae@operador.test", "Buzón CAE", "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", expiracion));

        // El cliente de Graph cuenta el 404 de una suscripción ya expirada como borrado con éxito.
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var marcasGuardadas = new List<DateTime?>();
        unitOfWork.AlGuardar = () => marcasGuardadas.Add(conexion.HuecoPendienteDesdeUtc);
        var guardadasAlPedirToken = -1;
        graph.AlObtenerTokenAplicacion = () => guardadasAlPedirToken = marcasGuardadas.Count;
        var guardadasAlBorrar = -1;
        graph.AlEliminarSuscripcion = () => guardadasAlBorrar = marcasGuardadas.Count;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
        guardadasAlPedirToken.Should().Be(1, "la marca ya estaba guardada cuando se pidió el token");
        guardadasAlBorrar.Should().Be(2, "la marca y la desconexión, antes del DELETE");
        marcasGuardadas[0].Should().Be(expiracion);
        unitOfWork.TokensDeGuardado[0].Should().Be(CancellationToken.None);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexion.HuecoPendienteDesdeUtc.Should().Be(expiracion, "un borrado con éxito no la borra ni la adelanta");
    }

    /// <summary>
    /// La suscripción expira mientras se pide el token: la regla se vuelve a aplicar sobre la conexión aún sin
    /// deshabilitar y la marca viaja en el MISMO guardado de la desconexión, antes del DELETE. El reloj controlado fija
    /// la expiración dentro de la petición del token (Codex, ronda 15, hallazgo bajo): al pedirlo aún no había expirado
    /// y no se había guardado nada.
    /// </summary>
    [Fact]
    public async Task Una_suscripcion_que_expira_mientras_se_pide_el_token_se_marca_en_el_guardado_de_la_desconexion()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var guardados = new List<(DateTime? Marca, EstadoConexionIntegracion Estado)>();
        unitOfWork.AlGuardar = () => guardados.Add((conexion.HuecoPendienteDesdeUtc, conexion.Estado));
        DateTime? relojAlPedirToken = null;
        var guardadosAlPedirToken = -1;
        graph.AlObtenerTokenAplicacion = () =>
        {
            relojAlPedirToken = reloj.AhoraUtc;
            guardadosAlPedirToken = guardados.Count;
            reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var guardadosAlBorrar = -1;
        graph.AlEliminarSuscripcion = () => guardadosAlBorrar = guardados.Count;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), unitOfWork, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        relojAlPedirToken.Should().BeBefore(expiracion, "control: la expiración cae dentro de la petición del token");
        guardadosAlPedirToken.Should().Be(0, "con la suscripción vigente no hay marca que guardar antes del token");
        guardados.Should().Equal([(expiracion, EstadoConexionIntegracion.Deshabilitada)],
            "la marca viaja en el mismo guardado que la desconexión, y no hace falta otro");
        guardadosAlBorrar.Should().Be(1, "la marca ya estaba en la base cuando salió el DELETE");
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
    }

    /// <summary>
    /// Medio de Codex (ronda 15): la suscripción expira después de la última comprobación previa al guardado y antes de que
    /// la desconexión quede confirmada, y el DELETE responde 404, que el cliente de Graph cuenta como borrado con éxito.
    /// La regla se vuelve a aplicar con el reloj leído tras la confirmación: la marca queda en la expiración y llega a la
    /// base antes del DELETE, sin depender de lo que Graph responda.
    /// </summary>
    [Fact]
    public async Task Una_suscripcion_que_expira_durante_el_guardado_de_la_desconexion_se_marca_aunque_el_DELETE_tenga_exito()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        // Sin error de eliminación: el 404 de una suscripción ya expirada se cuenta como borrado con éxito.
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var guardados = new List<(DateTime? Marca, EstadoConexionIntegracion Estado)>();
        unitOfWork.AlGuardar = () =>
        {
            guardados.Add((conexion.HuecoPendienteDesdeUtc, conexion.Estado));
            // La expiración cae dentro del guardado que confirma la desconexión.
            if (guardados.Count == 1)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var guardadosAlBorrar = -1;
        graph.AlEliminarSuscripcion = () => guardadosAlBorrar = guardados.Count;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), unitOfWork, reloj: reloj);
        // Un token del llamador vivo y distinto de None: el guardado de la marca no debe usarlo.
        using var cancelacion = new CancellationTokenSource();

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), cancelacion.Token);

        resultado.EsExitoso.Should().BeTrue();
        guardados.Should().Equal(
            [(null, EstadoConexionIntegracion.Deshabilitada), (expiracion, EstadoConexionIntegracion.Deshabilitada)],
            "control: al confirmar aún no había marca; la del hueco se guarda justo después");
        guardadosAlBorrar.Should().Be(2, "la marca ya estaba en la base cuando salió el DELETE");
        unitOfWork.TokensDeGuardado[0].Should().Be(cancelacion.Token, "control: la desconexión usa el token del llamador");
        unitOfWork.TokensDeGuardado[1].Should().Be(CancellationToken.None, "la marca se guarda sin la cancelación del llamador");
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
        conexion.HuecoPendienteDesdeUtc.Should().Be(expiracion);
    }

    /// <summary>
    /// Medio del revisor-puente (ronda 15): la suscripción expira durante el guardado de la desconexión, el guardado de la
    /// marca de justo después falla y el DELETE también. El intento de la marca del borrado fallido sigue con la versión
    /// recargada de la base, no con la instancia descartada que ya lleva la marca en memoria, y la marca llega a la base.
    /// </summary>
    [Fact]
    public async Task Si_falla_el_guardado_de_la_marca_tras_confirmar_el_del_borrado_fallido_la_guarda_sobre_la_version_recargada()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        // La expiración cae dentro del guardado que confirma la desconexión (el primero que llega al interno).
        interno.AlGuardar = () =>
        {
            if (interno.VecesGuardado == 0)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        // La base guarda la Deshabilitada sin marca: es lo que se recarga tras descartar.
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la desconexión ya quedó guardada");
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
        descarte.Veces.Should().Be(1);
        var recargada = conexiones.Conexiones[0];
        recargada.Should().NotBeSameAs(conexion, "control: se recargó la versión de la base");
        recargada.HuecoPendienteDesdeUtc.Should().Be(expiracion);
        interno.VecesGuardado.Should().Be(2, "la desconexión y la marca del borrado fallido sobre la versión recargada");
    }

    /// <summary>
    /// Medio de Codex (ronda 16): la suscripción expira durante el guardado de la desconexión, el guardado de la marca de
    /// justo después falla y el DELETE tiene éxito (el 404 de una suscripción ya expirada cuenta como borrado). Antes se
    /// devolvía éxito con la conexión Deshabilitada y sin marca; ahora la marca se reintenta sobre la versión recargada
    /// antes de devolver y llega a la base en la expiración.
    /// </summary>
    [Fact]
    public async Task Si_falla_el_guardado_de_la_marca_tras_confirmar_y_el_DELETE_tiene_exito_la_reintenta_sobre_la_version_recargada()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        interno.AlGuardar = () =>
        {
            if (interno.VecesGuardado == 0)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"], "control: el DELETE tuvo éxito");
        descarte.Veces.Should().Be(1, "control: el guardado de la marca tras confirmar falló");
        var recargada = conexiones.Conexiones[0];
        recargada.Should().NotBeSameAs(conexion, "control: el reintento va sobre la versión de la base");
        recargada.HuecoPendienteDesdeUtc.Should().Be(expiracion);
        interno.VecesGuardado.Should().Be(2, "la desconexión y la marca reintentada");
    }

    /// <summary>
    /// Codex, ronda 16 (variante): el guardado de la marca tras confirmar agota las colisiones y el DELETE tiene éxito.
    /// La marca se reintenta sobre la versión recargada antes de devolver.
    /// </summary>
    [Fact]
    public async Task Si_la_marca_tras_confirmar_agota_las_colisiones_y_el_DELETE_tiene_exito_la_reintenta_sobre_la_version_recargada()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        interno.AlGuardar = () =>
        {
            if (interno.VecesGuardado == 0)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkConColisiones(interno, 2, 3, 4), descarte: descarte, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
        descarte.Veces.Should().Be(DesconectarBuzonCommandHandler.IntentosGuardadoMarca, "control: se agotaron las colisiones");
        conexiones.Conexiones[0].HuecoPendienteDesdeUtc.Should().Be(expiracion);
        interno.VecesGuardado.Should().Be(2, "la desconexión y la marca reintentada");
    }

    /// <summary>
    /// Codex, ronda 16 (variante): el guardado de la marca tras confirmar falla, la recarga del reintento también y el
    /// DELETE tiene éxito. Antes se devolvía éxito con la instancia descartada; ahora no hay éxito silencioso: crítico con
    /// el Id de la conexión y un fallo propio.
    /// </summary>
    [Fact]
    public async Task Si_fallan_la_marca_tras_confirmar_y_la_recarga_aunque_el_DELETE_tenga_exito_devuelve_un_fallo()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        interno.AlGuardar = () =>
        {
            if (interno.VecesGuardado == 0)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var recargaRota = false;
        conexiones.AlLeerActual = _ =>
        {
            if (recargaRota)
                throw new InvalidOperationException("base caída");
        };
        var descarte = new DescarteContado(() => recargaRota = true);
        var logger = new LoggerCaptura();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte, reloj: reloj,
            logger: logger);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada);
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"], "control: el DELETE tuvo éxito");
        interno.VecesGuardado.Should().Be(1, "solo la desconexión");
        logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Mensaje.Should().Contain(conexion.Id.ToString());
    }

    /// <summary>
    /// Fila 9 del inventario de la ronda 16 (bajo del revisor-puente): fallan el guardado de la marca tras confirmar, el
    /// DELETE y la recarga del intento del borrado. Ese intento parte de la base (no de la instancia descartada, que ya
    /// lleva la marca en memoria) y, si la recarga se rompe una sola vez, el reintento la guarda; si sigue rota, no hay
    /// éxito silencioso.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Si_fallan_la_marca_tras_confirmar_el_DELETE_y_la_recarga_el_reintento_la_guarda_o_devuelve_un_fallo(bool recargaSiempreRota)
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        interno.AlGuardar = () =>
        {
            if (interno.VecesGuardado == 0)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var recargaRota = false;
        var lecturasRotas = 0;
        conexiones.AlLeerActual = _ =>
        {
            if (!recargaRota)
                return;
            lecturasRotas++;
            if (!recargaSiempreRota)
                recargaRota = false;
            throw new InvalidOperationException("base caída");
        };
        // La base guarda la Deshabilitada sin marca; tras el primer descarte, la recarga se rompe.
        var descarte = new DescarteContado(() =>
        {
            conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true);
            if (lecturasRotas == 0)
                recargaRota = true;
        });
        var logger = new LoggerCaptura();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte, reloj: reloj,
            logger: logger);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"], "control: el DELETE se pidió y falló (ErrorEliminacion)");
        lecturasRotas.Should().BeGreaterThan(0, "control: la recarga del intento del borrado se rompió");
        if (recargaSiempreRota)
        {
            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada);
            interno.VecesGuardado.Should().Be(1, "solo la desconexión");
            logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical);
        }
        else
        {
            resultado.EsExitoso.Should().BeTrue();
            interno.VecesGuardado.Should().Be(2, "la desconexión y la marca reintentada");
            var recargada = conexiones.Conexiones[0];
            recargada.Should().NotBeSameAs(conexion, "control: el reintento va sobre la versión de la base");
            recargada.HuecoPendienteDesdeUtc.Should().Be(expiracion, "la marca del borrado sobre una suscripción ya expirada");
            logger.Entradas.Should().NotContain(e => e.Nivel == LogLevel.Critical);
        }
    }

    /// <summary>
    /// Codex, ronda 16 (variante): la cancelación pedida durante el borrado no tapa una marca que no llegó a la base: el
    /// fallo propio prevalece sobre la cancelación, porque la desconexión ya está guardada.
    /// </summary>
    [Fact]
    public async Task Si_la_marca_no_llega_tras_un_borrado_cancelado_devuelve_el_fallo_en_vez_de_la_cancelacion()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        using var cancelacion = new CancellationTokenSource();
        var graph = new Microsoft365GraphClientFalso();
        graph.AlEliminarSuscripcion = () =>
        {
            cancelacion.Cancel();
            graph.ExcepcionEliminacion = new OperationCanceledException(cancelacion.Token);
        };
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 2, 3, 4, 5, 6, 7), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), cancelacion.Token);

        cancelacion.IsCancellationRequested.Should().BeTrue("control: la cancelación se pidió durante el borrado");
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada);
        interno.VecesGuardado.Should().Be(1, "solo la desconexión");
    }

    /// <summary>
    /// Control de la regla anterior: si la suscripción sigue vigente al confirmar la desconexión, la conexión ya
    /// Deshabilitada no se marca (la regla sobre una conexión no Habilitada marcaría también una suscripción vigente).
    /// </summary>
    [Fact]
    public async Task Con_la_suscripcion_vigente_al_confirmar_y_un_DELETE_con_exito_no_se_marca_nada()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(reloj.AhoraUtc.AddMinutes(1), reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        // La suscripción expira después de la confirmación, ya durante el DELETE: la desconexión ya era efectiva.
        graph.AlEliminarSuscripcion = () => reloj.Avanzar(TimeSpan.FromMinutes(2));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), unitOfWork, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
        unitOfWork.VecesGuardado.Should().Be(2, "la desconexión y el vaciado de lo que quedaba por registrar");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull("el borrado se confirmó");
    }

    /// <summary>
    /// El guardado de la marca previo a Graph colisiona (la ingesta escribió la conexión): se recarga, se vuelve a poner
    /// la marca y la desconexión sigue sobre la versión recargada, no sobre la instancia ya descartada.
    /// </summary>
    [Fact]
    public async Task Una_colision_del_guardado_previo_recarga_y_desconecta_la_version_recargada()
    {
        var expiracion = DateTime.UtcNow.AddHours(-3);
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        suscripciones.Suscripciones.Clear();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", expiracion));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: false));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 1), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var recargada = conexiones.Conexiones[0];
        recargada.Should().NotBeSameAs(conexion, "control: el descarte cargó la versión de la base");
        recargada.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        recargada.HuecoPendienteDesdeUtc.Should().Be(expiracion);
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"]);
        interno.VecesGuardado.Should().Be(2, "la marca reaplicada y la desconexión");
    }

    /// <summary>
    /// El guardado de la marca previo a Graph agota las colisiones: la excepción sale sin pedir el token, sin deshabilitar
    /// y sin borrar. Ninguna llamada externa sale sin la marca guardada.
    /// </summary>
    [Fact]
    public async Task Si_el_guardado_previo_agota_las_colisiones_no_se_llama_a_Graph_ni_se_deshabilita()
    {
        var expiracion = DateTime.UtcNow.AddHours(-3);
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        suscripciones.Suscripciones.Clear();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", expiracion));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: false));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 1, 2, 3), descarte: descarte);

        var desconectar = () => handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        await desconectar.Should().ThrowAsync<DbUpdateConcurrencyException>();
        graph.SuscripcionesEliminadas.Should().BeEmpty();
        graph.VecesRefrescado.Should().Be(0, "el token tampoco se pidió");
        conexiones.Conexiones[0].Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        interno.VecesGuardado.Should().Be(0);
        descarte.Veces.Should().Be(DesconectarBuzonCommandHandler.IntentosGuardadoMarca);
    }

    /// <summary>
    /// Un fallo del guardado previo que no es una colisión (base caída) descarta lo rastreado antes de salir: la marca no
    /// queda pendiente para que la guarde otra operación del circuito. No se llama a Graph.
    /// </summary>
    [Fact]
    public async Task Un_fallo_del_guardado_previo_que_no_es_colision_descarta_y_no_llama_a_Graph()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        suscripciones.Suscripciones.Clear();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddHours(-3)));
        var graph = new Microsoft365GraphClientFalso();
        var descarte = new DescarteContado();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("base caída") }, descarte: descarte);

        var desconectar = () => handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        await desconectar.Should().ThrowAsync<InvalidOperationException>();
        descarte.Veces.Should().Be(1);
        graph.SuscripcionesEliminadas.Should().BeEmpty();
        graph.VecesRefrescado.Should().Be(0);
    }

    /// <summary>Bajo B5 del revisor-puente (ronda 13): la visibilidad se vuelve a comprobar con el estado releído en el cerrojo.</summary>
    [Fact]
    public async Task La_visibilidad_se_vuelve_a_comprobar_dentro_del_cerrojo()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceQueRetiraLaConexion(consultasVisibles: 1);
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), alcance,
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        alcance.Consultas.Should().Be(2, "control: la segunda consulta es la del estado releído");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        unitOfWork.VecesGuardado.Should().Be(0);
        graph.SuscripcionesEliminadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_borrado_que_lanza_deja_la_marca()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ExcepcionEliminacion = new TaskCanceledException("HttpClient.Timeout") };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().NotBeNull();
        unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task La_cancelacion_pedida_durante_el_borrado_se_propaga_y_deja_la_marca_guardada()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        using var cancelacion = new CancellationTokenSource();
        var graph = new Microsoft365GraphClientFalso();
        graph.AlEliminarSuscripcion = () =>
        {
            cancelacion.Cancel();
            graph.ExcepcionEliminacion = new OperationCanceledException(cancelacion.Token);
        };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var accion = () => handler.Handle(new DesconectarBuzonCommand(conexion.Id), cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
        conexion.HuecoPendienteDesdeUtc.Should().NotBeNull();
        unitOfWork.VecesGuardado.Should().Be(2, "la desconexión y la marca, esta sin la cancelación del llamador");
        unitOfWork.TokensDeGuardado.Should().Equal(cancelacion.Token, CancellationToken.None);
    }

    // Ronda 12, R3: la marca se guarda siempre sin la cancelación del llamador, sea cual sea el camino del fallo.

    [Fact]
    public async Task Un_borrado_fallido_guarda_la_marca_sin_el_token_del_llamador()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        using var vivo = new CancellationTokenSource();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), vivo.Token);

        resultado.EsExitoso.Should().BeTrue();
        conexion.HuecoPendienteDesdeUtc.Should().NotBeNull();
        unitOfWork.TokensDeGuardado.Should().Equal(vivo.Token, CancellationToken.None);
    }

    [Fact]
    public async Task Un_borrado_que_devuelve_fallo_con_la_cancelacion_ya_pedida_guarda_la_marca_y_propaga_la_cancelacion()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        using var cancelacion = new CancellationTokenSource();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        graph.AlEliminarSuscripcion = cancelacion.Cancel;
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var accion = () => handler.Handle(new DesconectarBuzonCommand(conexion.Id), cancelacion.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
        conexion.HuecoPendienteDesdeUtc.Should().NotBeNull();
        unitOfWork.TokensDeGuardado.Should().Equal(cancelacion.Token, CancellationToken.None);
    }

    // Ronda 12, P2: un primer guardado fallido no deja la Deshabilitada rastreada ni toca Graph.

    [Fact]
    public async Task Si_el_guardado_de_la_desconexion_falla_descarta_lo_rastreado_y_no_borra_en_Graph()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("base caída") };
        var descarte = new DescarteContado();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork, descarte: descarte);

        var accion = () => handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>();
        descarte.Veces.Should().Be(1);
        graph.SuscripcionesEliminadas.Should().BeEmpty();
    }

    // Ronda 12, R1: Desconectar decide con el estado releído dentro del cerrojo, no con el cargado antes.

    [Fact]
    public async Task Una_reactivacion_entre_la_carga_y_el_cerrojo_se_desconecta_entera_con_su_reclamacion_y_su_suscripcion()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion("sub-vieja");
        conexion.FijarIdentidadBuzon("inbox-x");
        conexion.Deshabilitar();
        conexiones.SimularRastreo = true;
        suscripciones.SimularRastreo = true;
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        ConexionIntegracion? reactivada = null;
        cerrojo.AlTomar = id =>
        {
            // Otro Reactivar terminó entre la carga (Deshabilitada) y el cerrojo: Habilitada, reclamada y con suscripción nueva.
            reactivada = conexiones.SustituirEnBase(id, c => c.MarcarIdentidadVerificada(DateTime.UtcNow));
            reclamaciones.Reclamar(new ReclamacionBuzonIntegracion(conexion.BuzonEmail, "inbox-x", Guid.NewGuid(), id, DateTime.UtcNow));
            suscripciones.SustituirEnBase(id, "sub-nueva", DateTime.UtcNow.AddDays(2));
        };
        var handler = CrearHandler(conexiones, suscripciones, reclamaciones, new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork, cerrojo);
        (await suscripciones.ObtenerPorConexionAsync(conexion.Id)).Should().NotBeNull("control: la suscripción vieja queda rastreada");

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        reactivada!.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada, "se desconecta la versión releída, no la obsoleta");
        reclamaciones.Reclamaciones.Should().BeEmpty("la reclamación que existe ahora se libera junto a la Deshabilitada");
        graph.SuscripcionesEliminadas.Should().Equal("sub-nueva");
        unitOfWork.VecesGuardado.Should().Be(2, "la desconexión y el vaciado tras el borrado confirmado");
    }

    [Fact]
    public async Task Una_desconexion_entre_la_carga_y_el_cerrojo_no_deja_nada_que_hacer()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        conexiones.SimularRastreo = true;
        var reclamaciones = new ReclamacionBuzonIntegracionRepositorioFalso();
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var cerrojo = new CerrojoVerificacionBuzonFalso();
        cerrojo.AlTomar = id => conexiones.SustituirEnBase(id, c => c.Deshabilitar());
        var handler = CrearHandler(conexiones, suscripciones, reclamaciones, new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork, cerrojo);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cerrojo.Pedidos.Should().Equal([conexion.Id], "control: el cerrojo sí se tomó");
        unitOfWork.VecesGuardado.Should().Be(0);
        graph.SuscripcionesEliminadas.Should().BeEmpty("quien la desconectó ya borró su suscripción");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "la instancia obsoleta no se toca");
    }

    // Codex, ronda 17, hallazgo medio: tras ConexionIntegracion.MarcaHuecoNoGuardada, volver a desconectar la conexión ya
    // Deshabilitada devolvía éxito sin intentar guardar la marca, y el fallo parecía resuelto.

    /// <summary>
    /// El escenario entero: la primera desconexión devuelve el fallo de la marca (la suscripción expiró durante el
    /// guardado de la desconexión, el guardado de la marca y su recarga fallan, el DELETE tiene éxito) y la segunda,
    /// sobre la conexión ya Deshabilitada, registra lo que la desconexión guardó por registrar (la hora de antes del
    /// guardado, anterior a la expiración: avisa de más, nunca de menos) sin volver a llamar a Graph (Codex, ronda 18).
    /// </summary>
    [Fact]
    public async Task Tras_el_fallo_de_la_marca_volver_a_desconectar_registra_el_comienzo_guardado()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var antesDelGuardado = reloj.AhoraUtc;
        var expiracion = reloj.AhoraUtc.AddMinutes(1);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        interno.AlGuardar = () =>
        {
            if (interno.VecesGuardado == 0)
                reloj.Avanzar(TimeSpan.FromMinutes(2));
        };
        var recargaRota = false;
        conexiones.AlLeerActual = _ =>
        {
            if (recargaRota)
                throw new InvalidOperationException("base caída");
        };
        // La base guarda la Deshabilitada sin marca y con lo que quedó por registrar: es lo que se lee tras cada descarte.
        DateTime? porRegistrarGuardado = null;
        interno.AlGuardar += () => porRegistrarGuardado ??= conexion.MarcaHuecoPorRegistrarDesdeUtc;
        var descarte = new DescarteContado(() =>
        {
            recargaRota = true;
            conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true, porRegistrarGuardado);
        });
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte, reloj: reloj);

        var primero = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        primero.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada, "control: el primer intento no la guardó");
        porRegistrarGuardado.Should().Be(antesDelGuardado, "viaja en el mismo guardado que la Deshabilitada");
        conexiones.Conexiones[0].Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada, "control");
        conexiones.Conexiones[0].HuecoPendienteDesdeUtc.Should().BeNull("control: la marca no llegó a la base");

        recargaRota = false;
        var segundo = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        segundo.EsExitoso.Should().BeTrue();
        conexiones.Conexiones[0].HuecoPendienteDesdeUtc.Should().Be(antesDelGuardado);
        conexiones.Conexiones[0].MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull();
        interno.VecesGuardado.Should().Be(2, "la desconexión del primer intento y la marca reparada en el segundo");
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"], "el segundo intento no vuelve a llamar a Graph");
    }

    /// <summary>Si al repetir la desconexión la marca tampoco llega a la base, el mismo fallo visible, no un éxito.</summary>
    [Fact]
    public async Task Si_la_marca_reparada_tampoco_llega_a_la_base_devuelve_el_mismo_fallo()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = reloj.AhoraUtc.AddMinutes(-5);
        var porRegistrar = reloj.AhoraUtc.AddMinutes(-10);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        conexion.Deshabilitar(porRegistrar);
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso { ExcepcionAlGuardar = new InvalidOperationException("base caída") };
        // Tras cada descarte se lee la versión de la base, sin la marca que el intento dejó en memoria.
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true, porRegistrar));
        var logger = new LoggerCaptura();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), interno, descarte: descarte, reloj: reloj, logger: logger);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada);
        descarte.Veces.Should().Be(ReparacionMarcaHuecoService.IntentosGuardado, "cada intento parte de la base");
        conexiones.Conexiones[0].MarcaHuecoPorRegistrarDesdeUtc.Should().Be(porRegistrar, "sigue guardada para la próxima reparación");
        graph.SuscripcionesEliminadas.Should().BeEmpty();
        logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Mensaje.Should().Contain(conexion.Id.ToString());
    }

    /// <summary>
    /// Sin nada por registrar en la conexión, repetir la desconexión no deja marca ni guarda, esté la suscripción vigente
    /// o expirada: la conexión se desconectó sin problemas (o su marca ya está).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repetir_la_desconexion_sin_marca_que_reparar_devuelve_exito_sin_guardar(bool expiradaYaMarcada)
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var expiracion = expiradaYaMarcada ? reloj.AhoraUtc.AddMinutes(-5) : reloj.AhoraUtc.AddDays(2);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(expiracion, reloj.AhoraUtc.AddHours(-1));
        conexion.Deshabilitar();
        if (expiradaYaMarcada)
            conexion.RegistrarHuecoAlObservarSuscripcion(expiracion, reloj.AhoraUtc).Should().BeTrue("control: la marca ya está");
        var graph = new Microsoft365GraphClientFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), unitOfWork, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
        conexion.HuecoPendienteDesdeUtc.Should().Be(expiradaYaMarcada ? expiracion : null);
        graph.SuscripcionesEliminadas.Should().BeEmpty();
    }

    /// <summary>
    /// Codex, ronda 18, hallazgo medio (DesconectarBuzonCommand.cs:155): el borrado de Graph falla, el guardado de la marca
    /// y su reintento también, y la primera desconexión devuelve MarcaHuecoNoGuardada. Repetirla con la suscripción aún
    /// vigente devolvía éxito sin guardar nada, a la espera de una notificación que no está garantizada. Ahora la base
    /// guarda el comienzo por registrar con la Deshabilitada, y la repetición lo registra aunque la suscripción siga
    /// vigente y sin volver a llamar a Graph.
    /// </summary>
    [Fact]
    public async Task Con_la_suscripcion_vigente_repetir_la_desconexion_tras_un_borrado_fallido_registra_el_comienzo_guardado()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var antesDelGuardado = reloj.AhoraUtc;
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(reloj.AhoraUtc.AddDays(2), reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        DateTime? porRegistrarGuardado = null;
        interno.AlGuardar = () =>
        {
            porRegistrarGuardado ??= conexion.MarcaHuecoPorRegistrarDesdeUtc;
            reloj.Avanzar(TimeSpan.FromSeconds(1));
        };
        var recargaRota = false;
        conexiones.AlLeerActual = _ =>
        {
            if (recargaRota)
                throw new InvalidOperationException("base caída");
        };
        var descarte = new DescarteContado(() =>
        {
            recargaRota = true;
            conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true, porRegistrarGuardado);
        });
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(),
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte, reloj: reloj);

        var primero = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        primero.Error.Codigo.Should().Be(DesconectarBuzonCommandHandler.CodigoMarcaHuecoNoGuardada, "control: la marca no llegó");
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"], "control: el DELETE se pidió y falló");
        conexiones.Conexiones[0].HuecoPendienteDesdeUtc.Should().BeNull("control: la marca no llegó a la base");
        conexiones.Conexiones[0].MarcaHuecoPorRegistrarDesdeUtc.Should().Be(antesDelGuardado, "se guardó con la Deshabilitada");

        recargaRota = false;
        var segundo = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        segundo.EsExitoso.Should().BeTrue();
        conexiones.Conexiones[0].HuecoPendienteDesdeUtc.Should().Be(antesDelGuardado, "el comienzo real del hueco, no la expiración");
        conexiones.Conexiones[0].MarcaHuecoPorRegistrarDesdeUtc.Should().BeNull();
        graph.SuscripcionesEliminadas.Should().Equal(["sub-1"], "la repetición no vuelve a llamar a Graph");
    }

    /// <summary>
    /// Un borrado confirmado con la suscripción vigente no deja nada por registrar en la base: el guardado de después del
    /// DELETE vacía el valor que viajó con la Deshabilitada, sin marca.
    /// </summary>
    [Fact]
    public async Task Un_borrado_con_exito_y_la_suscripcion_vigente_vacia_lo_que_quedaba_por_registrar()
    {
        var reloj = new RelojFalso(DateTime.UtcNow);
        var (conexion, conexiones, suscripciones) = BuzonCompartidoQueExpiraEn(reloj.AhoraUtc.AddDays(2), reloj.AhoraUtc.AddHours(-1));
        var graph = new Microsoft365GraphClientFalso();
        var interno = new UnitOfWorkFalso();
        var porRegistrarEnCadaGuardado = new List<DateTime?>();
        interno.AlGuardar = () => porRegistrarEnCadaGuardado.Add(conexion.MarcaHuecoPorRegistrarDesdeUtc);
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso(), interno, reloj: reloj);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        porRegistrarEnCadaGuardado.Should().Equal([reloj.AhoraUtc, null], "con la Deshabilitada y vaciado tras el DELETE");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull("no hubo hueco");
    }

    // Ronda 10, hallazgo medio: primero se guarda la desconexión y después se borra en Graph.

    [Fact]
    public async Task Guarda_la_desconexion_antes_de_borrar_la_suscripcion_en_Graph()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddDays(2)));
        var unitOfWork = new UnitOfWorkFalso();
        var guardadosAlBorrar = -1;
        var graph = new Microsoft365GraphClientFalso();
        graph.AlEliminarSuscripcion = () => guardadosAlBorrar = unitOfWork.VecesGuardado;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        guardadosAlBorrar.Should().Be(1, "la Deshabilitada ya estaba guardada cuando se pidió el borrado");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull("el borrado fue bien");
    }

    [Fact]
    public async Task Si_el_borrado_falla_la_marca_queda_en_el_momento_del_borrado_y_sigue_Deshabilitada()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        var conexiones = new ConexionIntegracionRepositorioFalso();
        conexiones.Agregar(conexion);
        var suscripciones = new SuscripcionWebhookRepositorioFalso();
        suscripciones.Agregar(new SuscripcionWebhook(conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddDays(2)));
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var unitOfWork = new UnitOfWorkFalso();
        var antes = DateTime.UtcNow;
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        conexion.HuecoPendienteDesdeUtc.Should().BeOnOrAfter(antes).And.BeOnOrBefore(DateTime.UtcNow);
        unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Deshabilita_una_conexion_habilitada_y_libera_la_reclamacion_del_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepositorioFalso();
        reclamacionRepositorio.Reclamar(new ReclamacionBuzonIntegracion("cae@cliente.com", null, Guid.NewGuid(), conexion.Id, DateTime.UtcNow));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new SuscripcionWebhookRepositorioFalso(), reclamacionRepositorio, new AlcanceDatosServiceFalso(),
            new Microsoft365GraphClientFalso(), new CredencialIntegracionRepositorioFalso(), unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
        unitOfWork.VecesGuardado.Should().Be(1);
        reclamacionRepositorio.Reclamaciones.Should().BeEmpty("desconectar debe dejar el buzón disponible para cualquier tenant");
    }

    /// <summary>
    /// Un buzón compartido no tiene credencial: la baja de su suscripción pide el token de aplicación de su directorio
    /// (la baja no lleva buzón en la ruta, <c>/subscriptions/{id}</c>, así que no hay <c>BuzonGraph</c> que comprobar).
    /// </summary>
    [Fact]
    public async Task Con_un_buzon_compartido_elimina_la_suscripcion_con_el_token_de_aplicacion_del_directorio()
    {
        var directorio = Guid.NewGuid().ToString("D");
        var conexion = ConexionIntegracion.CrearBuzonCompartido("piloto@tenant.com", "Buzón piloto", directorio, Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexion);
        var suscripcionRepositorio = new SuscripcionWebhookRepositorioFalso();
        suscripcionRepositorio.Agregar(new SuscripcionWebhook(conexion.Id, "graph-sub-1", "secreto-compartido", DateTime.UtcNow.AddDays(2)));
        var graphClient = new Microsoft365GraphClientFalso();
        var handler = CrearHandler(
            conexionRepositorio, suscripcionRepositorio, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graphClient, new CredencialIntegracionRepositorioFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        graphClient.DirectoriosConTokenDeAplicacion.Should().Equal(directorio);
        graphClient.VecesRefrescado.Should().Be(0, "no hay token delegado que refrescar");
        graphClient.SuscripcionesEliminadas.Should().Equal("graph-sub-1");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
    }

    [Fact]
    public async Task No_desconecta_el_buzon_personal_de_otro_gestor_aunque_se_le_pase_su_Id()
    {
        // Mismo hallazgo que EnviarMensajeNuevoCommandHandlerTests: un buzón
        // personal tiene ClienteId null, igual que el genérico del tenant —
        // sin ConexionIntegracionVisibleAsync, cualquiera con acceso a
        // Comunicaciones podía desconectar (y borrar la suscripción de Graph
        // de) el buzón personal de un colega pasando su Id a mano.
        var conexionPersonal = new ConexionIntegracion(
            "gestor.otro@ejemplo.local", "Buzón personal", gestorPropietarioId: Guid.NewGuid());
        var conexionRepositorio = new ConexionIntegracionRepositorioFalso();
        conexionRepositorio.Agregar(conexionPersonal);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = CrearHandler(
            conexionRepositorio, new SuscripcionWebhookRepositorioFalso(), new ReclamacionBuzonIntegracionRepositorioFalso(),
            new AlcanceDatosServiceFalso(conexionIntegracionVisible: false),
            new Microsoft365GraphClientFalso(), new CredencialIntegracionRepositorioFalso(), unitOfWork);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexionPersonal.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        conexionPersonal.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
