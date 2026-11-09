using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Avisos de correo de Conexiones que salen de la cola de ingesta: eventos retenidos y descartados.</summary>
public class AvisosCorreoConexionesTests
{
    private readonly IntegracionesQueryContextFalso _contexto = new();

    private EventoWebhook Retenido(Guid conexionId)
    {
        var evento = new EventoWebhook(conexionId, "{}");
        evento.EsperarVerificacion("en espera", DateTime.UtcNow + EventoWebhook.TopeEsperaVerificacion + TimeSpan.FromMinutes(1));
        _contexto.EventosLista.Add(evento);
        return evento;
    }

    private EventoWebhook Descartado(Guid conexionId, TimeSpan recibidoHace)
    {
        var evento = new EventoWebhook(conexionId, "{}");
        typeof(EventoWebhook).GetProperty(nameof(EventoWebhook.FechaRecepcionUtc))!.SetValue(evento, DateTime.UtcNow - recibidoHace);
        for (var i = 0; i < EventoWebhook.MaximoIntentos; i++)
        {
            evento.MarcarEnProceso();
            evento.RegistrarFallo("fallo simulado");
        }

        _contexto.EventosLista.Add(evento);
        return evento;
    }

    /// <summary>
    /// Un evento puede quedar Retenido después de que la conexión se verificase y liberase los suyos: el aviso sale
    /// siempre que haya alguno, esté la conexión verificada o no.
    /// </summary>
    [Fact]
    public async Task Una_conexion_verificada_con_un_evento_retenido_lleva_el_aviso()
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE").Verificada();
        Retenido(conexion.Id).Estado.Should().Be(EstadoEventoWebhook.Retenido);

        var avisos = await ObtenerConexionesIntegracionQueryHandler.AvisosDeEventosAsync(
            _contexto, [conexion.Id], CancellationToken.None);

        avisos.Should().BeEquivalentTo([(conexion.Id, EstadoEventoWebhook.Retenido)]);
    }

    /// <summary>
    /// La política de cinco intentos no cambia, pero el correo descartado ya no se pierde en silencio: el aviso dura
    /// mientras exista el evento. Uno recibido hace semanas (retenido mucho tiempo y descartado después) también avisa.
    /// </summary>
    [Fact]
    public async Task Un_evento_descartado_definitivo_lleva_el_aviso_por_antiguo_que_sea()
    {
        var conReciente = Guid.NewGuid();
        var conAntiguo = Guid.NewGuid();
        Descartado(conReciente, TimeSpan.FromDays(1)).Estado.Should().Be(EstadoEventoWebhook.DescartadoDefinitivo);
        Descartado(conAntiguo, TimeSpan.FromDays(30)).Estado.Should().Be(EstadoEventoWebhook.DescartadoDefinitivo);

        var avisos = await ObtenerConexionesIntegracionQueryHandler.AvisosDeEventosAsync(
            _contexto, [conReciente, conAntiguo], CancellationToken.None);

        avisos.Should().BeEquivalentTo([
            (conReciente, EstadoEventoWebhook.DescartadoDefinitivo),
            (conAntiguo, EstadoEventoWebhook.DescartadoDefinitivo)]);
    }
    [Fact]
    public async Task Los_eventos_de_otras_conexiones_o_en_otros_estados_no_dan_aviso()
    {
        var conexion = Guid.NewGuid();
        _contexto.EventosLista.Add(new EventoWebhook(conexion, "{}"));
        Retenido(Guid.NewGuid());

        var avisos = await ObtenerConexionesIntegracionQueryHandler.AvisosDeEventosAsync(
            _contexto, [conexion], CancellationToken.None);

        avisos.Should().BeEmpty();
    }
}