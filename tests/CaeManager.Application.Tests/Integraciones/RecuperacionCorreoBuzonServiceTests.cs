using CaeManager.Application.Integraciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Lectura con reintentos del hueco de correo que deja una conexión de Microsoft 365 al dejar de estar operativa
/// (<see cref="ConexionIntegracion.HuecoPendienteDesdeUtc"/>).
/// </summary>
public class RecuperacionCorreoBuzonServiceTests
{
    private readonly IntegracionesQueryContextFalso _queryContext = new();
    private readonly ConexionIntegracionRepositorioFalso _conexiones = new();
    private readonly ConversacionRepositorioFalso _conversaciones = new();
    private readonly EventoWebhookRepositorioFalso _eventos = new();
    private readonly Microsoft365GraphClientFalso _graph = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly DescarteCambiosPendientesFalso _descarte = new();

    private RecuperacionCorreoBuzonService Servicio() => new(
        _queryContext, _conexiones,
        new AccesoGraphService(new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, _graph),
        _graph, _conversaciones, _eventos, _unitOfWork, _descarte, NullLogger<RecuperacionCorreoBuzonService>.Instance);

    /// <summary>
    /// Conexión verificada, conectada hace <paramref name="conectadaHace"/>, que cayó en <paramref name="caida"/> y ya
    /// volvió a estar operativa: tiene el hueco pendiente.
    /// </summary>
    private ConexionIntegracion ConexionConHueco(DateTime caida, TimeSpan conectadaHace)
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        typeof(ConexionIntegracion).GetProperty(nameof(ConexionIntegracion.FechaConectadaUtc))!
            .SetValue(conexion, DateTime.UtcNow - conectadaHace);
        conexion.FijarIdentidadBuzon("inbox-cae");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        conexion.MarcarConError("refresh token caducado", caida);
        conexion.Rehabilitar();
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow);
        _conexiones.Agregar(conexion);
        _queryContext.ConexionesLista.Add(conexion);
        return conexion;
    }

    private static MensajeResumenGraphDto Mensaje(string id, DateTime fechaUtc) =>
        new(id, "hilo-" + id, "Asunto", "contacto@tenant.com", fechaUtc, false);

    private void YaIngerido(ConexionIntegracion conexion, string mensajeExternoId)
    {
        var conversacion = new Conversacion("Asunto");
        conversacion.AsociarConexion(conexion.Id, "hilo-" + mensajeExternoId);
        conversacion.AgregarMensaje(
            DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@tenant.com", "<p>hola</p>", mensajeExternoId: mensajeExternoId);
        _conversaciones.Agregar(conversacion);
    }

    /// <summary>
    /// El caso de la revisión: la conexión cae a las 10:00 menos un poco; A llega a las 10:00 y se pierde (la ingesta
    /// descartaba sus notificaciones), B llega a las 10:10 cuando ya volvió y se ingiere. Anclar en el último mensaje
    /// ingerido (B) perdería A; la lectura se ancla en la marca y lo recupera.
    /// </summary>
    [Fact]
    public async Task Se_ancla_en_la_marca_y_no_en_el_ultimo_ingerido_recupera_A_aunque_B_ya_entrara()
    {
        var caida = DateTime.UtcNow.AddHours(-2);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(2));
        YaIngerido(conexion, "B");
        _graph.MensajesADevolver =
        [
            Mensaje("B", caida.AddMinutes(10)),
            Mensaje("A", caida),
            Mensaje("anterior", caida - RecuperacionCorreoBuzonService.Margen - TimeSpan.FromMinutes(10)),
        ];

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.Completa);
        _eventos.Eventos.Should().ContainSingle().Which.PayloadCrudo.Should().Be("recuperacion:A");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.CorreoNoRecuperadoDesdeUtc.Should().BeNull();
    }

    [Fact]
    public async Task Lo_recuperado_se_encola_del_mas_antiguo_al_mas_reciente_en_un_unico_evento()
    {
        var caida = DateTime.UtcNow.AddHours(-2);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(2));
        _graph.MensajesADevolver = [Mensaje("m-2", caida.AddMinutes(20)), Mensaje("m-1", caida.AddMinutes(10))];

        await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        _eventos.Eventos.Should().ContainSingle().Which.PayloadCrudo.Should().Be("recuperacion:m-1,m-2");
        _unitOfWork.VecesGuardado.Should().Be(2, "uno tras el token y otro con el evento y la marca limpia, juntos");
    }

    [Fact]
    public async Task Nunca_lee_correo_anterior_a_la_conexion()
    {
        var conexion = ConexionConHueco(DateTime.UtcNow.AddMinutes(-30), TimeSpan.FromMinutes(32));
        _graph.MensajesADevolver =
        [
            Mensaje("posterior", DateTime.UtcNow.AddMinutes(-10)),
            Mensaje("anterior-a-conectar", conexion.FechaConectadaUtc.AddMinutes(-1)),
        ];

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.Completa);
        _eventos.Eventos.Should().ContainSingle().Which.PayloadCrudo.Should().Be("recuperacion:posterior");
    }

    /// <summary>Cinco páginas llenas sin llegar a la marca: lo anterior no se lee, y queda el aviso visible.</summary>
    [Fact]
    public async Task El_tope_de_paginas_deja_el_aviso_y_no_limpia_la_marca_en_silencio()
    {
        var caida = DateTime.UtcNow.AddDays(-3);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(5));
        var total = RecuperacionCorreoBuzonService.MaximoPaginas * RecuperacionCorreoBuzonService.TamanoPagina + 10;
        _graph.MensajesADevolver = Enumerable.Range(0, total)
            .Select(i => Mensaje("m-" + i, DateTime.UtcNow.AddMinutes(-i)))
            .ToList();

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.TopeAlcanzado);
        _graph.ListadosPedidos.Should().HaveCount(RecuperacionCorreoBuzonService.MaximoPaginas);
        _graph.ListadosPedidos.Select(l => l.Skip).Should().Equal(0, 50, 100, 150, 200);
        var payload = _eventos.Eventos.Should().ContainSingle().Subject.PayloadCrudo;
        payload["recuperacion:".Length..].Split(',').Should().HaveCount(
            RecuperacionCorreoBuzonService.MaximoPaginas * RecuperacionCorreoBuzonService.TamanoPagina);
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.CorreoNoRecuperadoDesdeUtc.Should().Be(caida);
    }

    /// <summary>Una marca de hace más de siete días: se lee lo de la última semana y queda el aviso desde la marca.</summary>
    [Fact]
    public async Task El_tope_de_siete_dias_no_lee_mas_atras_y_deja_el_aviso()
    {
        var caida = DateTime.UtcNow.AddDays(-9);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(30));
        _graph.MensajesADevolver =
        [
            Mensaje("reciente", DateTime.UtcNow.AddDays(-1)),
            Mensaje("de-hace-ocho-dias", DateTime.UtcNow.AddDays(-8)),
        ];

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.TopeAlcanzado);
        _eventos.Eventos.Should().ContainSingle().Which.PayloadCrudo.Should().Be("recuperacion:reciente");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
        conexion.CorreoNoRecuperadoDesdeUtc.Should().Be(caida);
    }

    /// <summary>
    /// Un listado fallido (429, 5xx, tiempo agotado o respuesta ilegible llegan así) deja la marca intacta, y el
    /// reintento parte de la misma marca, no de la hora del reintento.
    /// </summary>
    [Fact]
    public async Task Un_listado_fallido_deja_la_marca_y_el_reintento_parte_de_ella_sin_adelantarla()
    {
        var caida = DateTime.UtcNow.AddHours(-2);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(2));
        _graph.MensajesADevolver = [Mensaje("perdido", caida.AddMinutes(1))];
        _graph.ErrorListado = Error.Crear("Integraciones.Microsoft365.ErrorGraph", "429 Too Many Requests");

        var primero = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        primero.Should().Be(RecuperacionCorreoBuzonService.Resultado.Reintentar);
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
        conexion.CorreoNoRecuperadoDesdeUtc.Should().BeNull();
        _eventos.Eventos.Should().BeEmpty();

        _graph.ErrorListado = null;
        var segundo = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        segundo.Should().Be(RecuperacionCorreoBuzonService.Resultado.Completa);
        _eventos.Eventos.Should().ContainSingle().Which.PayloadCrudo.Should().Be("recuperacion:perdido");
        conexion.HuecoPendienteDesdeUtc.Should().BeNull();
    }

    [Fact]
    public async Task Sin_token_deja_la_marca_para_el_siguiente_intento()
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "compartido@operador.test", "Buzón compartido", "11111111-2222-3333-4444-555555555555", Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-compartido");
        conexion.MarcarIdentidadVerificada(DateTime.UtcNow.AddHours(-1));
        var caida = DateTime.UtcNow.AddMinutes(-30);
        conexion.MarcarConError("caída", caida);
        conexion.Rehabilitar();
        _conexiones.Agregar(conexion);
        _graph.FallaTokenAplicacion = true;

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.Reintentar);
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
        _graph.ListadosPedidos.Should().BeEmpty();
    }

    /// <summary>Una excepción en una conexión no para a las demás: descarta su lote y la marca sigue.</summary>
    [Fact]
    public async Task Una_excepcion_descarta_el_lote_deja_la_marca_y_no_para_a_las_demas()
    {
        var caida = DateTime.UtcNow.AddHours(-2);
        var primera = ConexionConHueco(caida, TimeSpan.FromDays(2));
        var segunda = ConexionConHueco(caida, TimeSpan.FromDays(2));
        _unitOfWork.ExcepcionAlGuardar = new InvalidOperationException("guardado rechazado");

        var accion = () => Servicio().RecuperarPendientesAsync(CancellationToken.None);

        await accion.Should().NotThrowAsync();
        _descarte.VecesDescartado.Should().Be(2);
        primera.HuecoPendienteDesdeUtc.Should().Be(caida);
        segunda.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    [Fact]
    public async Task Solo_atiende_las_conexiones_operativas_con_marca()
    {
        var caida = DateTime.UtcNow.AddHours(-2);
        var conMarca = ConexionConHueco(caida, TimeSpan.FromDays(2));
        var caida2 = ConexionConHueco(caida, TimeSpan.FromDays(2));
        caida2.MarcarConError("sigue caída");
        var sinMarca = new ConexionIntegracion("otro@operador.test", "Otro").Verificada();
        _conexiones.Agregar(sinMarca);
        _queryContext.ConexionesLista.Add(sinMarca);
        _graph.MensajesADevolver = [];

        await Servicio().RecuperarPendientesAsync(CancellationToken.None);

        conMarca.HuecoPendienteDesdeUtc.Should().BeNull();
        caida2.HuecoPendienteDesdeUtc.Should().Be(caida, "una conexión en ConError no se lee: la marca espera a que vuelva");
        _graph.ListadosPedidos.Should().ContainSingle();
    }

    /// <summary>La instancia cargada dice operativa, pero la base dice que otro proceso la pasó a ConError: no se lee.</summary>
    [Fact]
    public async Task La_puerta_relee_la_base_antes_de_la_primera_pagina()
    {
        var caida = DateTime.UtcNow.AddHours(-2);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(2));
        _conexiones.NoOperativasEnBase.Add(conexion.Id);
        _graph.MensajesADevolver = [Mensaje("perdido", caida.AddMinutes(1))];

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.NoOperativa);
        _graph.ListadosPedidos.Should().BeEmpty();
        _eventos.Eventos.Should().BeEmpty();
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    /// <summary>Deja de estar operativa entre una página y la siguiente: no se pide la segunda ni se limpia la marca.</summary>
    [Fact]
    public async Task La_puerta_relee_la_base_antes_de_cada_pagina()
    {
        var caida = DateTime.UtcNow.AddDays(-2);
        var conexion = ConexionConHueco(caida, TimeSpan.FromDays(5));
        _graph.MensajesADevolver = Enumerable.Range(0, 120)
            .Select(i => Mensaje("m-" + i, DateTime.UtcNow.AddMinutes(-i)))
            .ToList();
        _conexiones.AlReleer = vez =>
        {
            if (vez == 2)
                _conexiones.NoOperativasEnBase.Add(conexion.Id);
        };

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.NoOperativa);
        _graph.ListadosPedidos.Should().ContainSingle();
        _eventos.Eventos.Should().BeEmpty();
        conexion.HuecoPendienteDesdeUtc.Should().Be(caida);
    }

    [Fact]
    public async Task Sin_marca_no_lee_nada()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE").Verificada();
        _conexiones.Agregar(conexion);

        var resultado = await Servicio().RecuperarAsync(conexion.Id, CancellationToken.None);

        resultado.Should().Be(RecuperacionCorreoBuzonService.Resultado.SinHueco);
        _graph.ListadosPedidos.Should().BeEmpty();
    }
}