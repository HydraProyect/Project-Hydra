using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionVehiculosDeCentro;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Bloque 4 del 21-10 de Centro 360, pestaña Trabajadores: el selector «Trabajadores · N | Vehículos · N», la fila de
/// un Vehículo con su fracción «n de m» validados en la plataforma del Centro, los Vehículos y Trabajadores de cada
/// Subcontrata en un desplegable propio, plegado, con «Subcontrata de …» bajo el nombre, y el buscador rotulado para
/// Vehículos.
/// </summary>
public partial class Centro360Gen2Tests
{
    private static VehiculoDocumentacionCentroDto VehiculoCentro(string placa, string nombre, string empleador,
        Guid? subcontrataId = null, string? contratista = null, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), nombre, nombre, placa, empleador, subcontrataId, contratista, documentos);

    private static DocumentoRequeridoDto DocumentoEnPlataforma(string nombre, EstadoAcreditacion estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, EstadoDocumento.Vigente, new DateOnly(2027, 1, 1),
            AcreditacionesEnElCentro: [new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", estado)]);

    private (IRenderedComponent<CentroDetalle> Cut, MediatorFalso Mediador) RenderizarConVehiculos(
        Guid id, params VehiculoDocumentacionCentroDto[] vehiculos) =>
        RenderizarConVehiculos(id, null, vehiculos);

    private (IRenderedComponent<CentroDetalle> Cut, MediatorFalso Mediador) RenderizarConVehiculos(
        Guid id, Action<MediatorFalso>? preparar, params VehiculoDocumentacionCentroDto[] vehiculos)
    {
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Resumenes[id] = Resumen(id, "Centro Sur");
        mediador.Vehiculos[id] = [.. vehiculos];
        preparar?.Invoke(mediador);
        return (Renderizar(id), mediador);
    }

    private static Task ElegirVehiculosAsync(IRenderedComponent<CentroDetalle> cut) =>
        cut.WaitForElement("button[data-recurso=vehiculos]").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task El_selector_cuenta_Trabajadores_y_Vehiculos_y_cambia_de_vista_sin_Asignar_vehiculo()
    {
        this.ConRolDeEscritura();
        var id = Guid.NewGuid();
        var (cut, _) = RenderizarConVehiculos(id,
            m => m.Asignaciones[id] = [Asignado("Juan Pérez", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente))],
            VehiculoCentro("4821KLM", "Renault Kangoo", "Ibertec GmbH"),
            VehiculoCentro("7310JHB", "Citroen Berlingo", "Ibertec GmbH"));

        cut.WaitForAssertion(() => cut.Find("button[data-recurso=vehiculos]").TextContent.Trim().Should().Be("Vehículos · 2"));
        var trabajadores = cut.Find("button[data-recurso=trabajadores]");
        trabajadores.GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("[data-vehiculos-centro]").Should().BeEmpty();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("+ Asignar trabajador");

        await ElegirVehiculosAsync(cut);

        cut.Find("button[data-recurso=vehiculos]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-vehiculos-centro]").Should().NotBeNull();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(t => t.Contains("Asignar"),
            "un Vehículo no se asigna a un Centro: la maqueta no tiene «+ Asignar vehículo» y el modelo tampoco");
        cut.Find(".barra-filtros input").GetAttribute("placeholder").Should().Be("Buscar vehículo por matrícula o modelo…");

        await cut.Find("button[data-recurso=trabajadores]").ClickAsync(new MouseEventArgs());

        cut.FindAll("[data-vehiculos-centro]").Should().BeEmpty();
        cut.Find(".barra-filtros input").GetAttribute("placeholder").Should().NotBe("Buscar vehículo por matrícula o modelo…");
    }

    [Fact]
    public async Task La_fila_de_un_Vehiculo_ensena_matricula_empleadora_fraccion_validada_y_estado_en_la_plataforma()
    {
        var id = Guid.NewGuid();
        var (cut, _) = RenderizarConVehiculos(id,
            VehiculoCentro("4821KLM", "Renault Kangoo", "Ibertec GmbH", documentos:
            [
                DocumentoEnPlataforma("ITV", EstadoAcreditacion.Aceptada),
                DocumentoEnPlataforma("Seguro obligatorio", EstadoAcreditacion.Subida),
                DocumentoEnPlataforma("Permiso de circulación", EstadoAcreditacion.Aceptada),
            ]),
            VehiculoCentro("7310JHB", "Citroen Berlingo", "Ibertec GmbH"));

        await ElegirVehiculosAsync(cut);

        var filas = cut.FindAll("[data-vehiculos-centro] [data-fraccion-validados]");
        filas.Select(f => f.TextContent.Trim()).Should().Equal("2 de 3", "—");
        var lista = cut.Find("[data-vehiculos-centro]").TextContent;
        lista.Should().Contain("4821KLM · Renault Kangoo").And.Contain("Ibertec GmbH").And.Contain("Pendiente");
        lista.Should().Contain("Sin documentos registrados");
    }

    [Fact]
    public async Task Los_Vehiculos_de_cada_Subcontrata_van_plegados_tras_los_propios_con_Subcontrata_de_la_contratista()
    {
        var id = Guid.NewGuid();
        var montajes = Guid.NewGuid();
        var (cut, _) = RenderizarConVehiculos(id,
            VehiculoCentro("9999ZZZ", "Furgón Jalón", "Montajes Jalón", montajes, "Obras del Ebro S.L.",
                DocumentoEnPlataforma("ITV", EstadoAcreditacion.Rechazada)),
            VehiculoCentro("4821KLM", "Renault Kangoo", "Ibertec GmbH"));

        await ElegirVehiculosAsync(cut);

        cut.FindAll("[data-vehiculos-centro] [data-fraccion-validados]").Should().ContainSingle("la Subcontrata nace plegada");
        var cabecera = cut.Find("[data-vehiculos-centro] [data-subcontrata-grupo] button.grupo-lista-cabecera");
        cabecera.GetAttribute("aria-expanded").Should().Be("false");
        cabecera.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim().Should().Be("Montajes Jalón");
        cabecera.QuerySelector(".grupo-lista-contador")!.TextContent.Trim().Should().Be("· 1 vehículo");
        cabecera.QuerySelector(".grupo-lista-subtitulo")!.TextContent.Trim().Should().Be("Subcontrata de Obras del Ebro S.L.");
        cabecera.QuerySelector(".grupo-lista-incidencias").Should().NotBeNull("plegada, avisa del Vehículo que bloquea");

        await cabecera.ClickAsync(new MouseEventArgs());

        cut.Find("[data-vehiculos-centro] [data-subcontrata-grupo] button.grupo-lista-cabecera").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-vehiculos-centro] [data-fraccion-validados]").Should().HaveCount(2);
        var texto = cut.Find("[data-vehiculos-centro]").TextContent;
        texto.IndexOf("4821KLM", StringComparison.Ordinal).Should().BeLessThan(texto.IndexOf("9999ZZZ", StringComparison.Ordinal),
            "los Vehículos de las Empresas del Centro van antes que los de las Subcontratas");
    }

    [Fact]
    public async Task Sin_Empresa_contratista_visible_la_cabecera_de_la_Subcontrata_no_lleva_subtitulo()
    {
        var id = Guid.NewGuid();
        var (cut, _) = RenderizarConVehiculos(id, VehiculoCentro("9999ZZZ", "Furgón Jalón", "Montajes Jalón", Guid.NewGuid(), contratista: null));

        await ElegirVehiculosAsync(cut);

        var cabecera = cut.Find("[data-vehiculos-centro] [data-subcontrata-grupo] button.grupo-lista-cabecera");
        cabecera.QuerySelector(".grupo-lista-subtitulo").Should().BeNull();
        cabecera.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim().Should().Be("Montajes Jalón");
    }

    [Fact]
    public void Los_Trabajadores_de_una_Subcontrata_van_en_su_desplegable_con_Subcontrata_de_la_contratista()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Resumenes[id] = Resumen(id, "Centro Sur");
        var propio = Asignado("Ana Alba", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente));
        var subcontratado = Asignado("Leo Mas", EstadoDocumento.Vigente, Documento("Formación PRL", EstadoDocumento.Vigente));
        mediador.Asignaciones[id] = [subcontratado, propio];
        var montajes = Guid.NewGuid();
        mediador.Empleadores[id] =
        [
            new(propio.TrabajadorId, "Ana Alba", "Ibertec GmbH", propio.FechaAlta),
            new(subcontratado.TrabajadorId, "Leo Mas", "Montajes Jalón", subcontratado.FechaAlta, SubcontrataId: montajes, EmpresaContratistaNombre: "Obras del Ebro S.L."),
        ];

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => cut.Find("[data-subcontrata-grupo] .grupo-lista-subtitulo").TextContent.Trim()
            .Should().Be("Subcontrata de Obras del Ebro S.L."));
        var cabecera = cut.Find("[data-subcontrata-grupo] button.grupo-lista-cabecera");
        cabecera.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim().Should().Be("Montajes Jalón");
        cabecera.QuerySelector(".grupo-lista-contador")!.TextContent.Trim().Should().Be("· 1 trabajador");
        cut.Markup.Should().Contain("Ana Alba").And.NotContain("Leo Mas");
    }

    [Fact]
    public async Task El_buscador_filtra_los_Vehiculos_por_matricula()
    {
        var id = Guid.NewGuid();
        var (cut, _) = RenderizarConVehiculos(id,
            VehiculoCentro("4821KLM", "Renault Kangoo", "Ibertec GmbH"),
            VehiculoCentro("7310JHB", "Citroen Berlingo", "Ibertec GmbH"));

        await ElegirVehiculosAsync(cut);
        await cut.Find(".barra-filtros input").InputAsync(new ChangeEventArgs { Value = "7310" });

        cut.WaitForAssertion(() => cut.FindAll("[data-vehiculos-centro] [data-fraccion-validados]").Should().ContainSingle());
        cut.Find("[data-vehiculos-centro]").TextContent.Should().Contain("7310JHB").And.NotContain("4821KLM");
    }
}
