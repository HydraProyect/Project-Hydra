using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// P2.7 (recalibración de la demo del piloto Outbound, 2026-09-23): una
/// acreditación Rechazada que bloquea su Centro de Trabajo (D-7) tiene que
/// marcar su grupo de la cola como «bloquea acceso», igual que un
/// RequisitoPendiente. Antes solo lo marcaba un RequisitoPendiente, y un
/// Centro bloqueado únicamente por un rechazo salía en Mi trabajo e Inicio sin
/// banda ni recuento de bloqueo, mientras Centro 360 decía «Acceso bloqueado».
///
/// <para>
/// Qué rechazada bloquea no se decide en la cola: se lee del mismo cálculo
/// que pinta el estado del Centro (<see cref="ICalculoEstadoCentroService"/>).
/// Estos tests fijan la lectura de ese resultado; que el cálculo emita la
/// causa solo para rechazadas aplicables lo prueban
/// <c>CalculoEstadoCentroServiceTests</c> (integración) y, de extremo a
/// extremo, <c>BandejaRechazadaBloqueaAccesoTests</c>.
/// </para>
/// </summary>
public class RechazadaBloqueaAccesoEnBandejaTests
{
    private static readonly Guid ClienteDuff = Guid.NewGuid();
    private static readonly Guid ClienteKrusty = Guid.NewGuid();
    private static readonly Guid CentroCerveceria = Guid.NewGuid();

    private static ItemBandejaDto Rechazada(Guid centroId, Guid documentoId, Guid? clienteId = null, string clienteNombre = "Cervezas Duff Ibérica") => new(
        Id: $"plataforma-{Guid.NewGuid()}", Tipo: TipoItemBandeja.PlataformaRechazada, Titulo: "Formación 60h",
        Subtitulo: "Homer Simpson — Documento ilegible", Fecha: null, TrabajadorId: Guid.NewGuid(), CentroId: centroId,
        DocumentoId: documentoId, TipoDocumentoId: Guid.NewGuid(), RequisitoId: null,
        ClienteId: clienteId ?? ClienteDuff, ClienteNombre: clienteNombre, ProveedorNombre: "Dokify");

    private static ItemBandejaDto DeTipo(TipoItemBandeja tipo, Guid clienteId, string clienteNombre, bool esAltaNueva = false) => new(
        Id: $"{tipo}-{Guid.NewGuid()}", Tipo: tipo, Titulo: "t", Subtitulo: "s", Fecha: null,
        TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(),
        RequisitoId: null, ClienteId: clienteId, ClienteNombre: clienteNombre, EsAltaNueva: esAltaNueva);

    private static CausaEstadoCentro Causa(Guid? documentoId, bool bloqueante, EstadoDocumento? estado = null) => new(
        "Formación 60h — Homer Simpson — rechazado por la plataforma", estado, bloqueante, AmbitoCausa.Trabajador,
        documentoId, Guid.NewGuid(), FechaVencimiento: null);

    private static Dictionary<Guid, ResultadoEstadoCentro> Estado(Guid centroId, params CausaEstadoCentro[] causas) => new()
    {
        [centroId] = new ResultadoEstadoCentro(
            causas.Any(c => c.Bloqueante) ? EstadoCentro.Bloqueado : EstadoCentro.Vigente, causas)
    };

