using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Features.Bandeja;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Mi trabajo Gen2 (multi-Tenant) reutiliza <c>ResolverUrl</c> para componer
/// el <c>returnUrl</c> exacto de la navegación cross-Tenant — este test fija
/// que sigue devolviendo lo mismo que <c>AbrirAsync</c> ya navegaba, para que
/// el refactor que lo extrajo (originalmente todo vivía dentro del switch de
/// <c>AbrirAsync</c>) no cambie el destino de ningún tipo existente.
/// </summary>
public class AccionesBandejaTests
{
    private static ItemBandejaDto Item(TipoItemBandeja tipo, Guid? documentoId = null, Guid? trabajadorId = null,
        Guid? tipoDocumentoId = null, Guid? empresaId = null, Guid? sugerenciaVisitaId = null) => new(
        Id: "x", Tipo: tipo, Titulo: "t", Subtitulo: "s", Fecha: null,
        TrabajadorId: trabajadorId, CentroId: null, DocumentoId: documentoId, TipoDocumentoId: tipoDocumentoId,
        RequisitoId: null, SugerenciaVisitaId: sugerenciaVisitaId, EmpresaId: empresaId);

    [Fact]
    public void RequisitoPendiente_no_tiene_url_propia()
    {
        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.RequisitoPendiente)).Should().BeNull();
    }

    [Fact]
    public void RevisionIa_va_a_la_pestana_de_revision_ia()
    {
        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.RevisionIa)).Should().Be("/documentos/revision-ia");
    }

    [Fact]
    public void SugerenciaVisitaUrgente_lleva_el_id_de_la_sugerencia()
    {
        var id = Guid.NewGuid();

        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.SugerenciaVisitaUrgente, sugerenciaVisitaId: id))
            .Should().Be($"/visitas?sugerenciaId={id}");
    }

    [Fact]
    public void VisitaUrgente_va_a_la_lista_de_visitas()
    {
        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.VisitaUrgente)).Should().Be("/visitas");
    }

    [Fact]
    public void DeteccionPendiente_lleva_el_id_de_empresa()
    {
        var empresaId = Guid.NewGuid();

        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.DeteccionPendiente, empresaId: empresaId))
            .Should().Be($"/empresas/{empresaId}/deteccion-trabajadores");
    }

    [Theory]
    [InlineData(TipoItemBandeja.PlataformaPendiente)]
    [InlineData(TipoItemBandeja.PlataformaRechazada)]
    [InlineData(TipoItemBandeja.EnPlataformaSeguimiento)]
    public void Plataforma_pendiente_rechazada_o_en_seguimiento_va_a_la_pestana_de_plataforma(TipoItemBandeja tipo)
    {
        AccionesBandeja.ResolverUrl(Item(tipo)).Should().Be("/documentos?pestana=plataforma");
    }

    /// <summary>
    /// P12: la vencida en plataforma lleva DocumentoId, pero lo que hay que
    /// renovar es la acreditación en la plataforma, no el documento de TALVEG
    /// (que sigue vigente): no puede caer en el destino genérico por documento.
    /// </summary>
    [Fact]
    public void Plataforma_vencida_va_a_la_pestana_de_plataforma_aunque_lleve_documento()
    {
        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.PlataformaVencida, documentoId: Guid.NewGuid()))
            .Should().Be("/documentos?pestana=plataforma");
    }

    [Fact]
    public void Con_documento_va_al_documento_concreto()
    {
        var documentoId = Guid.NewGuid();

        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.Vencido, documentoId: documentoId))
            .Should().Be($"/documentos?documentoId={documentoId}");
    }

    [Fact]
    public void Sin_documento_va_filtrado_por_trabajador_y_tipo()
    {
        var trabajadorId = Guid.NewGuid();
        var tipoDocumentoId = Guid.NewGuid();

        AccionesBandeja.ResolverUrl(Item(TipoItemBandeja.Faltante, trabajadorId: trabajadorId, tipoDocumentoId: tipoDocumentoId))
            .Should().Be($"/documentos?trabajadorId={trabajadorId}&tipoDocumentoId={tipoDocumentoId}");
    }
}
