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
/// Issue #2124: la recuperación del hueco de correo lee la Bandeja de entrada desde la marca, sigue
/// <c>@odata.nextLink</c>, guarda el cursor con cada página y solo limpia la marca tras una lectura completa e ingerida.
/// </summary>
public class RecuperacionHuecoCorreoServiceTests
{
    private const string Continuacion1 = "https://graph.microsoft.com/v1.0/continuacion-1";
    private const string Continuacion2 = "https://graph.microsoft.com/v1.0/continuacion-2";

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

        public void Pagina(string? siguiente, params MensajeRecibidoGraphDto[] mensajes) =>
            Graph.PaginasRecibidos.Add(Result.Exito(new PaginaMensajesRecibidosGraphDto(mensajes, siguiente)));

        public MensajeRecibidoGraphDto Mensaje(string id, int minutosTrasLaMarca) => new(id, Marca.AddMinutes(minutosTrasLaMarca));

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
    public async Task Sigue_la_continuacion_hasta_el_final_y_limpia_la_marca_solo_tras_ingerir_lo_leido()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10), escenario.Mensaje("m2", 20));
        escenario.Pagina(null, escenario.Mensaje("m3", 30));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);

        escenario.Graph.ListadosRecibidosPedidos.Select(l => l.Enlace).Should().Equal(null, Continuacion1);
        escenario.Graph.ListadosRecibidosPedidos[0].DesdeUtc.Should().Be(escenario.Marca, "lee desde la marca");
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
    public async Task Una_pagina_corta_con_continuacion_no_es_el_final()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10));
        escenario.Pagina(Continuacion2);
        escenario.Pagina(null, escenario.Mensaje("m2", 40));

        await escenario.PasadaAsync();

        escenario.Graph.ListadosRecibidosPedidos.Select(l => l.Enlace).Should().Equal(null, Continuacion1, Continuacion2);
        escenario.Eventos.Eventos.Should().HaveCount(2, "una página vacía no encola nada");
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca.AddMinutes(40));
    }

    [Fact]
    public async Task Un_fallo_en_la_segunda_pagina_no_limpia_la_marca_y_la_pasada_siguiente_reanuda_desde_la_continuacion()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10), escenario.Mensaje("m2", 20));
        escenario.Graph.PaginasRecibidos.Add(Result.Fallo<PaginaMensajesRecibidosGraphDto>(
            Error.Crear("Integraciones.Microsoft365.ListadoIlegible", "200 sin value")));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Interrumpida);

        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeFalse();
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca.AddMinutes(20), "la página 1 quedó guardada");
        escenario.Conexion.RecuperacionHuecoSiguienteEnlace.Should().Be(Continuacion1, "con su continuación");
        escenario.Eventos.Eventos.Should().ContainSingle();

        escenario.Pagina(null, escenario.Mensaje("m3", 25));
        escenario.IngerirTodo();

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);

        escenario.Graph.ListadosRecibidosPedidos[2].Enlace.Should().Be(Continuacion1, "reanuda desde la continuación guardada");
        escenario.Conexion.RecuperacionHuecoCubreDesdeUtc.Should().Be(escenario.Marca, "sigue siendo la misma recuperación");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Una_continuacion_guardada_que_Graph_rechaza_se_olvida_y_la_pasada_siguiente_lista_desde_el_cursor()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10));
        escenario.Graph.PaginasRecibidos.Add(Result.Fallo<PaginaMensajesRecibidosGraphDto>(
            Error.Crear(CodigosErrorMicrosoft365.Transitorio, "503")));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Interrumpida);
        escenario.Conexion.RecuperacionHuecoSiguienteEnlace.Should().Be(Continuacion1);

        // Pasada 2: la continuación heredada falla de forma definitiva (caducada) y se olvida.
        escenario.Graph.PaginasRecibidos.Add(Result.Fallo<PaginaMensajesRecibidosGraphDto>(
            Error.Crear("Integraciones.Microsoft365.ErrorApi", "skiptoken caducado")));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Interrumpida);
        escenario.Graph.ListadosRecibidosPedidos[2].Enlace.Should().Be(Continuacion1);
        escenario.Conexion.RecuperacionHuecoSiguienteEnlace.Should().BeNull();
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);

        // Pasada 3: vuelve al cursor.
        escenario.Pagina(null, escenario.Mensaje("m1", 10), escenario.Mensaje("m2", 20));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);
        escenario.Graph.ListadosRecibidosPedidos[3].Enlace.Should().BeNull();
        escenario.Graph.ListadosRecibidosPedidos[3].DesdeUtc.Should().Be(escenario.Marca.AddMinutes(10));
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Una_continuacion_guardada_con_fallo_transitorio_se_conserva()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10));
        escenario.Graph.PaginasRecibidos.Add(Result.Fallo<PaginaMensajesRecibidosGraphDto>(
            Error.Crear(CodigosErrorMicrosoft365.Transitorio, "503")));
        await escenario.PasadaAsync();

        escenario.Graph.PaginasRecibidos.Add(Result.Fallo<PaginaMensajesRecibidosGraphDto>(
            Error.Crear(CodigosErrorMicrosoft365.TiempoAgotado, "sin respuesta")));

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Interrumpida);
        escenario.Conexion.RecuperacionHuecoSiguienteEnlace.Should().Be(Continuacion1);
    }

    [Fact]
    public async Task El_canje_del_token_no_recibe_la_cancelacion_del_llamador()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(null);
        using var cancelacion = new CancellationTokenSource();

        await escenario.PasadaAsync(cancelacion.Token);

        escenario.Graph.CancelacionesDeToken.Should().NotBeEmpty()
            .And.OnlyContain(c => !c.CanBeCanceled, "un canje que ya rotó la credencial tiene que llegar a guardarse");
    }

    [Fact]
    public async Task Una_perdida_registrada_a_mitad_anula_la_recuperacion_y_no_guarda_la_pagina()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10));
        escenario.Pagina(null, escenario.Mensaje("m2", 20));
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
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10));
        escenario.Pagina(null, escenario.Mensaje("m2", 20));
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
        escenario.Pagina(null, escenario.Mensaje("m1", 10));

        await escenario.PasadaAsync();
        escenario.IngerirTodo();

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.SinRecuperar);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
    }

    [Fact]
    public async Task Un_evento_de_recuperacion_retenido_impide_limpiar()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        escenario.Pagina(null, escenario.Mensaje("m1", 10));

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
        escenario.Pagina(null);

        await escenario.PasadaAsync();

        var desde = escenario.Graph.ListadosRecibidosPedidos.Single().DesdeUtc;
        desde.Should().BeCloseTo(DateTime.UtcNow - ConexionIntegracion.AntiguedadMaximaRecuperacion, TimeSpan.FromMinutes(1));
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Truncada);
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);
        escenario.Graph.ListadosRecibidosPedidos.Should().ContainSingle("terminada y truncada, no se vuelve a listar");
    }

    [Fact]
    public async Task El_tope_de_mensajes_por_pasada_corta_y_la_siguiente_sigue_la_continuacion_guardada()
    {
        var escenario = new Escenario(TimeSpan.FromHours(30));
        for (var pagina = 0; pagina < 6; pagina++)
        {
            var mensajes = Enumerable.Range(0, 50)
                .Select(i => escenario.Mensaje($"p{pagina}-{i}", pagina * 50 + i))
                .ToArray();
            escenario.Pagina($"https://graph.microsoft.com/v1.0/p{pagina + 1}", mensajes);
        }

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Parcial);

        escenario.Graph.ListadosRecibidosPedidos.Should().HaveCount(
            RecuperacionHuecoCorreoService.MaximoMensajesPorPasada / 50);
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca.AddMinutes(249));
        escenario.Conexion.HuecoPendienteDesdeUtc.Should().Be(escenario.Marca);

        // Pasada siguiente: sigue la continuación guardada, no vuelve a pedir los mismos 250 desde el cursor.
        escenario.Pagina(null);
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);
        escenario.Graph.ListadosRecibidosPedidos[5].Enlace.Should().Be("https://graph.microsoft.com/v1.0/p5");
        escenario.Graph.ListadosRecibidosPedidos[6].Enlace.Should().Be("https://graph.microsoft.com/v1.0/p6");
        escenario.Eventos.Eventos.SelectMany(e => e.PayloadCrudo.Split(':')[1].Split(','))
            .Should().OnlyHaveUniqueItems("ningún mensaje se encola dos veces");
        escenario.Conexion.RecuperacionHuecoListadoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task Con_mas_de_250_mensajes_del_mismo_segundo_la_pasada_siguiente_avanza_por_la_continuacion()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        for (var pagina = 0; pagina < 5; pagina++)
            escenario.Pagina($"https://graph.microsoft.com/v1.0/mismo{pagina + 1}",
                Enumerable.Range(0, 50).Select(i => escenario.Mensaje($"s{pagina}-{i}", 10)).ToArray());

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Parcial);
        escenario.Conexion.RecuperacionHuecoCursorUtc.Should().Be(escenario.Marca.AddMinutes(10), "el cursor no puede pasar de ese segundo");

        escenario.Pagina(null, escenario.Mensaje("posterior", 60));
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.PendienteIngesta);

        escenario.Graph.ListadosRecibidosPedidos[5].Enlace.Should().Be("https://graph.microsoft.com/v1.0/mismo5");
        escenario.Eventos.Eventos.Last().PayloadCrudo.Should().Be("recuperacion:posterior", "el correo posterior se alcanza");
    }

    [Fact]
    public async Task El_tope_de_paginas_corta_una_continuacion_de_paginas_vacias()
    {
        var escenario = new Escenario(TimeSpan.FromHours(3));
        for (var pagina = 0; pagina < RecuperacionHuecoCorreoService.MaximoPaginasPorPasada * 2 + 5; pagina++)
            escenario.Pagina($"https://graph.microsoft.com/v1.0/vacia{pagina}");

        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Parcial);

        escenario.Graph.ListadosRecibidosPedidos.Should().HaveCount(RecuperacionHuecoCorreoService.MaximoPaginasPorPasada);
        escenario.Eventos.Eventos.Should().BeEmpty();

        // Pasada siguiente: la continuación de la página 20, no la primera página otra vez.
        (await escenario.PasadaAsync()).Should().Be(ResultadoRecuperacionHueco.Parcial);
        escenario.Graph.ListadosRecibidosPedidos[RecuperacionHuecoCorreoService.MaximoPaginasPorPasada].Enlace
            .Should().Be($"https://graph.microsoft.com/v1.0/vacia{RecuperacionHuecoCorreoService.MaximoPaginasPorPasada - 1}");
        escenario.Graph.ListadosRecibidosPedidos.Select(l => l.Enlace).Where(e => e is null)
            .Should().ContainSingle("solo la primera pasada pide la primera página");
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
        escenario.Pagina(Continuacion1, escenario.Mensaje("m1", 10));
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
