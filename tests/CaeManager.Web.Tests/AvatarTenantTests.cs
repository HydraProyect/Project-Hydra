using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>AvatarTenant</c> (selector de Tenant beneficiario): el logo si lo hay y, si no, las iniciales.
/// Es decorativo —el nombre lo lleva el texto de al lado—, así que va oculto a los lectores de pantalla.
/// </summary>
public class AvatarTenantTests : BunitContext
{
    [Fact]
    public void Sin_logo_pinta_las_iniciales_y_ninguna_imagen()
    {
        var cut = Render<AvatarTenant>(p => p.Add(x => x.Nombre, "Norte Servicios"));

        cut.Find("span.avatar-tenant").TextContent.Trim().Should().Be("NS");
        cut.FindAll("img").Should().BeEmpty();
    }

    [Fact]
    public void Con_logo_pinta_la_imagen_sin_texto_alternativo_y_sin_iniciales()
    {
        var cut = Render<AvatarTenant>(p => p
            .Add(x => x.Nombre, "Norte Servicios")
            .Add(x => x.LogoUrl, "/tenants/abc/logo?v=1234"));

        var imagen = cut.Find("img.avatar-tenant-logo");
        imagen.GetAttribute("src").Should().Be("/tenants/abc/logo?v=1234");
        imagen.GetAttribute("alt").Should().BeEmpty("el nombre ya lo dice el texto de al lado");
        cut.Find("span.avatar-tenant").TextContent.Trim().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Una_url_vacia_equivale_a_sin_logo(string? url)
    {
        var cut = Render<AvatarTenant>(p => p.Add(x => x.Nombre, "Montajes Ebro").Add(x => x.LogoUrl, url));

        cut.FindAll("img").Should().BeEmpty();
        cut.Find("span.avatar-tenant").TextContent.Trim().Should().Be("ME");
    }

    [Fact]
    public void Es_decorativo_para_los_lectores_de_pantalla()
    {
        var cut = Render<AvatarTenant>(p => p.Add(x => x.Nombre, "Norte Servicios"));

        cut.Find("span.avatar-tenant").GetAttribute("aria-hidden").Should().Be("true");
    }

    [Fact]
    public void El_tamano_pequeno_anade_su_clase()
    {
        var cut = Render<AvatarTenant>(p => p
            .Add(x => x.Nombre, "Norte Servicios")
            .Add(x => x.Tamano, TamanoAvatarTenant.Pequeno));

        cut.Find("span.avatar-tenant").ClassList.Should().Contain("avatar-tenant-pequeno");
    }

    [Theory]
    [InlineData("Norte Servicios", "NS")]
    [InlineData("Hoteles Grand Budapest", "HG")]
    [InlineData("  refrielectric  ", "R")]
    [InlineData("& Co", "C")]
    [InlineData("123 456", "?")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Las_iniciales_son_las_de_las_dos_primeras_palabras_que_empiezan_por_letra(string? nombre, string esperado)
    {
        AvatarTenant.Iniciales(nombre).Should().Be(esperado);
    }

    [Fact]
    public void La_url_del_logo_lleva_la_version_escapada_o_es_nula_sin_ella()
    {
        var id = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");

        AvatarTenant.UrlDeLogo(id, "ab cd&e").Should().Be($"/tenants/{id}/logo?v=ab%20cd%26e");
        AvatarTenant.UrlDeLogo(id, null).Should().BeNull();
        AvatarTenant.UrlDeLogo(id, "").Should().BeNull();
    }
}
