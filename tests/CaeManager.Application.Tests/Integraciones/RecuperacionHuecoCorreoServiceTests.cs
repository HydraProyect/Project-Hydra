using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Issue #2124: la recuperación del hueco de correo lista la carpeta Inbox desde la marca, pide cada página desde el
/// cursor que dejó la anterior (nunca sigue la continuación posicional de Graph), guarda el cursor con cada página y solo
/// limpia la marca tras una lectura completa e ingerida.
/// </summary>
public class RecuperacionHuecoCorreoServiceTests
{
    private sealed class DescarteContado : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes() => Veces++;
    }

    private sealed class Escenario
    {
        public DateTime Marca { get; }
        public ConexionIntegracion Conexion { get; }
        public ConexionIntegracionRepositorioFalso Conexiones { get; } = new();
        public EventoWebhookRepositorioFalso Eventos { get; } = new();
        public SuscripcionWebhookRepositorioFalso Suscripciones { get; } = new();
        public Microsoft365GraphClientFalso Graph { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public CerrojoVerificacionBuzonFalso Cerrojo { get; } = new();
        public DescarteContado Descarte { get; } = new();

        public Escenario(TimeSpan antiguedadMarca)
        {
            Conexion = ConexionIntegracion.CrearBuzonCompartido(
                "cae@operador.test", "Buzón CAE", Guid.NewGuid().ToString(), Guid.NewGuid()).Verificada();
            Marca = DateTime.UtcNow - antiguedadMarca;
            Conexion.RegistrarNotificacionDescartada(Marca + ConexionIntegracion.RetrasoMaximoNotificacion);
            Conexiones.Agregar(Conexion);
            Suscripciones.Agregar(new SuscripcionWebhook(Conexion.Id, "sub-1", "estado", DateTime.UtcNow.AddDays(2)));
        }

        public RecuperacionHuecoCorreoService Servicio() =>
            new(Conexiones, Eventos, Suscripciones, Graph, new AccesoGraphService(new CredencialIntegracionRepositorioFalso(), Graph),
                UnitOfWork, Cerrojo, Descarte, NullLogger<RecuperacionHuecoCorreoService>.Instance);

        public Task<ResultadoRecuperacionHueco> PasadaAsync(CancellationToken cancellationToken = default) =>
            Servicio().RecuperarConexionAsync(Conexion.Id, cancellationToken);

        public void Pagina(bool hayMas, params MensajeRecibidoGraphDto[] mensajes) =>
            Graph.PaginasRecibidos.Add(Result.Exito(new PaginaMensajesRecibidosGraphDto(mensajes, hayMas)));

        public MensajeRecibidoGraphDto Mensaje(string id, int minutosTrasLaMarca) => new(id, Marca.AddMinutes(minutosTrasLaMarca));

        public IEnumerable<DateTime> DesdePedidos => Graph.ListadosRecibidosPedidos.Select(l => l.DesdeUtc);

        public void IngerirTodo()
        {
            foreach (var evento in Eventos.Eventos.Where(e => e.Estado == EstadoEventoWebhook.Pendiente))
            {
                evento.MarcarEnProceso();
                evento.MarcarProcesado();
            }
        }
    }

    [Fact]
    public async Task Cada_pagina_se_pide_desde_el_cursor_que_dejo_la_anterior_y_la_marca_se_limpia_solo_tras_ingerir()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: true, escenario.Mensaje("m1", 10), escenario.Mensaje("m2", 20));
        escenario.Pagina(hayMas: false, escenario.Mensaje("m2", 20), escenario.Mensaje("m3", 30));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);

        escenario.DesdePedidos.Should().Equal(
            [escenario.Marca, escenario.Marca.AddMinutes(20)], "la primera desde la marca y la segunda desde el cursor, sin continuación");
        escenario.Eventos.Eventos.Should().HaveCount(2).And.OnlyContain(e => e.EsRecuperacionHueco);
        escenario.Eventos.Eventos.Select(e => e.PayloadCrudo).Should().Equal("recuperacion:m1,m2", "recuperacion:m3");
        escenario.Eventos.Eventos.Select(e => e.CubreDesdeUtc).Should().Equal(escenario.Marca.AddMinutes(10), escenario.Marca.AddMinutes(30));
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca, "lo leído aún no se ha ingerido");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();

        escenario.IngerirTodo();

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Limpiada);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        escenario.Conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
        escenario.Graph.ListadosRecibidosPedidos.Should().HaveCount(2, "con el listado completo no se vuelve a listar");
    }

    [Fact]
    public async Task Los_empates_del_segundo_del_cursor_se_deduplican_por_id_y_no_se_encolan_dos_veces()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        // Página 1: el cursor queda en el minuto 20, con b y c empatados en ese segundo.
        escenario.Pagina(hayMas: true, escenario.Mensaje("a", 10), escenario.Mensaje("b", 20), escenario.Mensaje("c", 20));
        // Página 2, pedida con ge 20: repite b y c y trae d, del mismo segundo, y e, que avanza.
        escenario.Pagina(hayMas: true, escenario.Mensaje("b", 20), escenario.Mensaje("c", 20), escenario.Mensaje("d", 20), escenario.Mensaje("e", 25));
        // Página 3: solo repeticiones del segundo del cursor (e) y el final.
        escenario.Pagina(hayMas: false, escenario.Mensaje("e", 25));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);

        escenario.DesdePedidos.Should().Equal(escenario.Marca, escenario.Marca.AddMinutes(20), escenario.Marca.AddMinutes(25));
        escenario.Eventos.Eventos.Select(e => e.PayloadCrudo).Should().Equal(
            ["recuperacion:a,b,c", "recuperacion:d,e"], "cada Id se encola una sola vez en la pasada; una página solo de repeticiones no encola nada");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Un_id_repetido_dentro_de_la_misma_pagina_se_encola_una_vez()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: false, escenario.Mensaje("a", 10), escenario.Mensaje("a", 10), escenario.Mensaje("b", 11));

        await escenario.PasadaAsync();

        escenario.Eventos.Eventos.Select(e => e.PayloadCrudo).Should().Equal("recuperacion:a,b");
    }

    [Fact]
    public async Task Mas_de_mil_mensajes_del_mismo_segundo_atascan_la_recuperacion_falla_cerrado_y_la_marca_se_queda()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        var mismoSegundo = Enumerable.Range(0, 1000).Select(i => escenario.Mensaje($"s{i}", 10)).ToArray();
        escenario.Pagina(hayMas: true, escenario.Mensaje("antes", 5));
        // Una página entera (el $top del adaptador) del segundo del cursor, con más detrás.
        escenario.Pagina(hayMas: true, [escenario.Mensaje("antes", 5), .. mismoSegundo]);
        escenario.Pagina(hayMas: true, mismoSegundo);

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Atascada);

        escenario.DesdePedidos.Should().Equal(
            [escenario.Marca, escenario.Marca.AddMinutes(5), escenario.Marca.AddMinutes(10)],
            "la tercera petición desde el minuto 10 devuelve la misma página: no se repite en bucle");
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca.AddMinutes(10), "el cursor no puede pasar de ese segundo");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);

        // Pasadas siguientes: igual, aunque todo lo encolado se haya ingerido. La marca no se limpia nunca así.
        escenario.IngerirTodo();
        escenario.Pagina(hayMas: true, mismoSegundo);

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Atascada);
        escenario.DesdePedidos.Last().Should().Be(escenario.Marca.AddMinutes(10));
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
    }

    [Fact]
    public async Task Una_pagina_vacia_con_mas_detras_atasca_en_vez_de_completar()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: true);

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Atascada);

        escenario.Graph.ListadosRecibidosPedidos.Should().ContainSingle();
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
    }

    [Fact]
    public async Task Lo_nuevo_de_una_pagina_se_encola_en_eventos_de_a_lo_sumo_cincuenta_ids_en_el_mismo_guardado()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: false, [.. Enumerable.Range(0, 120).Select(i => escenario.Mensaje($"m{i}", i))]);

        await escenario.PasadaAsync();

        escenario.Eventos.Eventos.Select(e => e.PayloadCrudo.Split(',').Length).Should().Equal(
            RecuperacionHuecoCorreoService.MaximoMensajesPorEvento, RecuperacionHuecoCorreoService.MaximoMensajesPorEvento, 20);
        escenario.Eventos.Eventos.Select(e => e.CubreDesdeUtc).Should().Equal(
            escenario.Marca, escenario.Marca.AddMinutes(50), escenario.Marca.AddMinutes(100));
    }

    [Fact]
    public async Task Un_fallo_en_la_segunda_pagina_no_limpia_la_marca_y_la_pasada_siguiente_reanuda_desde_el_cursor()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: true, escenario.Mensaje("m1", 10), escenario.Mensaje("m2", 20));
        escenario.Graph.PaginasRecibidos.Add(Result.Fallo<PaginaMensajesRecibidosGraphDto>(
            Error.Crear("Integraciones.Microsoft365.ListadoIlegible", "200 sin value")));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Interrumpida);

        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca.AddMinutes(20), "la página 1 quedó guardada");
        escenario.Eventos.Eventos.Should().ContainSingle();

        escenario.Pagina(hayMas: false, escenario.Mensaje("m2", 20), escenario.Mensaje("m3", 25));
        escenario.IngerirTodo();

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);

        escenario.Graph.ListadosRecibidosPedidos[2].DesdeUtc.Should().Be(escenario.Marca.AddMinutes(20), "reanuda desde el cursor");
        escenario.Eventos.Eventos.Last().PayloadCrudo.Should().Be(
            "recuperacion:m2,m3", "lo releído de otra pasada lo deduplica la ingesta por MensajeExternoId");
        escenario.Conexion.RecuperacionHuecoCubreDesdeUtc.Should().Be(escenario.Marca, "sigue siendo la misma recuperación");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Una_pasada_sobre_una_recuperacion_anulada_y_reiniciada_con_los_mismos_valores_no_guarda_su_pagina()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: false, escenario.Mensaje("m1", 10));
        // Mientras esta pasada espera a Graph, otra réplica ve una pérdida (que no mueve la marca) y la anula, y otra
        // pasada empieza una recuperación nueva: misma cobertura, mismo cursor, sin listado completo.
        escenario.Graph.AlListarRecibidos = indice =>
        {
            if (indice != 0)
                return;
            escenario.Conexion.RegistrarNotificacionDescartada(DateTime.UtcNow.AddHours(2)).Should().BeTrue();
            escenario.Conexion.IniciarRecuperacionHueco(DateTime.UtcNow).Should().Be(escenario.Marca, "control: mismos valores");
        };

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Anulada);

        escenario.Eventos.Eventos.Should().BeEmpty("la página leída para la recuperación anulada no se encola en la nueva");
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca);
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
    }

    [Fact]
    public async Task El_canje_del_token_no_recibe_la_cancelacion_del_llamador()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: false);
        using var cancelacion = new CancellationTokenSource();

        await escenario.PasadaAsync(cancelacion.Token);

        escenario.Graph.CancelacionesDeToken.Should().NotBeEmpty()
            .And.OnlyContain(c => !c.CanBeCanceled, "un canje que ya rotó la credencial tiene que llegar a guardarse");
    }

    [Fact]
    public async Task Una_perdida_registrada_a_mitad_anula_la_recuperacion_y_no_guarda_la_pagina()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: true, escenario.Mensaje("m1", 10));
        escenario.Pagina(hayMas: false, escenario.Mensaje("m2", 20));
        // Mientras se lee la página 2, la ingesta descarta una notificación reciente: la marca no se mueve, pero la
        // recuperación se anula en la fila.
        escenario.Graph.AlListarRecibidos = indice =>
        {
            if (indice == 1)
                escenario.Conexion.RegistrarNotificacionDescartada(DateTime.UtcNow.AddHours(2)).Should().BeTrue();
        };

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Anulada);

        escenario.Eventos.Eventos.Should().ContainSingle("la página 2 no se encola sobre una recuperación anulada");
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
        escenario.Conexion.RecuperacionHuecoEnCurso.Should().BeFalse();
    }

    [Fact]
    public async Task Si_la_conexion_deja_de_estar_operativa_entre_paginas_no_vuelve_a_llamar_a_Graph()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: true, escenario.Mensaje("m1", 10));
        escenario.Pagina(hayMas: false, escenario.Mensaje("m2", 20));
        escenario.Conexiones.AlReleer = vez =>
        {
            if (vez == 2)
                escenario.Conexiones.NoOperativasEnBase.Add(escenario.Conexion.Id);
        };

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Interrumpida);

        escenario.Graph.ListadosRecibidosPedidos.Should().ContainSingle();
        escenario.Conexiones.VecesReleido.Should().Be(2, "la puerta se relee antes de cada llamada a Graph");
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
    }

    [Fact]
    public async Task Sin_suscripcion_viva_no_limpia_la_marca()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Suscripciones.SustituirEnBase(escenario.Conexion.Id, "sub-2", DateTime.UtcNow.AddMinutes(-1));
        escenario.Pagina(hayMas: false, escenario.Mensaje("m1", 10));

        await escenario.PasadaAsync();
        escenario.IngerirTodo();

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.SinRecuperar);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
    }

    [Fact]
    public async Task Un_evento_de_recuperacion_retenido_impide_limpiar()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(hayMas: false, escenario.Mensaje("m1", 10));

        await escenario.PasadaAsync();
        var evento = escenario.Eventos.Eventos.Single();
        evento.MarcarEnProceso();
        evento.EsperarVerificacion("identidad sin verificar", DateTime.UtcNow);

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
    }

    [Fact]
    public async Task Una_marca_mas_antigua_que_el_tope_lee_solo_los_ultimos_siete_dias_y_se_queda()
    {
        var escenario = new Escenario(TimeSpan.FromDays(10));
        escenario.Pagina(hayMas: false);

        await escenario.PasadaAsync();

        var desde = escenario.Graph.ListadosRecibidosPedidos.Single().DesdeUtc;
        desde.Should().BeCloseTo(DateTime.UtcNow - ConexionIntegracion.AntiguedadMaximaRecuperacion, TimeSpan.FromMinutes(1));
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Truncada);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
        escenario.Graph.ListadosRecibidosPedidos.Should().ContainSingle("terminada y truncada, no se vuelve a listar");
    }

    [Fact]
    public async Task El_tope_de_mensajes_por_pasada_corta_y_la_siguiente_sigue_desde_el_cursor()
    {
        const int porPagina = 1000;
        var escenario = new Escenario(TimeSpan.FromHours(120));
        for (var pagina = 0; pagina < 6; pagina++)
            escenario.Pagina(hayMas: true,
                [.. Enumerable.Range(0, porPagina).Select(i => escenario.Mensaje($"p{pagina}-{i}", pagina * porPagina + i))]);

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Parcial);

        var paginas = RecuperacionHuecoCorreoService.MaximoMensajesPorPasada / porPagina;
        escenario.Graph.ListadosRecibidosPedidos.Should().HaveCount(paginas);
        var cursor = escenario.Marca.AddMinutes(paginas * porPagina - 1);
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(cursor);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);

        // Pasada siguiente: desde el cursor (ge), sin repetir lo ya leído salvo el último segundo.
        escenario.Graph.PaginasRecibidos.RemoveRange(paginas, escenario.Graph.PaginasRecibidos.Count - paginas);
        escenario.Pagina(hayMas: false, escenario.Mensaje($"p{paginas - 1}-{porPagina - 1}", paginas * porPagina - 1),
            escenario.Mensaje("nuevo", paginas * porPagina + 10));
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);
        escenario.Graph.ListadosRecibidosPedidos[paginas].DesdeUtc.Should().Be(cursor);
        escenario.Eventos.Eventos.Last().PayloadCrudo.Should().Be(
            $"recuperacion:p{paginas - 1}-{porPagina - 1},nuevo", "lo releído de otra pasada lo deduplica la ingesta");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task El_tope_de_paginas_por_pasada_corta_y_la_siguiente_sigue_desde_el_cursor()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        for (var pagina = 0; pagina < RecuperacionHuecoCorreoService.MaximoPaginasPorPasada + 5; pagina++)
            escenario.Pagina(hayMas: true, escenario.Mensaje($"m{pagina}", pagina + 1));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Parcial);

        escenario.Graph.ListadosRecibidosPedidos.Should().HaveCount(RecuperacionHuecoCorreoService.MaximoPaginasPorPasada);
        var cursor = escenario.Marca.AddMinutes(RecuperacionHuecoCorreoService.MaximoPaginasPorPasada);
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(cursor);

        // Pasada siguiente: las cinco páginas que quedan y, tras ellas, una vacía sin más detrás.
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);
        escenario.Graph.ListadosRecibidosPedidos[RecuperacionHuecoCorreoService.MaximoPaginasPorPasada].DesdeUtc.Should().Be(cursor);
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Con_el_cerrojo_ocupado_no_llama_a_Graph()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Cerrojo.Ocupadas.Add(escenario.Conexion.Id);

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Ocupada);

        escenario.Graph.ListadosRecibidosPedidos.Should().BeEmpty();
        escenario.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_conexion_no_operativa_no_se_recupera()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Conexion.Deshabilitar();

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.SinRecuperar);

        escenario.Graph.ListadosRecibidosPedidos.Should().BeEmpty();
    }

    [Fact]
    public async Task La_cancelacion_no_se_traga()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        using var cancelacion = new CancellationTokenSource();
        escenario.Pagina(hayMas: true, escenario.Mensaje("m1", 10));
        escenario.Graph.AlListarRecibidos = _ => cancelacion.Cancel();

        var pasada = () => escenario.Servicio().RecuperarDelTenantActualAsync(cancelacion.Token);

        await pasada.Should().ThrowAsync<OperationCanceledException>();
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
        escenario.Descarte.Veces.Should().Be(0);
    }

    [Fact]
    public async Task Un_fallo_al_guardar_descarta_lo_rastreado_y_no_detiene_a_las_demas_conexiones()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.UnitOfWork.ExcepcionAlGuardar = new InvalidOperationException("colisión de concurrencia");

        await escenario.Servicio().RecuperarDelTenantActualAsync(CancellationToken.None);

        escenario.Descarte.Veces.Should().Be(1);
        escenario.Graph.ListadosRecibidosPedidos.Should().BeEmpty("el inicio no llegó a guardarse");
    }
}
