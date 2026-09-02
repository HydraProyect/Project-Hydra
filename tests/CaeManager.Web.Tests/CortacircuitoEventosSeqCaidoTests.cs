using CaeManager.Web.Services;
using FluentAssertions;
using Sentry;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-2: Sentry.AspNetCore captura automáticamente toda petición HTTP fallida
/// 5xx (FailedRequestTargets = [".*"] por defecto), incluida la que hace el
/// exportador OTLP contra Seq cuando Seq está caído — 6.362 eventos en 6 días
/// fue DOTNET-7. Estas pruebas cubren las dos condiciones que no pueden
/// romperse al arreglarlo: que la ceguera quede acotada a Seq (control
/// positivo: otros destinos siguen llegando) y que no se convierta en un
/// silencio total (throttle, no descarte permanente).
/// </summary>
public class CortacircuitoEventosSeqCaidoTests
{
    private const string UrlSeq = "https://seq.talveg.es";
    private static readonly DateTime T0 = new(2026, 9, 2, 3, 0, 0, DateTimeKind.Utc);

    private static SentryEvent EventoConUrl(string url) => new() { Request = new SentryRequest { Url = url } };

    [Fact]
    public void Un_fallo_contra_otro_destino_nunca_se_descarta_aunque_Seq_este_cayendo_a_la_vez()
    {
        // Control positivo exigido por la coordinadora: la ceguera tiene que
        // quedar acotada a Seq. Un 5xx real de Anthropic/Graph/Stripe/etc.
        // sigue generando su evento normal, esté Seq caído o no — incluso
        // justo después de que un fallo de Seq ya haya cerrado su ventana.
        var cortacircuito = new CortacircuitoEventosSeqCaido(UrlSeq);
        cortacircuito.DebeDescartarEn(EventoConUrl($"{UrlSeq}/ingest/otlp/v1/traces"), T0).Should().BeFalse();

        var eventoOtroDestino = EventoConUrl("https://api.anthropic.com/v1/messages");
        cortacircuito.DebeDescartarEn(eventoOtroDestino, T0.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void Un_host_que_solo_contiene_la_URL_de_Seq_como_texto_no_se_confunde_con_Seq()
    {
        // Revisión adversaria de Codex: un string.Contains habría dado un
        // falso positivo aquí — "seq.talveg.es.ejemplo.com" contiene
        // "seq.talveg.es" como substring, pero es un HOST distinto. La
        // comparación real es estructurada (esquema+host+puerto), no textual.
        //
        // Ojo con el falso verde: llamar UNA sola vez con la URL trampa no
        // distingue las dos implementaciones (la primera llamada siempre deja
        // pasar, sea o no Seq — "primer aviso"). Por eso primero se abre la
        // ventana con un evento de Seq real, y LUEGO se prueba la URL trampa
        // dentro de esa ventana: si el host se reconociera mal como Seq,
        // caería en la ventana y se descartaría (True); al ser un host
        // distinto de verdad, tiene que pasar (False) pese a la ventana abierta.
        var cortacircuito = new CortacircuitoEventosSeqCaido(UrlSeq);
        cortacircuito.DebeDescartarEn(EventoConUrl($"{UrlSeq}/ingest/otlp/v1/traces"), T0).Should().BeFalse("abre la ventana con un evento real de Seq");

        cortacircuito.DebeDescartarEn(EventoConUrl("https://seq.talveg.es.ejemplo.com/x"), T0.AddSeconds(1))
            .Should().BeFalse("host distinto de verdad — no debe caer en la ventana de Seq aunque el texto se parezca");
    }

    [Fact]
    public void Un_puerto_explicito_por_defecto_sigue_reconociendose_como_el_mismo_destino()
    {
        // La normalización de URI (no un Contains) también evita el falso
        // negativo simétrico: el mismo servidor con el puerto por defecto
        // escrito explícitamente sigue siendo Seq.
        var cortacircuito = new CortacircuitoEventosSeqCaido(UrlSeq);

        cortacircuito.DebeDescartarEn(EventoConUrl($"{UrlSeq}:443/ingest/otlp/v1/metrics"), T0).Should().BeFalse("primer aviso, no descarta");
        cortacircuito.DebeDescartarEn(EventoConUrl($"{UrlSeq}:443/ingest/otlp/v1/traces"), T0.AddSeconds(1))
            .Should().BeTrue("mismo destino que el primer evento (puerto 443 explícito = por defecto de https), dentro de la ventana");
    }

    [Fact]
    public void Sin_Request_o_sin_Url_nunca_se_descarta()
    {
        var cortacircuito = new CortacircuitoEventosSeqCaido(UrlSeq);

        cortacircuito.DebeDescartarEn(new SentryEvent(), T0).Should().BeFalse();
        cortacircuito.DebeDescartarEn(EventoConUrl(""), T0).Should().BeFalse();
    }

    [Fact]
    public void El_primer_fallo_contra_Seq_pasa_como_aviso_inmediato()
    {
        // No es un descarte total: el primero de una racha se deja pasar
        // para que Sentry avise en cuanto Seq empieza a fallar.
        var cortacircuito = new CortacircuitoEventosSeqCaido(UrlSeq);
        var evento = EventoConUrl($"{UrlSeq}/ingest/otlp/v1/metrics");

        cortacircuito.DebeDescartarEn(evento, T0).Should().BeFalse();
    }

    [Fact]
    public void Tras_el_primer_aviso_los_siguientes_dentro_de_la_ventana_se_descartan_y_fuera_de_ella_pasa_otro()
    {
        var cortacircuito = new CortacircuitoEventosSeqCaido(UrlSeq);
        var evento = EventoConUrl($"{UrlSeq}/ingest/otlp/v1/traces");

        cortacircuito.DebeDescartarEn(evento, T0).Should().BeFalse("primer aviso");
        cortacircuito.DebeDescartarEn(evento, T0.AddMinutes(1)).Should().BeTrue("dentro de la ventana de 30 min");
        cortacircuito.DebeDescartarEn(evento, T0.AddMinutes(29)).Should().BeTrue("todavía dentro de la ventana");
        cortacircuito.DebeDescartarEn(evento, T0.AddMinutes(31))
            .Should().BeFalse("fuera de la ventana: Seq sigue caído, así que se deja pasar otro aviso periódico");
    }
}
