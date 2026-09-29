using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1): la edición en línea de Información del panel de Vehículo pinta su propio
/// aviso de cambios sin guardar. Vive en el panel, no en la pestaña: cambiar de pestaña no
/// la pierde y no pregunta.
/// </summary>
public partial class Vehiculo360Gen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<CaeManager.Web.Features.Vehiculos.Components.VehiculoWorkspacePanel>> EditarInformacionAsync()
    {
        var id = Guid.NewGuid();
        var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);
        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        return cut;
    }

    [Fact]
    public async Task Aviso_la_informacion_editada_pregunta_al_salir()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Nombre").InputAsync(new ChangeEventArgs { Value = "Furgoneta nueva" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_abrir_la_edicion_sin_tocar_nada_no_pregunta()
    {
        var cut = await EditarInformacionAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "los valores de partida no son un cambio de quien edita");
    }

    [Fact]
    public async Task Aviso_salir_y_descartar_cierra_la_edicion()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Nombre").InputAsync(new ChangeEventArgs { Value = "Furgoneta nueva" });
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);

        await cut.PulsarEnElAvisoAsync("Salir y descartar");

        Navegacion.Uri.Should().EndWith(AvisoCambiosSinGuardarPrueba.DestinoFuera);
        cut.WaitForAssertion(() => cut.FindAll("input").Should().BeEmpty("la edición se descartó"));
    }
}
