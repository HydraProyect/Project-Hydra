using CaeManager.Web.Features.Documentos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// La tabla única de lo que hace cada Id de <c>?pestana=</c> desde que <c>/documentos</c> no tiene barra (PR8 del lienzo
/// «Documentos · listado sin pestañas»). Cada fila es una redirección que la barra antigua hacía por sí misma.
/// </summary>
public class RedireccionDePestanasTests
{
    [Fact]
    public void Preventivo_es_el_listado_con_Vence_en_90_por_vencimiento_para_cualquier_rol()
    {
        foreach (bool? gestiona in new bool?[] { true, false, null })
        {
            var r = RedireccionDePestanas.Resolver("sugerencias", conAcreditacionId: false, gestiona);
            r.Vista.ToString().Should().Be("Listado");
            r.VenceEn.Should().Be("90");
            r.Orden.Should().Be("vencimiento");
            r.RutaExterna.Should().BeNull();
        }
    }

    [Fact]
    public void Plataformas_sin_acreditacion_agrupa_el_listado_por_Plataforma_CAE()
    {
        var r = RedireccionDePestanas.Resolver("plataforma", conAcreditacionId: false, gestionaDocumentos: true);

        r.Vista.ToString().Should().Be("Listado");
        r.Agrupar.Should().Be("plataforma");
    }

    [Theory]
    [InlineData(true, "PlataformaDeAcreditacion")]
    [InlineData(null, "PlataformaDeAcreditacion")]
    [InlineData(false, "Listado")]
    public void Plataformas_con_acreditacion_conserva_la_vista_solo_para_quien_gestiona(bool? gestiona, string esperada) =>
        RedireccionDePestanas.Resolver("plataforma", conAcreditacionId: true, gestiona).Vista.ToString().Should().Be(esperada);

    [Theory]
    [InlineData(true, "Reclamaciones")]
    [InlineData(null, "Reclamaciones")]
    [InlineData(false, "Listado")]
    public void Reclamaciones_conserva_la_vista_solo_para_quien_gestiona(bool? gestiona, string esperada) =>
        RedireccionDePestanas.Resolver("reclamaciones", conAcreditacionId: false, gestiona).Vista.ToString().Should().Be(esperada);

    [Theory]
    [InlineData("revision-ia", "/documentos/revision-ia")]
    [InlineData("plantillas", "/plantillas")]
    public void Revision_IA_y_Plantillas_salen_a_su_pantalla_solo_para_quien_gestiona(string id, string ruta)
    {
        var gestiona = RedireccionDePestanas.Resolver(id, conAcreditacionId: false, gestionaDocumentos: true);
        gestiona.Vista.ToString().Should().Be("Redirigiendo");
        gestiona.RutaExterna.Should().Be(ruta);

        var noGestiona = RedireccionDePestanas.Resolver(id, conAcreditacionId: false, gestionaDocumentos: false);
        noGestiona.Vista.ToString().Should().Be("Listado");
        noGestiona.RutaExterna.Should().BeNull("D-3: se oculta, no se enseña un «sin permiso» ni se lleva a una pantalla que no se podría abrir");

        var sinLeerElRol = RedireccionDePestanas.Resolver(id, conAcreditacionId: false, gestionaDocumentos: null);
        sinLeerElRol.Vista.ToString().Should().Be("Redirigiendo");
        sinLeerElRol.RutaExterna.Should().BeNull("hasta leer el rol no se decide el destino");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("listado")]
    [InlineData("inventado")]
    public void Un_Id_ausente_o_desconocido_es_el_listado(string? id)
    {
        var r = RedireccionDePestanas.Resolver(id, conAcreditacionId: false, gestionaDocumentos: true);

        r.Should().Be(new PestanaResuelta(Enum.Parse<VistaDePestana>("Listado")));
    }
}
