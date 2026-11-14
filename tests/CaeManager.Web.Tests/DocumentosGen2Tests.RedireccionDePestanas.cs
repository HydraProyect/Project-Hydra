using Bunit;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Web.Components.DesignSystem;
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
    public void Preventivo_se_convierte_en_el_listado_con_Vence_en_90_dias_por_vencimiento(string rol)
    {
        var (cut, mediador) = Renderizar(ConDocumentos(Documento("Seguro RC")), rol: rol, url: "documentos?pestana=sugerencias");

        cut.WaitForAssertion(() =>
        {
            UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(90);
            UrlDeLaPagina().Should().Contain("VenceEn=90").And.Contain("orden=vencimiento").And.NotContain("pestana");
        });
        cut.FindAll("[role=tablist]").Should().BeEmpty();
    }

    [Fact]
    public void Preventivo_con_un_plazo_propio_en_la_URL_no_lo_pisa()
    {
        var (cut, mediador) = Renderizar(url: "documentos?pestana=sugerencias&VenceEn=30");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(30));
        UrlDeLaPagina().Should().Contain("VenceEn=30").And.NotContain("VenceEn=90").And.NotContain("pestana");
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public void Preventivo_sin_rol_de_gestion_tambien_cae_en_el_listado_con_el_plazo(string rol)
    {
        var (cut, mediador) = Renderizar(rol: rol, url: "documentos?pestana=sugerencias");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).VenceEnDias.Should().Be(90));
        UrlDeLaPagina().Should().NotContain("pestana");
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

    /// <summary>Una vista recordada que no es la de inicio: borrarla o pisarla se nota.</summary>
    private const string VistaRecordadaDelUsuario = """{"Estado":"Vencido"}""";

    /// <summary>
    /// /documentos montado, con la vista recordada restaurada (no vacía) y la navegación como con circuito: cada NavigateTo
    /// espera a que el test lo confirme, igual que el aviso de cambio de dirección llega de ida y vuelta con el navegador.
    /// </summary>
    private async Task<(IRenderedComponent<PaginaDocumentos> Cut, MediadorControlado Mediador, NavegacionRetenida Navegacion)>
        MontarConVistaRecordadaYCircuitoAsync()
    {
        var navegacion = new NavegacionRetenida("http://localhost/documentos");
        Services.AddSingleton<NavigationManager>(navegacion);
        var (cut, mediador) = Renderizar(ConVistaRecordada(VistaRecordadaDelUsuario));
        cut.WaitForAssertion(() => UrlDeLaPagina().Should().Contain("Estado=Vencido", "barrera: la vista recordada se restauró"));
        EsperarListado(cut, mediador);
        cut.FindComponents<VistaRecordadaDeListado>().Should().NotBeEmpty("barrera: la vista recordada está montada y escuchando");
        await Task.Delay(VistaRecordadaDeListado.Rebote + TimeSpan.FromSeconds(1));
        await cut.InvokeAsync(() => { });
        mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty("barrera: restaurar no escribe");
        navegacion.Retener = true;
        return (cut, mediador, navegacion);
    }

    /// <summary>
    /// La paleta o el menú navegan a «?pestana=» ya dentro de /documentos: el aviso del enlace llega del navegador, la página
    /// lo traduce a filtros y el aviso de esa traducción llega después, también de vuelta. Ninguno de los dos es del usuario.
    /// </summary>
    private static async Task NavegarAPestanaConCircuitoAsync(
        IRenderedComponent<PaginaDocumentos> cut, NavegacionRetenida navegacion, string pestana, string filtroDeLaUrl)
    {
        await cut.InvokeAsync(() => navegacion.NavigateTo($"documentos?pestana={pestana}"));
        await cut.InvokeAsync(() => navegacion.Confirmar(navegacion.Destinos[^1]));
        cut.WaitForAssertion(() => navegacion.Destinos[^1].Should().Contain(filtroDeLaUrl).And.NotContain("pestana"));
        await cut.InvokeAsync(() => navegacion.Confirmar(navegacion.Destinos[^1]));
        cut.WaitForAssertion(() => navegacion.Uri.Should().Contain(filtroDeLaUrl).And.NotContain("pestana"));
    }

    /// <summary>
    /// Ya dentro de /documentos (con la vista recordada montada y escuchando la dirección), un enlace guardado a «?pestana=» lo
    /// traduce la página a filtros: eso no es una acción del usuario y la vista recordada ni lo guarda ni olvida la que había
    /// (en main una pestaña nunca la tocaba). Los avisos llegan después del anuncio de la página, como en el circuito real;
    /// el reloj de este arnés es el real: se espera al rebote.
    /// </summary>
    [Theory]
    [InlineData("sugerencias", "VenceEn=90")]
    [InlineData("plataforma", "agrupar=plataforma")]
    public async Task La_redireccion_de_una_pestana_con_circuito_ni_guarda_ni_olvida_la_vista_recordada(string pestana, string filtroDeLaUrl)
    {
        var (cut, mediador, navegacion) = await MontarConVistaRecordadaYCircuitoAsync();

        await NavegarAPestanaConCircuitoAsync(cut, navegacion, pestana, filtroDeLaUrl);
        await Task.Delay(VistaRecordadaDeListado.Rebote + TimeSpan.FromSeconds(1));
        await cut.InvokeAsync(() => { });

        mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty("la redirección no es un cambio del usuario");
        mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().BeEmpty("el aviso tardío del enlace no borra lo recordado");
    }

    [Fact]
    public async Task Un_cambio_de_filtro_del_usuario_tras_la_redireccion_si_se_guarda_como_vista_recordada()
    {
        var (cut, mediador, navegacion) = await MontarConVistaRecordadaYCircuitoAsync();
        await NavegarAPestanaConCircuitoAsync(cut, navegacion, "sugerencias", "VenceEn=90");

        navegacion.Retener = false;
        await cut.InvokeAsync(() => navegacion.NavigateTo("documentos?VenceEn=30&orden=vencimiento"));

        await Task.Delay(VistaRecordadaDeListado.Rebote + TimeSpan.FromSeconds(1));
        await cut.InvokeAsync(() => { });
        mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().ContainSingle()
            .Which.ValoresJson.Should().Contain("30");
        mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().BeEmpty();
    }

    /// <summary>
    /// Entrada en frío con «?pestana=»: la página traduce el enlace al cargar, quizá antes de que la vista recordada esté
    /// montada, y el aviso de la traducción llega después. Tampoco escribe, ni restaura lo recordado (la URL traía consulta).
    /// </summary>
    [Theory]
    [InlineData("sugerencias", "VenceEn=90")]
    [InlineData("plataforma", "agrupar=plataforma")]
    public async Task Una_entrada_en_frio_con_pestana_ni_guarda_ni_olvida_la_vista_recordada(string pestana, string filtroDeLaUrl)
    {
        var navegacion = new NavegacionRetenida($"http://localhost/documentos?pestana={pestana}") { Retener = true };
        Services.AddSingleton<NavigationManager>(navegacion);
        var (cut, mediador) = Renderizar(ConVistaRecordada(VistaRecordadaDelUsuario), url: $"documentos?pestana={pestana}");
        cut.WaitForAssertion(() => navegacion.Destinos[^1].Should().Contain(filtroDeLaUrl).And.NotContain("pestana"));
        await cut.InvokeAsync(() => navegacion.Confirmar(navegacion.Destinos[^1]));
        EsperarListado(cut, mediador);

        await Task.Delay(VistaRecordadaDeListado.Rebote + TimeSpan.FromSeconds(1));
        await cut.InvokeAsync(() => { });

        mediador.Enviadas.OfType<GuardarVistaRecordadaCommand>().Should().BeEmpty();
        mediador.Enviadas.OfType<OlvidarVistaRecordadaCommand>().Should().BeEmpty();
        navegacion.Uri.Should().NotContain("Estado=Vencido", "con consulta en la URL no se restaura");
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
