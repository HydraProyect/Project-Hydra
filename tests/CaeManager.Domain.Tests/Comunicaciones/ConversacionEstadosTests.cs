using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Comunicaciones;

/// <summary>
/// Máquina de estados de la Conversacion (decisión de producto 2026-10-17, hueco 2 de Comunicaciones): transiciones válidas
/// e inválidas, reapertura por mensaje entrante, cierre automático a los 4 días de resolverse y el rastro de cada cambio.
/// La tabla de transiciones válidas está escrita aquí a mano, no derivada de <see cref="TransicionesEstadoConversacion"/>:
/// un test que recalculase la regla con la propia regla no fallaría nunca.
/// </summary>
public class ConversacionEstadosTests
{
    private static readonly DateTime Base = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private static readonly EstadoConversacion[] Vivos =
    [
        EstadoConversacion.Abierta, EstadoConversacion.Pendiente,
        EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero
    ];

    /// <summary>Las 23 transiciones permitidas, una a una.</summary>
    private static readonly HashSet<(EstadoConversacion, EstadoConversacion)> Validas =
    [
        // Desde cada estado vivo: a cualquier otro estado (los otros tres vivos, Resuelta y Cerrada).
        (EstadoConversacion.Abierta, EstadoConversacion.Pendiente),
        (EstadoConversacion.Abierta, EstadoConversacion.EsperandoRespuestaExterna),
        (EstadoConversacion.Abierta, EstadoConversacion.EnEsperaDeTercero),
        (EstadoConversacion.Abierta, EstadoConversacion.Resuelta),
        (EstadoConversacion.Abierta, EstadoConversacion.Cerrada),
        (EstadoConversacion.Pendiente, EstadoConversacion.Abierta),
        (EstadoConversacion.Pendiente, EstadoConversacion.EsperandoRespuestaExterna),
        (EstadoConversacion.Pendiente, EstadoConversacion.EnEsperaDeTercero),
        (EstadoConversacion.Pendiente, EstadoConversacion.Resuelta),
        (EstadoConversacion.Pendiente, EstadoConversacion.Cerrada),
        (EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.Abierta),
        (EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.Pendiente),
        (EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero),
        (EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.Resuelta),
        (EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.Cerrada),
        (EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Abierta),
        (EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Pendiente),
        (EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.EsperandoRespuestaExterna),
        (EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Resuelta),
        (EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Cerrada),
        // Resuelta: solo se reabre o se cierra.
        (EstadoConversacion.Resuelta, EstadoConversacion.Abierta),
        (EstadoConversacion.Resuelta, EstadoConversacion.Cerrada),
        // Cerrada: solo se reabre, a mano.
        (EstadoConversacion.Cerrada, EstadoConversacion.Abierta)
    ];

    public static TheoryData<EstadoConversacion, EstadoConversacion, bool> TodasLasParejas()
    {
        var datos = new TheoryData<EstadoConversacion, EstadoConversacion, bool>();
        foreach (var desde in Enum.GetValues<EstadoConversacion>())
            foreach (var hacia in Enum.GetValues<EstadoConversacion>())
                datos.Add(desde, hacia, Validas.Contains((desde, hacia)));
        return datos;
    }

    /// <summary>Lleva una conversación nueva a <paramref name="estado"/> por la vía legal (desde Abierta todo es alcanzable) y deja limpia la huella del montaje.</summary>
    private static Conversacion EnEstado(EstadoConversacion estado, DateTime desdeUtc)
    {
        var conversacion = new Conversacion("Documentación pendiente", Guid.NewGuid());
        if (estado != EstadoConversacion.Abierta)
            conversacion.CambiarEstado(estado, desdeUtc).EsExitoso.Should().BeTrue("el montaje del test parte de una transición válida");
        return conversacion;
    }

    private static DateTime Marca(Conversacion conversacion) => conversacion.EstadoDesdeUtc;

    [Fact]
    public void La_tabla_de_validas_del_test_cubre_exactamente_23_de_las_36_parejas()
    {
        Validas.Should().HaveCount(23);
        (Enum.GetValues<EstadoConversacion>().Length * Enum.GetValues<EstadoConversacion>().Length).Should().Be(36);
    }

