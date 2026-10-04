using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Bandeja;

/// <summary>
/// P2.7 (recalibración de la demo del piloto Outbound, 2026-09-23): una
/// acreditación Rechazada que bloquea al Trabajador (o a los de su Empresa) en su Centro de
/// Trabajo (D-7) tiene que marcar su grupo de la cola como «bloquea acceso», igual que un
/// RequisitoPendiente. Antes solo lo marcaba un RequisitoPendiente, y un
/// Trabajador bloqueado únicamente por un rechazo salía en Mi trabajo e Inicio sin
/// banda ni recuento de bloqueo, mientras Centro 360 decía que no podía entrar.
///
/// <para>
/// Qué rechazada bloquea no se decide en la cola: se lee del mismo evaluador
/// que pinta el detalle por Trabajador del Centro 360
/// (<see cref="IEvaluacionDeAccesoPorCentroService"/>). Desde 2026-10-04 ya no se lee del estado
/// del Centro: el Centro no se marca «Bloqueado» por un rechazo. Estos tests fijan la lectura de las
/// filas evaluadas; que el evaluador las emita solo para rechazadas aplicables lo prueban
/// <c>LaPlataformaBloqueaAlTrabajadorYNoAlCentroTests</c> (integración) y, de extremo a
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

    private static ItemBandejaDto DeTipo(TipoItemBandeja tipo, Guid clienteId, string clienteNombre) => new(
        Id: $"{tipo}-{Guid.NewGuid()}", Tipo: tipo, Titulo: "t", Subtitulo: "s", Fecha: null,
        TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(), DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(),
        RequisitoId: null, ClienteId: clienteId, ClienteNombre: clienteNombre);

    private static RequisitoEvaluado Evaluado(
        Guid centroId, Guid? documentoId, SituacionDeRequisitoBloqueante situacion = SituacionDeRequisitoBloqueante.RechazadoPorPlataforma) => new(
        centroId, TrabajadorId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), AmbitoAplicacion.Trabajador, EmpresaId: null,
        new ResultadoDeRequisito(situacion, VencimientoEfectivo: null, EnToleranciaHasta: null), ToleranciaDias: 0, documentoId);

    [Fact]
    public void Una_rechazada_que_bloquea_a_un_Trabajador_en_su_Centro_marca_su_grupo_como_bloquea_acceso()
    {
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, documento)],
            [Evaluado(CentroCerveceria, documento)]);

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeTrue();
        ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(items[0]).Should().BeTrue();

        var grupo = ObtenerBandejaAgrupadaQueryHandler.Agrupar(items).Grupos.Should().ContainSingle().Subject;
        grupo.BloqueaAcceso.Should().BeTrue(
            "un Trabajador está bloqueado solo por este rechazo (D-7): su grupo no puede salir como si no bloqueara");
    }

    [Fact]
    public void Una_rechazada_que_el_evaluador_no_cuenta_como_bloqueo_no_marca_el_grupo()
    {
        // Rechazada de otro Centro, de un Trabajador desvinculado, de una Empresa sin Trabajadores en el Centro o de un
        // tipo que no aplica: el evaluador no emite fila por ella. Sigue siendo trabajo en la cola, pero no bloquea a nadie.
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, Guid.NewGuid())],
            []);

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse();
        ObtenerBandejaAgrupadaQueryHandler.Agrupar(items).Grupos.Should().ContainSingle()
            .Which.BloqueaAcceso.Should().BeFalse();
    }

    [Fact]
    public void Una_rechazada_no_se_apropia_del_bloqueo_que_causa_otro_documento_del_mismo_Centro()
    {
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, Guid.NewGuid())],
            [Evaluado(CentroCerveceria, Guid.NewGuid())]);

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse(
            "hay un Trabajador bloqueado en el Centro, pero no por este documento: quien lo cierra es otro item");
    }

    [Fact]
    public void Una_fila_de_otra_situacion_sobre_el_mismo_documento_no_marca_la_rechazada()
    {
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, documento)],
            [Evaluado(CentroCerveceria, documento, SituacionDeRequisitoBloqueante.VencidoEnPlataforma)]);

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse(
            "la fila existe pero dice «vencido en la plataforma», no «rechazado»: no es el rechazo de este item");
    }

    [Fact]
    public void Un_bloqueo_del_mismo_documento_en_otro_Centro_no_marca_la_rechazada()
    {
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [Rechazada(CentroCerveceria, documento)],
            [Evaluado(Guid.NewGuid(), documento)]);

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse();
    }

    [Fact]
    public void Solo_se_marcan_las_rechazadas_no_una_pendiente_de_subir_del_mismo_documento()
    {
        var documento = Guid.NewGuid();
        var pendiente = Rechazada(CentroCerveceria, documento) with { Tipo = TipoItemBandeja.PlataformaPendiente };

        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [pendiente], [Evaluado(CentroCerveceria, documento)]);

        items.Should().ContainSingle().Which.RechazoBloqueaCentro.Should().BeFalse(
            "pendiente de subir es trabajo, no un «no» de la plataforma (D-7)");
    }

    [Fact]
    public void Un_item_que_no_es_rechazada_no_bloquea_aunque_lleve_el_campo()
    {
        // Defensa del predicado: el campo solo significa algo en PlataformaRechazada.
        var vencido = DeTipo(TipoItemBandeja.Vencido, ClienteDuff, "Cervezas Duff Ibérica") with { RechazoBloqueaCentro = true };

        ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(vencido).Should().BeFalse();
    }

    [Fact]
    public void Un_requisito_pendiente_siempre_bloquea_tambien_el_de_un_Trabajador_recien_dado_de_alta()
    {
        ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(
            DeTipo(TipoItemBandeja.RequisitoPendiente, ClienteDuff, "Cervezas Duff Ibérica"))
            .Should().BeTrue("ya no hay excepción de «alta nueva»: sin documentación, el Trabajador está bloqueado");
    }

    [Fact]
    public void El_grupo_que_bloquea_por_un_rechazo_va_antes_que_uno_con_un_vencido_que_no_bloquea()
    {
        // Sin el marcado, el Vencido (prioridad 2) adelantaría a la Rechazada
        // (prioridad 3): el grupo que de verdad bloquea a un Trabajador quedaría detrás.
        var documento = Guid.NewGuid();
        var items = ObtenerBandejaAgrupadaQueryHandler.MarcarRechazosQueBloquean(
            [
                DeTipo(TipoItemBandeja.Vencido, ClienteKrusty, "Hamburguesas Krusty"),
                Rechazada(CentroCerveceria, documento)
            ],
            [Evaluado(CentroCerveceria, documento)]);

        var grupos = ObtenerBandejaAgrupadaQueryHandler.Agrupar(items).Grupos;

        grupos.Select(g => g.Titulo).Should().Equal("Cervezas Duff Ibérica", "Hamburguesas Krusty");
        grupos[0].BloqueaAcceso.Should().BeTrue();
        grupos[1].BloqueaAcceso.Should().BeFalse();
    }
}
