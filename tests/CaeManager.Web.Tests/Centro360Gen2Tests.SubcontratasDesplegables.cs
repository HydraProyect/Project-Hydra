using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Bloque 4 del 21-10 de Centro 360, «Documentación Empresa»: las Empresas del Centro van primero y cada Subcontrata
/// en un desplegable propio, plegado por defecto, con el patrón de «Agrupar» (#1570): «· N» junto al nombre y, plegado,
/// la pastilla roja de incidencias; las filas largas piden «Ver los N restantes».
/// </summary>
public partial class Centro360Gen2Tests
{
    private static DocumentoEmpresaCentroDto DocumentoVigente(string nombre) =>
        new(Guid.NewGuid(), nombre, Guid.NewGuid(), EstadoDocumento.Vigente, new(2027, 1, 1), false);

    private static DocumentoEmpresaCentroDto DocumentoVencido(string nombre) =>
        new(Guid.NewGuid(), nombre, Guid.NewGuid(), EstadoDocumento.Vencido, new(2026, 10, 1), true);

    private async Task<IRenderedComponent<CaeManager.Web.Features.Centros.Pages.CentroDetalle>> AbrirDocumentacionAsync(
        Guid id, params DocumentacionEmpresaCentroDto[] empresas)
    {
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.DocumentacionEmpresas[id] = [.. empresas];
        var cut = Renderizar(id);
        await cut.Find("[role=tab][aria-controls$='-documentacion-centro']").ClickAsync(new MouseEventArgs());
        return cut;
    }

    [Fact]
    public async Task Las_Subcontratas_de_Documentacion_Empresa_nacen_plegadas_tras_las_Empresas_y_se_abren_y_cierran()
    {
        var id = Guid.NewGuid();
        var cut = await AbrirDocumentacionAsync(id,
            new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Empresa Sur", false, [DocumentoVigente("Certificado")]),
            new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Montajes Jalón", true, [DocumentoVencido("Seguro de RC"), DocumentoVigente("TGSS")]));

        // Plegada: solo la fila de la Empresa; la cabecera dice nombre, «· 2» y la pastilla roja (1 documento en rojo).
        cut.FindAll(".centro-documentacion-fila").Should().ContainSingle();
        var cabecera = cut.Find("[data-subcontrata-docs] button.grupo-lista-cabecera");
        cabecera.GetAttribute("aria-expanded").Should().Be("false");
        cabecera.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim().Should().Be("Montajes Jalón");
        cabecera.QuerySelector(".grupo-lista-contador")!.TextContent.Trim().Should().Be("· 2");
        cabecera.QuerySelector(".grupo-lista-incidencias").Should().NotBeNull();

        await cabecera.ClickAsync(new MouseEventArgs());

        var abierta = cut.Find("[data-subcontrata-docs] button.grupo-lista-cabecera");
        abierta.GetAttribute("aria-expanded").Should().Be("true");
        abierta.QuerySelector(".grupo-lista-incidencias").Should().BeNull("abierto, las filas rojas ya se ven");
        cut.FindAll(".centro-documentacion-fila").Select(f => f.QuerySelector(".centro-documentacion-nombre > span")!.TextContent.Trim())
            .Should().Equal("Certificado", "Seguro de RC", "TGSS");

        await abierta.ClickAsync(new MouseEventArgs());

        cut.FindAll(".centro-documentacion-fila").Should().ContainSingle();
    }

    [Fact]
    public async Task Cada_Subcontrata_se_abre_por_separado()
    {
        var id = Guid.NewGuid();
        var cut = await AbrirDocumentacionAsync(id,
            new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Empresa Sur", false, [DocumentoVigente("Certificado")]),
            new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Montajes Jalón", true, [DocumentoVigente("TGSS")]),
            new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Pinturas Ebro", true, [DocumentoVigente("Seguro")]));

        await cut.FindAll("[data-subcontrata-docs] button.grupo-lista-cabecera")[1].ClickAsync(new MouseEventArgs());

        var cabeceras = cut.FindAll("[data-subcontrata-docs] button.grupo-lista-cabecera");
        cabeceras.Select(c => c.GetAttribute("aria-expanded")).Should().Equal("false", "true");
        cut.FindAll(".centro-documentacion-fila").Select(f => f.QuerySelector(".centro-documentacion-nombre > span")!.TextContent.Trim())
            .Should().Equal("Certificado", "Seguro");
    }

    [Fact]
    public async Task Una_Subcontrata_con_muchos_documentos_ensena_diez_y_pide_Ver_los_restantes()
    {
        var id = Guid.NewGuid();
        var documentos = Enumerable.Range(1, 14).Select(n => DocumentoVigente($"Documento {n:00}")).ToList();
        var cut = await AbrirDocumentacionAsync(id, new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Montajes Jalón", true, documentos));

        await cut.Find("[data-subcontrata-docs] button.grupo-lista-cabecera").ClickAsync(new MouseEventArgs());

        cut.FindAll(".centro-documentacion-fila").Should().HaveCount(10);
        var verRestantes = cut.Find("button[data-accion=ver-restantes]");
        verRestantes.TextContent.Should().Contain("4");

        await verRestantes.ClickAsync(new MouseEventArgs());

        cut.FindAll(".centro-documentacion-fila").Should().HaveCount(14);
        cut.FindAll("button[data-accion=ver-restantes]").Should().BeEmpty();
    }

    [Fact]
    public async Task Los_documentos_con_incidencia_de_una_Subcontrata_salen_los_primeros_aunque_haya_mas_de_diez()
    {
        var id = Guid.NewGuid();
        var documentos = Enumerable.Range(1, 12).Select(n => DocumentoVigente($"Documento {n:00}")).ToList();
        documentos.Add(DocumentoVencido("Seguro vencido"));
        var cut = await AbrirDocumentacionAsync(id, new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Montajes Jalón", true, documentos));

        await cut.Find("[data-subcontrata-docs] button.grupo-lista-cabecera").ClickAsync(new MouseEventArgs());

        var filas = cut.FindAll(".centro-documentacion-fila");
        filas.Should().HaveCount(10);
        filas[0].QuerySelector(".centro-documentacion-nombre > span")!.TextContent.Trim().Should().Be("Seguro vencido");
        cut.Find("[data-subcontrata-docs] + [role=row] button[data-accion=ver-restantes], [role=row] > [role=cell] > button[data-accion=ver-restantes]")
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Sin_Subcontratas_no_aparece_ningun_desplegable()
    {
        var id = Guid.NewGuid();
        var cut = await AbrirDocumentacionAsync(id, new DocumentacionEmpresaCentroDto(Guid.NewGuid(), "Empresa Sur", false, [DocumentoVigente("Certificado")]));

        cut.FindAll("[data-subcontrata-docs]").Should().BeEmpty();
    }
}