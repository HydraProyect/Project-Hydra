using Bunit;
using CaeManager.Application.Importacion.Queries;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b: salir del asistente /importacion con un archivo analizado y todavía sin importar
/// pregunta antes; sin archivo, o con la importación ya hecha (reporte), no.
/// </summary>
public partial class ImportarClientesGen2Tests
{
    [Fact]
    public async Task Salir_con_un_archivo_analizado_sin_importar_pregunta()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante", [Fila("Instalaciones Vidal S.L.")]);
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "clientes-levante.xlsx", "levante");
        cut.WaitForAssertion(() => Campo<object?>(cut.Instance, "_planSimple").Should().NotBeNull());

        await cut.SalirYComprobarQuePreguntaAsync(Services.GetRequiredService<NavigationManager>());
    }

    [Fact]
    public async Task Salir_sin_archivo_analizado_no_pregunta()
    {
        var (cut, _) = Renderizar(new Escenario());
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        await cut.SalirYComprobarQueNoPreguntaAsync(Services.GetRequiredService<NavigationManager>(), "sin archivo no hay nada que perder");
    }

    [Fact]
    public async Task Tras_importar_salir_desde_el_reporte_no_pregunta()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante", [Fila("Instalaciones Vidal S.L.")]);
        var (cut, _) = Renderizar(escenario);
        await LlevarAConfirmarAsync(cut, "clientes-levante.xlsx", "levante");
        await MarcarRevisado(cut);
        await Pulsar(cut, "Importar ahora");
        await BotonDelDialogo(cut, "Sí, importar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => TituloDeLaSeccion(cut).Should().Be("Reporte"));

        await cut.SalirYComprobarQueNoPreguntaAsync(Services.GetRequiredService<NavigationManager>(),
            "el plan ya está importado: los enlaces del reporte salen sin aviso");
    }
}
