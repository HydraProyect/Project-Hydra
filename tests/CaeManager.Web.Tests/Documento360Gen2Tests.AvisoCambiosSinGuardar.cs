using Bunit;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1): la renovación en línea (fechas y comentarios) del panel de Documento pinta
/// su propio aviso de cambios sin guardar, fuera de las pestañas: su estado vive en el panel.
/// </summary>
public partial class Documento360Gen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<DocumentoWorkspacePanel>> RenovarAsync()
    {
        // La pregunta pinta sus textos con IStringLocalizer<TextosComunes>.
        Services.AddLocalization();
        var id = Guid.NewGuid();
        Registrar(new MediadorFalso()).Detalles[id] = Detalle(id);
        var cut = Renderizar(id);
        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        return cut;
    }

    private static AngleSharp.Dom.IElement Comentarios(IRenderedComponent<DocumentoWorkspacePanel> cut) =>
        cut.Find("#" + cut.FindAll("label").Single(l => l.TextContent.Trim() == "Comentarios").GetAttribute("for"));

    [Fact]
    public async Task Aviso_la_renovacion_a_medias_pregunta_al_salir()
    {
        var cut = await RenovarAsync();
        await Comentarios(cut).InputAsync(new ChangeEventArgs { Value = "Renovado en septiembre" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_abrir_la_renovacion_sin_tocar_nada_no_pregunta()
    {
        var cut = await RenovarAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "las fechas precargadas no son un cambio de quien edita");
    }

    [Fact]
    public async Task Aviso_cancelar_la_renovacion_no_pregunta()
    {
        var cut = await RenovarAsync();
        await Comentarios(cut).InputAsync(new ChangeEventArgs { Value = "Renovado en septiembre" });
        await Boton(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "«Cancelar» es una decisión explícita");
    }
}