    [Theory]
    [MemberData(nameof(TodasLasParejas))]
    public void CambiarEstado_acepta_las_transiciones_validas_y_rechaza_las_invalidas(
        EstadoConversacion desde, EstadoConversacion hacia, bool valida)
    {
        var conversacion = EnEstado(desde, Base);
        var cambiosAntes = conversacion.CambiosEstado.Count;
        var marcaAntes = Marca(conversacion);

        var resultado = conversacion.CambiarEstado(hacia, Base.AddHours(1));

        if (valida)
        {
            resultado.EsExitoso.Should().BeTrue();
            conversacion.Estado.Should().Be(hacia);
            conversacion.CambiosEstado.Should().HaveCount(cambiosAntes + 1);
            var ultimo = conversacion.CambiosEstado[^1];
            ultimo.EstadoAnterior.Should().Be(desde);
            ultimo.EstadoNuevo.Should().Be(hacia);
            ultimo.FechaUtc.Should().Be(Base.AddHours(1));
            conversacion.EstadoDesdeUtc.Should().Be(Base.AddHours(1));
        }
        else
        {
            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("Conversacion.TransicionDeEstadoNoPermitida");
            conversacion.Estado.Should().Be(desde, "un cambio rechazado no toca nada");
            conversacion.CambiosEstado.Should().HaveCount(cambiosAntes, "ni deja rastro de un cambio que no ocurrió");
            conversacion.EstadoDesdeUtc.Should().Be(marcaAntes);
        }
    }

    [Fact]
    public void Una_conversacion_nueva_nace_Abierta_sin_historial_y_con_su_estado_desde_ahora()
    {
        var antes = DateTime.UtcNow;

        var conversacion = new Conversacion("Consulta", Guid.NewGuid());

        conversacion.Estado.Should().Be(EstadoConversacion.Abierta);
        conversacion.CambiosEstado.Should().BeEmpty();
        conversacion.EstadoDesdeUtc.Should().BeOnOrAfter(antes).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void Cada_cambio_de_estado_guarda_su_instante_y_los_cambios_quedan_en_orden()
    {
        var conversacion = new Conversacion("Consulta", Guid.NewGuid());

        conversacion.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, Base);
        conversacion.CambiarEstado(EstadoConversacion.EnEsperaDeTercero, Base.AddHours(5));
        conversacion.CambiarEstado(EstadoConversacion.Resuelta, Base.AddHours(8));

        conversacion.CambiosEstado.Select(c => (c.EstadoAnterior, c.EstadoNuevo, c.FechaUtc)).Should().Equal(
            (EstadoConversacion.Abierta, EstadoConversacion.EsperandoRespuestaExterna, Base),
            (EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero, Base.AddHours(5)),
            (EstadoConversacion.EnEsperaDeTercero, EstadoConversacion.Resuelta, Base.AddHours(8)));
        conversacion.CambiosEstado.Should().OnlyContain(c => c.ConversacionId == conversacion.Id);
    }

    [Fact]
    public void El_registro_de_cambios_no_guarda_quien_lo_hizo()
    {
        // Regla de producto: no fiscalizar al Gestor CAE. Si alguien añade un campo de autor, este test obliga a decidirlo a conciencia.
        var propiedades = typeof(CambioEstadoConversacion).GetProperties().Select(p => p.Name).ToList();

        propiedades.Should().BeEquivalentTo(
            [
                nameof(CambioEstadoConversacion.Id), nameof(CambioEstadoConversacion.TenantId),
                nameof(CambioEstadoConversacion.ConversacionId), nameof(CambioEstadoConversacion.EstadoAnterior),
                nameof(CambioEstadoConversacion.EstadoNuevo), nameof(CambioEstadoConversacion.FechaUtc)
            ]);
        propiedades.Should().NotContain(n => n.Contains("Usuario") || n.Contains("Autor") || n.Contains("Por"));
    }

    [Fact]
    public void Los_valores_numericos_de_los_estados_antiguos_no_cambian()
    {
        // La columna guarda el entero: reordenar el enum reinterpretaría las filas ya guardadas.
        ((int)EstadoConversacion.Abierta).Should().Be(0);
        ((int)EstadoConversacion.Pendiente).Should().Be(1);
        ((int)EstadoConversacion.Resuelta).Should().Be(2);
        ((int)EstadoConversacion.Cerrada).Should().Be(3);
        ((int)EstadoConversacion.EsperandoRespuestaExterna).Should().Be(4);
        ((int)EstadoConversacion.EnEsperaDeTercero).Should().Be(5);
    }

