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

    [Fact]
    public void Una_acreditacion_rechazada_por_plataforma_es_peligro_y_ofrece_corregir_en_la_plataforma()
    {
        var item = new ItemBandejaDto(
            Id: "plataforma-1", Tipo: TipoItemBandeja.PlataformaRechazada, Titulo: "Formación 60h",
            Subtitulo: "Iker — Ilegible", Fecha: null, TrabajadorId: Guid.NewGuid(), CentroId: null,
            DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), RequisitoId: null, ProveedorNombre: "Dokify");

        TipoItemBandejaUi.Tono(item).Should().Be(TonoBadge.Peligro);
        TipoItemBandejaUi.Texto(item).Should().Be("Rechazada por plataforma");
        TipoItemBandejaUi.TextoAccion(item).Should().Be("Corregir en Dokify");
    }

    /// <summary>
    /// Mi trabajo Gen2 (multi-Tenant): estos dos tipos solo los emite
    /// ObtenerMiTrabajoAgregadoQueryHandler, nunca Nivel 0 — sin sus propios
    /// casos aquí, caerían en el "_ =>" genérico de Tono/Texto/TextoAccion
    /// sin que ningún test lo notara.
    /// </summary>
    [Fact]
    public void Proximo_y_seguimiento_tienen_tono_neutro_y_texto_propio()
    {
        var proximo = new ItemBandejaDto(
            Id: "proximo-1", Tipo: TipoItemBandeja.VencimientoProximo, Titulo: "Apto médico",
            Subtitulo: "Ana García", Fecha: new DateOnly(2026, 10, 1), TrabajadorId: Guid.NewGuid(), CentroId: null,
            DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), RequisitoId: null);
        var seguimiento = new ItemBandejaDto(
            Id: "seguimiento-1", Tipo: TipoItemBandeja.EnPlataformaSeguimiento, Titulo: "Formación 60h",
            Subtitulo: "Iker Etxeberria", Fecha: null, TrabajadorId: Guid.NewGuid(), CentroId: null,
            DocumentoId: Guid.NewGuid(), TipoDocumentoId: Guid.NewGuid(), RequisitoId: null, ProveedorNombre: "Dokify");

        TipoItemBandejaUi.Tono(proximo).Should().Be(TonoBadge.Neutro);
        TipoItemBandejaUi.Texto(proximo).Should().Be("Próximo");
        TipoItemBandejaUi.TextoAccion(proximo).Should().Be("Ver documento");

        TipoItemBandejaUi.Tono(seguimiento).Should().Be(TonoBadge.Neutro);
        TipoItemBandejaUi.Texto(seguimiento).Should().Be("En plataforma");
        TipoItemBandejaUi.TextoAccion(seguimiento).Should().Be("Ver en Dokify");
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
    [InlineData(TipoItemBandeja.PlataformaRechazada, false)]
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

    /// <summary>
    /// P9 (2026-09-18, CAPA-USUARIO-AVANZADO-TALVEG.md § 6.1 quinquies):
    /// «solo la vigencia es copiable», con excepción acotada de Detección/
    /// Revisión IA. Medido contra ObtenerBandejaGestorQueryHandler qué
    /// representa Fecha en cada tipo (ver el comentario largo de
    /// TipoItemBandejaUi.EsFechaCopiable): solo VisitaUrgente y
    /// SugerenciaVisitaUrgente llevan una fecha que no es ni vigencia ni la
    /// excepción (FechaInicio/FechaInicioSugerida). Faltante/Vencido/Urgente
    /// SÍ deben seguir siendo copiables — es vigencia real (FechaVencimiento)
    /// — y restringir a solo RevisionIa/DeteccionPendiente, como decía la
    /// redacción literal del hallazgo, les habría quitado la copia sin motivo.
    /// </summary>
    [Theory]
    [InlineData(TipoItemBandeja.Faltante, true)]
    [InlineData(TipoItemBandeja.Vencido, true)]
    [InlineData(TipoItemBandeja.Urgente, true)]
    [InlineData(TipoItemBandeja.RequisitoPendiente, true)]
    [InlineData(TipoItemBandeja.RevisionIa, true)]
    [InlineData(TipoItemBandeja.VisitaUrgente, false)]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, false)]
    [InlineData(TipoItemBandeja.DeteccionPendiente, true)]
    [InlineData(TipoItemBandeja.PlataformaPendiente, true)]
    [InlineData(TipoItemBandeja.PlataformaRechazada, true)]
    public void EsFechaCopiable_es_false_solo_para_VisitaUrgente_y_SugerenciaVisitaUrgente(
        TipoItemBandeja tipo, bool esperado)
    {
        TipoItemBandejaUi.EsFechaCopiable(Item(tipo, Guid.NewGuid(), Guid.NewGuid())).Should().Be(esperado);
    }
}
