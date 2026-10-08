using System.Xml.Linq;
using Bunit;
using CaeManager.Application.Usuarios;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>AvatarUsuario</c>: el avatar que la persona eligió en <see cref="CatalogoAvatares"/> y, si
/// no eligió ninguno, sus iniciales (decisión de producto del 2026-10-08: nunca es obligatorio).
/// Es decorativo —el nombre lo lleva el texto de al lado—, así que va oculto a los lectores de pantalla.
/// </summary>
public class AvatarUsuarioTests : BunitContext
{
    [Fact]
    public void Sin_avatar_elegido_pinta_las_iniciales_sobre_el_par_azul()
    {
        var cut = Render<AvatarUsuario>(p => p.Add(x => x.Nombre, "Marta Ibarra"));

        var avatar = cut.Find("span.avatar-usuario");
        avatar.TextContent.Trim().Should().Be("MI");
        avatar.GetAttribute("aria-hidden").Should().Be("true");
        avatar.ClassList.Should().NotContain(c => c.StartsWith("avatar-usuario-tono-"));
        cut.FindAll(".avatar-usuario-glifo").Should().BeEmpty();
    }

    [Fact]
    public void Con_avatar_elegido_pinta_su_emoji_sobre_su_tono_y_no_las_iniciales()
    {
        var cut = Render<AvatarUsuario>(p => p
            .Add(x => x.Nombre, "Marta Ibarra")
            .Add(x => x.Avatar, "buho-ambar"));

        var avatar = cut.Find("span.avatar-usuario");
        avatar.ClassList.Should().Contain("avatar-usuario-tono-ambar");
        avatar.TextContent.Trim().Should().Be("🦉");
        cut.Find(".avatar-usuario-glifo").TextContent.Should().Be("🦉");
    }

    [Theory]
    [InlineData("dragon-verde")]
    [InlineData("zorro-fucsia")]
    [InlineData("")]
    public void Una_clave_que_no_esta_en_el_catalogo_pinta_las_iniciales_y_no_un_hueco(string clave)
    {
        var cut = Render<AvatarUsuario>(p => p
            .Add(x => x.Nombre, "Marta Ibarra")
            .Add(x => x.Avatar, clave));

        cut.Find("span.avatar-usuario").TextContent.Trim().Should().Be("MI");
        cut.Find("span.avatar-usuario").ClassList.Should().NotContain(c => c.StartsWith("avatar-usuario-tono-"));
    }

    [Fact]
    public void Al_quitar_el_avatar_vuelven_las_iniciales()
    {
        var cut = Render<AvatarUsuario>(p => p
            .Add(x => x.Nombre, "Marta Ibarra")
            .Add(x => x.Avatar, "zorro-verde"));
        cut.Find("span.avatar-usuario").TextContent.Trim().Should().Be("🦊");

        cut.Render(p => p.Add(x => x.Avatar, null));

        cut.Find("span.avatar-usuario").TextContent.Trim().Should().Be("MI");
    }

    [Theory]
    [InlineData(TamanoAvatarUsuario.Pequeno, "avatar-usuario-pequeno")]
    [InlineData(TamanoAvatarUsuario.Grande, "avatar-usuario-grande")]
    public void El_tamano_solo_anade_su_clase(TamanoAvatarUsuario tamano, string clase)
    {
        var cut = Render<AvatarUsuario>(p => p.Add(x => x.Nombre, "Marta Ibarra").Add(x => x.Tamano, tamano));

        cut.Find("span.avatar-usuario").ClassList.Should().Contain(clase);
    }

    // ---- El catálogo y lo que Web le debe: un tono sin regla o un motivo sin nombre no falla al compilar ----

    [Fact]
    public void Cada_tono_del_catalogo_salvo_el_de_las_iniciales_tiene_su_fondo_en_el_css()
    {
        var css = File.ReadAllText(Path.Combine(RaizWeb(), "Components", "DesignSystem", "AvatarUsuario.razor.css"));

        // El primero es el par azul de .avatar-usuario: no necesita regla propia.
        foreach (var tono in CatalogoAvatares.Tonos.Skip(1))
            css.Should().MatchRegex($@"\.avatar-usuario-tono-{tono}\s*\{{[^}}]*background-color:",
                $"sin esa regla el tono «{tono}» se pintaría azul sin avisar");
    }

    [Theory]
    [InlineData("TextosUsuarios.resx")]
    [InlineData("TextosUsuarios.ca-ES.resx")]
    public void Cada_motivo_y_cada_tono_del_catalogo_tiene_nombre_en_el_idioma(string fichero)
    {
        var claves = XDocument.Load(Path.Combine(RaizWeb(), "Features", "Usuarios", "Recursos", fichero))
            .Root!.Elements("data")
            .Where(d => !string.IsNullOrWhiteSpace(d.Element("value")?.Value))
            .Select(d => d.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);

        var esperadas = CatalogoAvatares.Motivos.Select(m => $"AvatarMotivo_{m.Clave}")
            .Concat(CatalogoAvatares.Tonos.Select(t => $"AvatarTono_{t}"));

        esperadas.Except(claves).Should().BeEmpty(
            "el selector de /mi-avatar nombra cada opción para el lector de pantalla; sin el recurso se leería la clave");
    }

    private static string RaizWeb()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        directorio.Should().NotBeNull("hay que encontrar la raíz del repositorio para leer los ficheros de Web");
        return Path.Combine(directorio!.FullName, "src", "CaeManager.Web");
    }
}
