using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class VehiculosEstadoDocumentalTests
{
    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void La_lista_conserva_lectura_y_acciones_segun_el_rol(string rol, bool escritura)
    {
        var vehiculo = Vehiculo("Camión grúa", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -3));
        var cut = Renderizar(new MediadorFalso { Almacen = { vehiculo } }, rol: rol);

        Cabeceras(cut).Should().Equal("", "Vehículo", "Empresa", "Modelo", "Documentación", "Estado", "");
        cut.Find("a.boton-360-pagina").GetAttribute("href").Should().Be($"/vehiculos/{vehiculo.Id}");
        cut.Find(".celda-vehiculo-matricula button").Should().NotBeNull();
        cut.FindAll("button").Any(b => b.TextContent.Contains("Nuevo vehículo")).Should().Be(escritura);
        cut.FindAll("button[aria-label='Selección múltiple']").Any().Should().Be(escritura);
        cut.Find("summary[data-keytip=T]").GetAttribute("aria-label").Should().Contain("Documentación");
        cut.Find("button[data-keytip=P]").GetAttribute("aria-label").Should().Contain("Empresa");
        cut.Find("button[data-keytip=A]").TextContent.Should().Contain("Agrupar");
        cut.Find("button[data-keytip=E]").TextContent.Should().Contain("Expandir todo");
        cut.Find("a[data-keytip=X]").GetAttribute("href").Should().StartWith("/vehiculos/exportar.xlsx");

        var detalle = AbrirIncidencias(cut);
        detalle.QuerySelector(".incidencia-tipo")!.TextContent.Should().Be("ITV");
        detalle.QuerySelectorAll("button").Count().Should().Be(escritura ? 1 : 0);
        if (escritura) detalle.QuerySelector("button")!.TextContent.Should().Be("Gestionar");
        cut.Find("button.enlace-nombre-fila").Click();
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(vehiculo.Id);
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    public void Lo_correcto_se_muestra_como_texto_gris_sin_pastilla(EstadoDocumento estado)
    {
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo("Furgoneta", estado) } });
        var celda = CeldaDeEstado(cut);
        celda.QuerySelector(".estado-fila-correcto")!.TextContent.Should().Contain("Sin incidencias");
        celda.QuerySelectorAll(".badge").Should().BeEmpty();
        cut.FindAll(".boton-expandir-incidencias").Should().BeEmpty();
    }

    [Fact]
    public void La_fraccion_documental_usa_los_recuentos_del_dto_y_no_las_incidencias()
    {
        var vehiculo = Vehiculo("Camión", EstadoDocumento.SinConfirmar, IncidenciaDe("ITV", EstadoDocumento.SinConfirmar))
            with
        { DocumentosRegistrados = 7, DocumentosVigentes = 5 };
        var cut = Renderizar(new MediadorFalso { Almacen = { vehiculo } });
        cut.Find(".barra-cumplimiento-cifra").TextContent.Should().Be("5/7");
        cut.Find(".barra-cumplimiento").GetAttribute("aria-label").Should().Contain("5 de 7");
    }

    [Fact]
    public void Agrupar_por_empresa_conserva_las_filas_el_total_y_el_acceso_360()
    {
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo("A"), Vehiculo("B") } }, "vehiculos?agrupar=empresa");
        cut.Find(".vehiculos-grupo").TextContent.Should().Be("Montajes Ebro S.L.");
        Filas(cut).Should().HaveCount(2);
        cut.FindAll("a.boton-360-pagina").Should().HaveCount(2);
        cut.Find(".paginador-texto").TextContent.Should().Contain("2 de 2");
    }
}
