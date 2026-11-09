using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

public class DesconectarBuzonCommandHandlerTests
{
    private static DesconectarBuzonCommandHandler CrearHandler(
        ConexionIntegracionRepositorioFalso conexionRepositorio, SuscripcionWebhookRepositorioFalso suscripcionRepositorio,
        ReclamacionBuzonIntegracionRepositorioFalso reclamacionRepositorio,
        AlcanceDatosServiceFalso alcanceDatos, Microsoft365GraphClientFalso graphClient, CredencialIntegracionRepositorioFalso credencialRepositorio,
        IUnitOfWork unitOfWork, CerrojoVerificacionBuzonFalso? cerrojo = null, IDescarteCambiosPendientes? descarte = null) =>
        new(conexionRepositorio, suscripcionRepositorio, reclamacionRepositorio, alcanceDatos, graphClient,
            new AccesoGraphService(credencialRepositorio, graphClient), unitOfWork,
            cerrojo ?? new CerrojoVerificacionBuzonFalso(), descarte ?? new DescarteContado(),
            NullLogger<DesconectarBuzonCommandHandler>.Instance);

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

    private static ConexionIntegracion Copia(ConexionIntegracion original, bool deshabilitada)
    {
        var copia = new ConexionIntegracion(original.BuzonEmail, original.Nombre);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(copia, original.Id);
        if (deshabilitada)
            copia.Deshabilitar();
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

    [Fact]
    public async Task Agotados_los_intentos_de_la_marca_no_lanza_y_descarta_lo_pendiente()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado(() => conexiones.Conexiones[0] = Copia(conexion, deshabilitada: true));
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkConColisiones(interno, 2, 3, 4), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la desconexión ya quedó guardada");
        descarte.Veces.Should().Be(DesconectarBuzonCommandHandler.IntentosGuardadoMarca);
        interno.VecesGuardado.Should().Be(1, "solo la desconexión");
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
    /// se descarta, pero no tapa el resultado de la desconexión, que ya está confirmada en la base.
    /// </summary>
    [Fact]
    public async Task Un_fallo_ajeno_a_la_concurrencia_al_guardar_la_marca_no_tapa_la_desconexion_confirmada()
    {
        var (conexion, conexiones, suscripciones) = ConSuscripcion();
        var graph = new Microsoft365GraphClientFalso { ErrorEliminacion = CaeManager.Domain.Common.Error.Crear("x", "503") };
        var interno = new UnitOfWorkFalso();
        var descarte = new DescarteContado();
        var handler = CrearHandler(conexiones, suscripciones, new ReclamacionBuzonIntegracionRepositorioFalso(), new AlcanceDatosServiceFalso(),
            graph, new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true },
            new UnitOfWorkQueFallaEn(interno, 2, new InvalidOperationException("base caída")), descarte: descarte);

        var resultado = await handler.Handle(new DesconectarBuzonCommand(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la desconexión ya quedó guardada");
        interno.VecesGuardado.Should().Be(1, "solo la desconexión");
        descarte.Veces.Should().Be(1);
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
        unitOfWork.VecesGuardado.Should().Be(1);
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
