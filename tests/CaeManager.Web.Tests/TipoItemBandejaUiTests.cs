using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un RequisitoPendiente con EsAltaNueva (Trabajador sin ningún documento
/// vigente de los tipos bloqueantes en ese Centro — nunca llegó a completar
/// el alta) debe leerse distinto de un requisito que sí bloquea acceso por
/// una regresión (algo caducó): ni el badge ni la acción primaria deben
/// alarmar como si el centro se hubiera roto.
/// </summary>
public class TipoItemBandejaUiTests
{
    private static ItemBandejaDto Requisito(bool esAltaNueva) => new(
        Id: "requisito-1", Tipo: TipoItemBandeja.RequisitoPendiente, Titulo: "PSS firmado — Ana García",
        Subtitulo: "Centro Sur", Fecha: null, TrabajadorId: Guid.NewGuid(), CentroId: Guid.NewGuid(),
        DocumentoId: null, TipoDocumentoId: Guid.NewGuid(), RequisitoId: null, EsAltaNueva: esAltaNueva);

    [Fact]
    public void Alta_nueva_usa_tono_de_advertencia_no_de_peligro()
    {
        TipoItemBandejaUi.Tono(Requisito(esAltaNueva: true)).Should().Be(TonoBadge.Advertencia);
    }

    [Fact]
    public void Visita_tradicional_sigue_usando_tono_de_peligro()
    {
        TipoItemBandejaUi.Tono(Requisito(esAltaNueva: false)).Should().Be(TonoBadge.Peligro);
    }

    [Fact]
    public void Alta_nueva_dice_alta_pendiente_en_vez_de_bloquea_el_centro()
    {
        TipoItemBandejaUi.Texto(Requisito(esAltaNueva: true)).Should().Be("Alta pendiente");
        TipoItemBandejaUi.Texto(Requisito(esAltaNueva: false)).Should().Be("Bloquea el centro");
    }

    [Fact]
    public void Alta_nueva_ofrece_adjuntar_en_vez_de_ver_requisito()
    {
        TipoItemBandejaUi.TextoAccion(Requisito(esAltaNueva: true)).Should().Be("Adjuntar");
        TipoItemBandejaUi.TextoAccion(Requisito(esAltaNueva: false)).Should().Be("Ver requisito");
    }

    private static ItemBandejaDto Item(TipoItemBandeja tipo, Guid? trabajadorId, Guid? tipoDocumentoId) => new(
        Id: "item-1", Tipo: tipo, Titulo: "t", Subtitulo: "s", Fecha: null,
        TrabajadorId: trabajadorId, CentroId: null, DocumentoId: null, TipoDocumentoId: tipoDocumentoId, RequisitoId: null);

    /// <summary>
    /// U-2 (plan nocturno 2026-09-02): EsReclamable es el gate que decide si
    /// Bandeja ofrece el botón "Reclamar" en un ítem. Solo Faltante/Vencido/
    /// Urgente son documentos de Trabajador reclamables por email hoy — el
    /// resto de tipos son otra clase de trabajo (revisión IA, requisito de
    /// centro, visita, detección de personal, plataforma del cliente).
    /// </summary>
    [Theory]
    [InlineData(TipoItemBandeja.Faltante, true)]
    [InlineData(TipoItemBandeja.Vencido, true)]
    [InlineData(TipoItemBandeja.Urgente, true)]
    [InlineData(TipoItemBandeja.RequisitoPendiente, false)]
    [InlineData(TipoItemBandeja.RevisionIa, false)]
    [InlineData(TipoItemBandeja.VisitaUrgente, false)]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, false)]
    [InlineData(TipoItemBandeja.DeteccionPendiente, false)]
    [InlineData(TipoItemBandeja.PlataformaPendiente, false)]
    public void EsReclamable_solo_es_true_para_Faltante_Vencido_o_Urgente_con_trabajador_y_tipo_de_documento(
        TipoItemBandeja tipo, bool esperado)
    {
        TipoItemBandejaUi.EsReclamable(Item(tipo, Guid.NewGuid(), Guid.NewGuid())).Should().Be(esperado);
    }

    [Fact]
    public void EsReclamable_es_false_sin_TrabajadorId_aunque_el_tipo_sea_reclamable()
    {
        // VisitaUrgente/SugerenciaVisitaUrgente son justo los tipos Faltante-
        // adyacentes sin Trabajador — sin este guard, EsReclamable ofrecería
        // "Reclamar" sobre un ítem que EnviarReclamacionCommand no sabría a
        // quién pedir.
        TipoItemBandejaUi.EsReclamable(Item(TipoItemBandeja.Faltante, trabajadorId: null, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public void EsReclamable_es_false_sin_TipoDocumentoId()
    {
        TipoItemBandejaUi.EsReclamable(Item(TipoItemBandeja.Vencido, Guid.NewGuid(), tipoDocumentoId: null)).Should().BeFalse();
    }
}
