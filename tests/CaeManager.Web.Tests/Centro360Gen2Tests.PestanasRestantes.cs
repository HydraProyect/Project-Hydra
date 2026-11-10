using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Pages;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pulido de las pestañas restantes de Centro 360 para la demo del 10-11: «Documentación Empresa» con el nombre que lleva a la
/// ficha 360 del Documento y «Ver» que abre el visor (el patrón de la ficha de Trabajador y de Empresa), la dirección de «Información» y de «Ficha»
/// sin recuadro de botón y sin cortarse, y la rejilla de cajas de «Ficha» sin huecos. La maquetación (ancho de columnas, flujo
/// a dos columnas) es CSS que bUnit no calcula: aquí se fija el marcado que la sostiene y la hoja fuente.
/// </summary>
public partial class Centro360Gen2Tests
{
    private static string CssCentroDetalle()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Centros", "Pages", "CentroDetalle.razor.css"));
        return Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private IRenderedComponent<CentroDetalle> DocumentacionConDocumentoYFaltante(Guid id, Guid documentoId, string rol = "Administrador")
    {
        this.ConRolDeEscritura(rol);
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.DocumentacionEmpresas[id] = [new(Guid.NewGuid(), "Empresa Sur", false,
            [new(Guid.NewGuid(), "Seguro", documentoId, EstadoDocumento.Vigente, new(2027, 1, 1), true),
             new(Guid.NewGuid(), "Certificado", null, EstadoDocumento.Faltante, null, false)])];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=documentacion-centro");
        return Renderizar(id);
    }

    [Fact]
    public void Documentacion_Empresa_el_nombre_enlaza_a_la_ficha_del_Documento_solo_en_filas_con_Documento()
    {
        var documentoId = Guid.NewGuid();
        var cut = DocumentacionConDocumentoYFaltante(Guid.NewGuid(), documentoId);

        var filas = cut.FindAll(".centro-documentacion-fila");
        filas.Should().HaveCount(2);
        var conDocumento = filas.Single(f => f.TextContent.Contains("Seguro"));
        var enlace = conDocumento.QuerySelector("a.centro-documentacion-enlace")!;
        enlace.GetAttribute("href").Should().Be($"/documentos/{documentoId}");
        enlace.TextContent.Trim().Should().Be("Seguro");
        // El nombre sigue siendo el primer span del bloque, con el mismo texto que antes.
        conDocumento.QuerySelector(".centro-documentacion-nombre > span")!.TextContent.Trim().Should().Be("Seguro");

        var faltante = filas.Single(f => f.TextContent.Contains("Certificado"));
        faltante.QuerySelector(".centro-documentacion-enlace").Should().BeNull("el requisito sin Documento no tiene ficha a la que ir");
        faltante.QuerySelector(".centro-documentacion-nombre > span")!.TextContent.Trim().Should().Be("Certificado");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("Consulta")]
    public async Task Documentacion_Empresa_Ver_abre_el_visor_del_Documento_para_todo_rol_y_solo_en_filas_con_Documento(string rol)
    {
        var documentoId = Guid.NewGuid();
        var cut = DocumentacionConDocumentoYFaltante(Guid.NewGuid(), documentoId, rol);

        cut.FindComponent<VisorDocumento>().Instance.Visible.Should().BeFalse();
        var filas = cut.FindAll(".centro-documentacion-fila");
        filas.Single(f => f.TextContent.Contains("Certificado")).QuerySelector("[data-accion=ver-documento-empresa]")
            .Should().BeNull("sin Documento no hay PDF que ver");
        var ver = filas.Single(f => f.TextContent.Contains("Seguro")).QuerySelector(".celda-accion-plataforma [data-accion=ver-documento-empresa]")!;
        ver.TextContent.Trim().Should().Be("Ver");
        ver.GetAttribute("aria-label").Should().Be("Ver el PDF del documento «Seguro» de Empresa Sur");

        await ver.ClickAsync(new MouseEventArgs());

        var visor = cut.FindComponent<VisorDocumento>().Instance;
        visor.Visible.Should().BeTrue();
        visor.DocumentoId.Should().Be(documentoId);
        visor.Titulo.Should().Be("Seguro");
    }

    [Fact]
    public void Documentacion_Empresa_la_rejilla_da_ancho_al_Documento_y_fija_las_tres_columnas_de_cifras_y_accion()
    {
        var css = CssCentroDetalle();
        var cuerpo = Regex.Match(css, @"\.centro-documentacion-cabecera,\s*\.centro-documentacion-fila\s*\{(?<c>[^}]*)\}").Groups["c"].Value;
        cuerpo.Should().Contain("grid-template-columns: minmax(0, 1.7fr) minmax(0, 1.1fr) 88px 56px 132px",
            "Documento y estado en la plataforma se reparten el ancho; Caducidad, Bloquea y Acción son pistas fijas, sin «auto» por contenido");
        cuerpo.Should().NotContain("auto", "con una pista «auto» cada fila mediría distinto y las columnas dejarían de caer bajo su rótulo");
    }

    [Fact]
    public void Direccion_de_Informacion_y_de_Ficha_es_un_dato_en_linea_sin_recuadro_de_boton()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Detalles[id] = Detalle(id, "Centro Sur") with { Direccion = "Polígono Industrial Norte, nave 4, 28805 Alcalá de Henares" };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=ficha");
        var cut = Renderizar(id);

        var direcciones = cut.FindAll("button.boton-copiar")
            .Where(b => b.TextContent.Contains("Alcalá de Henares")).ToList();
        direcciones.Should().HaveCount(2, "una en la tarjeta «Información» y otra en la caja «Acceso» de Ficha");
        direcciones.Should().OnlyContain(b => b.ClassList.Contains("boton-copiar-en-linea"),
            "el dato es el botón, sin el recuadro de «Copiar»");
        cut.Find("[data-caja=acceso-centro]").QuerySelectorAll("button.boton-copiar")
            .Should().OnlyContain(b => b.ClassList.Contains("boton-copiar-en-linea"));
    }

    [Fact]
    public void Dato_copiable_de_la_ficha_envuelve_y_las_cajas_de_Ficha_fluyen_en_dos_columnas_sin_huecos()
    {
        var css = CssCentroDetalle();
        Regex.Match(css, @"\.contenedor-pagina ::deep \.dato-rejilla-valor \.boton-copiar-en-linea\s*\{(?<c>[^}]*)\}").Groups["c"].Value
            .Should().Contain("white-space: normal").And.Contain("overflow-wrap: anywhere").And.Contain("min-width: 0");
        var flujo = Regex.Match(css, @"\.centro-ficha-canales ::deep \.cajas-ficha-editables\s*\{(?<c>[^}]*)\}").Groups["c"].Value;
        flujo.Should().Contain("display: block").And.Contain("columns: 2");
        css.Should().Contain("@media (max-width: 900px)");
        Regex.Match(css, @"\.centro-ficha-canales ::deep \.cajas-ficha-editables \.cajas-ficha-caja\s*\{(?<c>[^}]*)\}").Groups["c"].Value
            .Should().Contain("break-inside: avoid");
    }
}
