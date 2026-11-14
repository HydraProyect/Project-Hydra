using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

namespace CaeManager.Web.Tests;

/// <summary>
/// PR8 del lienzo «Documentos · listado sin pestañas»: la barra se retira pero cada Id de <c>?pestana=</c> se conserva como
/// redirección a su sustituto, con paridad de roles. Aquí se fija, por Id y por rol, lo que antes decidía la barra.
/// </summary>
public partial class DocumentosGen2Tests
{
    private string UrlDeLaPagina() => Services.GetRequiredService<NavigationManager>().Uri;

    private static void EsperarListado(IRenderedComponent<PaginaDocumentos> cut, MediadorControlado mediador) =>
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public void Preventivo_lleva_a_Faltan_documentos_de_Alertas_a_quien_Alertas_abre(string rol)
    {
        _ = Renderizar(rol: rol, url: "documentos?pestana=sugerencias");

        UrlDeLaPagina().Should().EndWith("/alertas", "su sustituto es la seccion «Faltan documentos» de /alertas (PR5), no el listado");
    }

    [Fact]
    public void Preventivo_para_el_Usuario_de_Cliente_se_queda_en_el_listado_porque_Alertas_no_se_le_abre()
    {
        var (cut, mediador) = Renderizar(rol: "Cliente", url: "documentos?pestana=sugerencias");

        EsperarListado(cut, mediador);
        UrlDeLaPagina().Should().NotContain("/alertas");
        UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().BeNull("el listado no hereda un filtro que la pestaña nunca fue");
    }
    [Fact]
    public void Plataformas_CAE_sin_acreditacion_es_el_listado_agrupado_por_Plataforma_CAE()
    {
        var (cut, mediador) = Renderizar(url: "documentos?pestana=plataforma");

        cut.WaitForAssertion(() => UrlDeLaPagina().Should().Contain("agrupar=plataforma").And.NotContain("pestana"));
        EsperarListado(cut, mediador);
        cut.FindComponents<PlataformaTab>().Should().BeEmpty("sin acreditacionId el sustituto es el listado agrupado");
    }

    [Fact]
    public void Plataformas_CAE_con_acreditacion_sigue_siendo_la_vista_de_Plataformas_CAE_para_quien_gestiona()
    {
        var (cut, _) = Renderizar(url: $"documentos?pestana=plataforma&acreditacionId={Guid.NewGuid()}");

        cut.WaitForAssertion(() => cut.FindComponents<PlataformaTab>().Should().NotBeEmpty());
        UrlDeLaPagina().Should().Contain("pestana=plataforma", "el bucle de corrección de Mi trabajo sigue llegando por su Id");
        cut.FindAll("[role=tablist]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public void Plataformas_CAE_con_acreditacion_sin_rol_de_gestion_se_queda_en_el_listado(string rol)
    {
        var (cut, mediador) = Renderizar(rol: rol, url: $"documentos?pestana=plataforma&acreditacionId={Guid.NewGuid()}");

        EsperarListado(cut, mediador);
        cut.FindComponents<PlataformaTab>().Should().BeEmpty();
    }

    [Fact]
    public void Reclamaciones_sigue_siendo_su_vista_para_quien_gestiona()
    {
        var (cut, _) = Renderizar(rol: "GestorCae", url: "documentos?pestana=reclamaciones");

        cut.WaitForAssertion(() => cut.FindComponents<ReclamacionesTab>().Should().NotBeEmpty());
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public void Reclamaciones_sin_rol_de_gestion_se_queda_en_el_listado(string rol)
    {
        var (cut, mediador) = Renderizar(rol: rol, url: "documentos?pestana=reclamaciones");

        EsperarListado(cut, mediador);
        cut.FindComponents<ReclamacionesTab>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador", "revision-ia", "/documentos/revision-ia")]
    [InlineData("DireccionCae", "revision-ia", "/documentos/revision-ia")]
    [InlineData("CoordinadorCae", "revision-ia", "/documentos/revision-ia")]
    [InlineData("GestorCae", "revision-ia", "/documentos/revision-ia")]
    [InlineData("Administrador", "plantillas", "/plantillas")]
    [InlineData("DireccionCae", "plantillas", "/plantillas")]
    [InlineData("CoordinadorCae", "plantillas", "/plantillas")]
    [InlineData("GestorCae", "plantillas", "/plantillas")]
    public void Revision_IA_y_Plantillas_llevan_a_su_pantalla_a_quien_gestiona(string rol, string id, string destino)
    {
        _ = Renderizar(rol: rol, url: $"documentos?pestana={id}");

        UrlDeLaPagina().Should().EndWith(destino);
    }

    [Theory]
    [InlineData("Consulta", "revision-ia")]
    [InlineData("Cliente", "revision-ia")]
    [InlineData("Consulta", "plantillas")]
    [InlineData("Cliente", "plantillas")]
    public void Revision_IA_y_Plantillas_sin_rol_de_gestion_se_quedan_en_el_listado_sin_salir_de_la_pantalla(string rol, string id)
    {
        var (cut, mediador) = Renderizar(rol: rol, url: $"documentos?pestana={id}");

        EsperarListado(cut, mediador);
        UrlDeLaPagina().Should().NotContain("/plantillas").And.NotContain("revision-ia");
    }

    [Theory]
    [InlineData("listado")]
    [InlineData("estado")]
    [InlineData("inventado")]
    public void Un_Id_desconocido_o_del_listado_cae_en_el_listado_sin_barra(string id)
    {
        var (cut, mediador) = Renderizar(url: $"documentos?pestana={id}");

        EsperarListado(cut, mediador);
        cut.FindAll("[role=tablist]").Should().BeEmpty();
    }
}