    [Fact]
    public void Una_rechazada_que_bloquea_su_Centro_marca_su_grupo_como_bloquea_acceso()
    {
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, documento)],
            Estado(CentroCerveceria, Causa(documento, bloqueante: true)));

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeTrue();
        ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(items[0]).Should().BeTrue();

        var grupo = ObtenerBandejaAgrupadaQueryHandler.Agrupar(items).Grupos.Should().ContainSingle().Subject;
        grupo.BloqueaAcceso.Should().BeTrue(
            "el Centro está Bloqueado solo por este rechazo (D-7): su grupo no puede salir como si no bloqueara");
    }

    [Fact]
    public void Una_rechazada_que_el_calculo_del_Centro_no_cuenta_como_bloqueante_no_marca_el_grupo()
    {
        // Rechazada de otro Centro, de un Trabajador desvinculado o de un tipo
        // que no aplica: el cálculo del Centro no emite causa por ella. Sigue
        // siendo trabajo en la cola, pero no cierra ningún Centro.
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, Guid.NewGuid())],
            Estado(CentroCerveceria));

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse();
        ObtenerBandejaAgrupadaQueryHandler.Agrupar(items).Grupos.Should().ContainSingle()
            .Which.BloqueaAcceso.Should().BeFalse();
    }

    [Fact]
    public void Una_rechazada_no_se_apropia_del_bloqueo_que_causa_otro_documento_del_mismo_Centro()
    {
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, Guid.NewGuid())],
            Estado(CentroCerveceria, Causa(Guid.NewGuid(), bloqueante: true)));

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse(
            "el Centro está bloqueado, pero no por este documento: quien lo cierra es otro item");
    }

    [Fact]
    public void Una_causa_no_bloqueante_sobre_el_mismo_documento_no_marca_la_rechazada()
    {
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, documento)],
            Estado(CentroCerveceria, Causa(documento, bloqueante: false, EstadoDocumento.Urgente)));

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse();
    }

    [Fact]
    public void Sin_estado_calculado_para_su_Centro_la_rechazada_no_se_marca()
    {
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, documento)],
            Estado(Guid.NewGuid(), Causa(documento, bloqueante: true)));

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse();
    }

    [Fact]
    public void Solo_se_marcan_las_rechazadas_no_una_pendiente_de_subir_del_mismo_documento()
    {
        var documento = Guid.NewGuid();
        var pendiente = Rechazada(CentroCerveceria, documento) with { Tipo = TipoItemBandeja.PlataformaPendiente };

        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [pendiente], Estado(CentroCerveceria, Causa(documento, bloqueante: true)));

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse(
            "pendiente de subir es trabajo, no un «no» de la plataforma (D-7)");
    }

    [Fact]
    public void Un_item_que_no_es_rechazada_no_bloquea_aunque_lleve_el_campo()
    {
        // Defensa del predicado: el campo solo significa algo en PlataformaRechazada.
        var vencido = DeTipo(TipoItemBandeja.Vencido, ClienteDuff, "Cervezas Duff Ibérica") with { RechazoBloqueaCentro = true };

        ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(vencido).Should().BeFalse();
    }

    [Fact]
    public void Un_requisito_de_alta_nueva_no_bloquea_y_uno_que_no_lo_es_si()
    {
        ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(
            DeTipo(TipoItemBandeja.RequisitoPendiente, ClienteDuff, "Cervezas Duff Ibérica", esAltaNueva: true))
            .Should().BeFalse("un alta sin completar no cierra ningún Centro");
        ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(
            DeTipo(TipoItemBandeja.RequisitoPendiente, ClienteDuff, "Cervezas Duff Ibérica"))
            .Should().BeTrue("control positivo: el requisito que no es alta nueva sí bloquea");
    }

    [Fact]
    public void El_grupo_que_bloquea_por_un_rechazo_va_antes_que_uno_con_un_vencido_que_no_bloquea()
    {
        // Sin el marcado, el Vencido (prioridad 2) adelantaría a la Rechazada
        // (prioridad 3): el grupo que de verdad cierra un Centro quedaría detrás.
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [
                DeTipo(TipoItemBandeja.Vencido, ClienteKrusty, "Hamburguesas Krusty"),
                Rechazada(CentroCerveceria, documento)
            ],
            Estado(CentroCerveceria, Causa(documento, bloqueante: true)));

        var grupos = ObtenerBandejaAgrupadaQueryHandler.Agrupar(items).Grupos;

        grupos.Select(g => g.Titulo).Should().Equal("Cervezas Duff Ibérica", "Hamburguesas Krusty");
        grupos[0].BloqueaAcceso.Should().BeTrue();
        grupos[1].BloqueaAcceso.Should().BeFalse();
    }
}
