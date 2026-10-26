using System.Reflection;
using Bunit;
using CaeManager.Application.Importacion.Queries;
using FluentAssertions;
using PaginaImportacion = CaeManager.Web.Features.Importacion.Pages.Importacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// CWE-602 («validación solo en el cliente»): los manejadores que avanzan el asistente comprueban en el servidor que el paso de
/// origen y el plan son los que el botón da por supuestos. Un clic que llegara de otro paso (un botón que el navegador conserva
/// o reactiva) no mueve el asistente ni el paso máximo alcanzado.
/// </summary>
public partial class ImportarClientesGen2Tests
{
    private static void InvocarSinResultado(PaginaImportacion instancia, string nombre) =>
        (typeof(PaginaImportacion).GetMethod(nombre, BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException($"Importacion ya no tiene el método {nombre}: el test ha dejado de observar lo que dice."))
        .Invoke(instancia, null);

    private async Task<IRenderedComponent<PaginaImportacion>> LlegarAlPasoAsync(int paso)
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante", [Fila("Instalaciones Vidal S.L.")]);
        var (cut, _) = Renderizar(escenario);
        if (paso >= 2) await Pulsar(cut, "Continuar con Plantilla de Clientes");
        if (paso >= 3)
        {
            await Subir(cut, "clientes-levante.xlsx", "levante");
            cut.WaitForAssertion(() => Campo<object?>(cut.Instance, "_planSimple").Should().NotBeNull());
            await Pulsar(cut, "Ver plan de importación");
        }

        Campo<int>(cut.Instance, "_step").Should().Be(paso, "el arnés tiene que haber llegado al paso de partida");
        return cut;
    }

    [Fact]
    public async Task ContinuarAAnalizar_forzado_desde_la_revision_no_hace_retroceder_el_asistente()
    {
        var cut = await LlegarAlPasoAsync(3);

        InvocarSinResultado(cut.Instance, "ContinuarAAnalizar");

        Campo<int>(cut.Instance, "_step").Should().Be(3, "el botón solo existe en el paso 1: desde otro paso el manejador no hace nada");
    }

    [Fact]
    public async Task ContinuarAConfirmar_forzado_sin_plan_no_salta_al_paso_de_confirmar()
    {
        var cut = await LlegarAlPasoAsync(2);
        Campo<object?>(cut.Instance, "_planSimple").Should().BeNull();

        InvocarSinResultado(cut.Instance, "ContinuarAConfirmar");

        Campo<int>(cut.Instance, "_step").Should().Be(2);
        Campo<int>(cut.Instance, "_pasoMaximoAlcanzado").Should().Be(2, "el paso 4 no se gana sin haber pasado por la revisión de un plan");
    }

    [Fact]
    public async Task ContinuarAConfirmar_forzado_desde_el_paso_del_archivo_aunque_haya_plan_no_se_salta_la_revision()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante", [Fila("Instalaciones Vidal S.L.")]);
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "clientes-levante.xlsx", "levante");
        cut.WaitForAssertion(() => Campo<object?>(cut.Instance, "_planSimple").Should().NotBeNull());

        InvocarSinResultado(cut.Instance, "ContinuarAConfirmar");

        Campo<int>(cut.Instance, "_step").Should().Be(2);
        Campo<int>(cut.Instance, "_pasoMaximoAlcanzado").Should().Be(2);
    }

    [Fact]
    public async Task ContinuarAConfirmar_desde_la_revision_con_plan_avanza()
    {
        var cut = await LlegarAlPasoAsync(3);

        InvocarSinResultado(cut.Instance, "ContinuarAConfirmar");

        Campo<int>(cut.Instance, "_step").Should().Be(4);
    }

    [Fact]
    public async Task ContinuarAAnalizar_desde_el_paso_1_avanza()
    {
        var cut = await LlegarAlPasoAsync(1);

        InvocarSinResultado(cut.Instance, "ContinuarAAnalizar");

        Campo<int>(cut.Instance, "_step").Should().Be(2);
    }
}