    [Fact]
    public void Los_estados_vivos_son_los_cuatro_que_se_siguen_atendiendo()
    {
        TransicionesEstadoConversacion.EstadosVivos.Should().BeEquivalentTo(Vivos);
        TransicionesEstadoConversacion.EsVivo(EstadoConversacion.Resuelta).Should().BeFalse();
        TransicionesEstadoConversacion.EsVivo(EstadoConversacion.Cerrada).Should().BeFalse();
    }

    // ---------------------------------------------------------------- reapertura por mensaje entrante

    [Fact]
    public void Un_mensaje_entrante_reabre_la_conversacion_resuelta()
    {
        var conversacion = EnEstado(EstadoConversacion.Resuelta, Base);

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "carmen@cliente.es", "<p>Una duda más.</p>", Base.AddHours(2));

        conversacion.Estado.Should().Be(EstadoConversacion.Abierta);
        conversacion.EstadoDesdeUtc.Should().Be(Base.AddHours(2));
        var reapertura = conversacion.CambiosEstado[^1];
        (reapertura.EstadoAnterior, reapertura.EstadoNuevo, reapertura.FechaUtc).Should().Be(
            (EstadoConversacion.Resuelta, EstadoConversacion.Abierta, Base.AddHours(2)));
    }

    [Fact]
    public void Un_mensaje_entrante_reabre_la_conversacion_que_esperaba_respuesta_externa()
    {
        var conversacion = EnEstado(EstadoConversacion.EsperandoRespuestaExterna, Base);

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "carmen@cliente.es", "<p>Aquí va.</p>", Base.AddHours(3));

        conversacion.Estado.Should().Be(EstadoConversacion.Abierta);
        conversacion.CambiosEstado[^1].EstadoAnterior.Should().Be(EstadoConversacion.EsperandoRespuestaExterna);
    }

    [Theory]
    [InlineData(EstadoConversacion.EnEsperaDeTercero)]
    [InlineData(EstadoConversacion.Pendiente)]
    [InlineData(EstadoConversacion.Abierta)]
    [InlineData(EstadoConversacion.Cerrada)]
    public void Un_mensaje_entrante_no_mueve_el_resto_de_estados(EstadoConversacion estado)
    {
        var conversacion = EnEstado(estado, Base);
        var cambios = conversacion.CambiosEstado.Count;

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "alguien@externo.es", "<p>Hola.</p>", Base.AddHours(2));

        conversacion.Estado.Should().Be(estado);
        conversacion.CambiosEstado.Should().HaveCount(cambios);
    }

    [Fact]
    public void Un_mensaje_saliente_no_reabre_una_conversacion_resuelta()
    {
        var conversacion = EnEstado(EstadoConversacion.Resuelta, Base);

        conversacion.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "gestor@talveg.es", "<p>Quedamos a la espera.</p>", Base.AddHours(2));

        conversacion.Estado.Should().Be(EstadoConversacion.Resuelta);
    }

    [Fact]
    public void Un_entrante_anterior_a_la_resolucion_no_la_deshace()
    {
        // Ingesta tardía: el correo se escribió antes de que el Gestor CAE resolviese el hilo.
        var conversacion = EnEstado(EstadoConversacion.Resuelta, Base);

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "carmen@cliente.es", "<p>Escrito antes.</p>", Base.AddMinutes(-30));

        conversacion.Estado.Should().Be(EstadoConversacion.Resuelta);
        conversacion.EstadoDesdeUtc.Should().Be(Base);
    }

    [Fact]
    public void Un_entrante_con_fecha_de_futuro_reabre_pero_el_cambio_se_registra_con_la_hora_actual()
    {
        var hace = DateTime.UtcNow.AddHours(-1);
        var conversacion = EnEstado(EstadoConversacion.Resuelta, hace);

        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "carmen@cliente.es", "<p>Reloj adelantado.</p>", DateTime.UtcNow.AddDays(1));

        conversacion.Estado.Should().Be(EstadoConversacion.Abierta);
        conversacion.EstadoDesdeUtc.Should().BeOnOrBefore(DateTime.UtcNow, "el historial no puede quedarse en el futuro");
        conversacion.EstadoDesdeUtc.Should().BeAfter(hace);
    }

    [Fact]
    public void Un_entrante_que_llega_por_vinculacion_reabre_el_hilo_que_esperaba_respuesta_externa()
    {
        var destino = EnEstado(EstadoConversacion.EsperandoRespuestaExterna, Base);
        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Hola", Base.AddHours(1));

        destino.AbsorberMensajesDe(origen);

        destino.Estado.Should().Be(EstadoConversacion.Abierta);
        destino.CambiosEstado[^1].FechaUtc.Should().Be(Base.AddHours(1));
    }

    [Fact]
    public void Un_automatico_que_llega_por_vinculacion_no_reabre_el_hilo()
    {
        var destino = EnEstado(EstadoConversacion.EsperandoRespuestaExterna, Base);
        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Fuera de la oficina",
            Base.AddHours(1), esAutomatico: true);

        destino.AbsorberMensajesDe(origen);

        destino.Estado.Should().Be(EstadoConversacion.EsperandoRespuestaExterna);
    }

    [Fact]
    public void Un_entrante_que_llega_por_vinculacion_no_mueve_un_hilo_en_espera_de_tercero()
    {
        var destino = EnEstado(EstadoConversacion.EnEsperaDeTercero, Base);
        var origen = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        origen.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Hola", Base.AddHours(1));

        destino.AbsorberMensajesDe(origen);

        destino.Estado.Should().Be(EstadoConversacion.EnEsperaDeTercero);
    }

    // ---------------------------------------------------------------- cierre automático

    [Fact]
    public void El_plazo_de_cierre_automatico_son_cuatro_dias()
    {
        Conversacion.PlazoCierreAutomaticoTrasResolucion.Should().Be(TimeSpan.FromDays(4));
    }

    [Fact]
    public void Una_conversacion_resuelta_hace_cuatro_dias_se_cierra_y_deja_su_rastro()
    {
        var conversacion = EnEstado(EstadoConversacion.Resuelta, Base);

        var cerrada = conversacion.CerrarSiResueltaHaceMasDelPlazo(Base.AddDays(4));

        cerrada.Should().BeTrue();
        conversacion.Estado.Should().Be(EstadoConversacion.Cerrada);
        var cierre = conversacion.CambiosEstado[^1];
        (cierre.EstadoAnterior, cierre.EstadoNuevo, cierre.FechaUtc).Should().Be(
            (EstadoConversacion.Resuelta, EstadoConversacion.Cerrada, Base.AddDays(4)));
    }

    [Fact]
    public void Una_conversacion_resuelta_hace_menos_de_cuatro_dias_sigue_resuelta()
    {
        var conversacion = EnEstado(EstadoConversacion.Resuelta, Base);

        var cerrada = conversacion.CerrarSiResueltaHaceMasDelPlazo(Base.AddDays(4).AddSeconds(-1));

        cerrada.Should().BeFalse();
        conversacion.Estado.Should().Be(EstadoConversacion.Resuelta);
        conversacion.CambiosEstado.Should().HaveCount(1, "solo el montaje: el pase no registró nada");
    }

    [Theory]
    [InlineData(EstadoConversacion.Abierta)]
    [InlineData(EstadoConversacion.Pendiente)]
    [InlineData(EstadoConversacion.EsperandoRespuestaExterna)]
    [InlineData(EstadoConversacion.EnEsperaDeTercero)]
    [InlineData(EstadoConversacion.Cerrada)]
    public void El_cierre_automatico_solo_cierra_conversaciones_resueltas(EstadoConversacion estado)
    {
        var conversacion = EnEstado(estado, Base);

        var cerrada = conversacion.CerrarSiResueltaHaceMasDelPlazo(Base.AddDays(30));

        cerrada.Should().BeFalse();
        conversacion.Estado.Should().Be(estado);
    }

    [Fact]
    public void Reabrir_y_volver_a_resolver_reinicia_la_cuenta_de_los_cuatro_dias()
    {
        var conversacion = EnEstado(EstadoConversacion.Resuelta, Base);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "carmen@cliente.es", "<p>Otra cosa.</p>", Base.AddDays(3));
        conversacion.CambiarEstado(EstadoConversacion.Resuelta, Base.AddDays(3).AddHours(1));

        conversacion.CerrarSiResueltaHaceMasDelPlazo(Base.AddDays(5)).Should().BeFalse("la cuenta empezó al resolverla la segunda vez");
        conversacion.CerrarSiResueltaHaceMasDelPlazo(Base.AddDays(7).AddHours(1)).Should().BeTrue();
    }
}
