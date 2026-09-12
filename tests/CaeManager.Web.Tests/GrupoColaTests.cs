using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// GrupoColaDto agrupa por Cliente/Empresa, nunca por Centro (ver
/// ObtenerBandejaAgrupadaQueryHandler.ClaveGrupo) — un grupo real con
/// muchos trabajadores puede pertenecer a Centros distintos. El truncado
/// (decisión de producto 2026-08-17) corta por TRABAJADOR y solo enlaza a
/// un Centro concreto cuando todos los ocultos comparten el mismo; si no,
/// cae a un enlace genérico a Mi trabajo.
/// </summary>
public class GrupoColaTests : BunitContext
{
    public GrupoColaTests()
    {
        Services.AddSingleton<ContextWorkspaceService>();
        Services.AddSingleton<ToastService>();
        // TextoFechaCopiable (dentro de PanelResolverItem) importa clipboard.js
        // en OnAfterRenderAsync — no hay nada que copiar en este test, así que
        // basta con dejar pasar la importación en vez de mockear el módulo entero.
        JSInterop.SetupModule("./js/clipboard.js");
    }

    private static ItemBandejaDto Item(int n, Guid centroId) => new(
        Id: $"item-{n}", Tipo: TipoItemBandeja.RequisitoPendiente, Titulo: $"PSS firmado — Trabajador {n}",
        Subtitulo: "Centro X", Fecha: null, TrabajadorId: Guid.NewGuid(), CentroId: centroId,
        DocumentoId: null, TipoDocumentoId: Guid.NewGuid(), RequisitoId: null,
        TrabajadorNombre: $"Trabajador {n}");

    [Fact]
    public void Trunca_a_partir_del_sexto_trabajador_con_enlace_al_centro_unico()
    {
        var centroId = Guid.NewGuid();
        var items = Enumerable.Range(1, 7).Select(n => Item(n, centroId)).ToList();
        var grupo = new GrupoColaDto("cliente-1", "Cliente X", true, items);

        var cut = Render<GrupoCola>(p => p.Add(c => c.Grupo, grupo).Add(c => c.ExpandidaPorDefecto, true));

        cut.FindAll(".grupo-cola-subcabecera-trabajador").Should().HaveCount(5);
        var enlace = cut.Find(".grupo-cola-truncado a");
        enlace.TextContent.Should().Contain("Ver 2 trabajadores más en este Centro");
        enlace.GetAttribute("href").Should().Be($"/centros/{centroId}");
    }

    [Fact]
    public void Sin_centro_comun_entre_los_ocultos_enlaza_a_mi_trabajo()
    {
        // Cada Item recibe su propio Guid.NewGuid() — los dos ocultos (6º y 7º)
        // acaban con Centros distintos entre sí, sin necesidad de forzarlo.
        var items = Enumerable.Range(1, 7).Select(n => Item(n, Guid.NewGuid())).ToList();
        var grupo = new GrupoColaDto("cliente-1", "Cliente X", true, items);

        var cut = Render<GrupoCola>(p => p.Add(c => c.Grupo, grupo).Add(c => c.ExpandidaPorDefecto, true));

        var enlace = cut.Find(".grupo-cola-truncado a");
        enlace.TextContent.Should().Contain("Ver 2 trabajadores más en Mi trabajo");
        enlace.GetAttribute("href").Should().Be("/bandeja");
    }

    [Fact]
    public void No_trunca_con_cinco_trabajadores_o_menos()
    {
        var items = Enumerable.Range(1, 5).Select(n => Item(n, Guid.NewGuid())).ToList();
        var grupo = new GrupoColaDto("cliente-1", "Cliente X", true, items);

        var cut = Render<GrupoCola>(p => p.Add(c => c.Grupo, grupo).Add(c => c.ExpandidaPorDefecto, true));

        cut.FindAll(".grupo-cola-subcabecera-trabajador").Should().HaveCount(5);
        cut.FindAll(".grupo-cola-truncado").Should().BeEmpty();
    }

    /// <summary>
    /// U-1 (plan nocturno 2026-09-02): PaginarPorGrupo (opt-in, /bandeja lo
    /// activa) sustituye el truncado + "ver más" por un PaginadorSimple in
    /// situ — sin este parámetro (el caso de Inicio, no probado aquí de
    /// nuevo) el comportamiento de arriba no cambia.
    /// </summary>
    [Fact]
    public void PaginarPorGrupo_sustituye_el_truncado_por_un_paginador_y_avanza_la_pagina()
    {
        var items = Enumerable.Range(1, 7).Select(n => Item(n, Guid.NewGuid())).ToList();
        var grupo = new GrupoColaDto("cliente-1", "Cliente X", true, items);

        var cut = Render<GrupoCola>(p => p
            .Add(c => c.Grupo, grupo)
            .Add(c => c.ExpandidaPorDefecto, true)
            .Add(c => c.PaginarPorGrupo, true));

        cut.FindAll(".grupo-cola-truncado").Should().BeEmpty("con PaginarPorGrupo no se usa el enlace \"ver más\"");
        cut.FindAll(".grupo-cola-subcabecera-trabajador").Should().HaveCount(5, "MaximoTrabajadoresVisibles sigue siendo el tamaño de página");
        cut.Find(".paginador-simple").TextContent.Should().Contain("Página 1 de 2");

        var siguiente = cut.FindAll("button").Single(b => b.TextContent.Contains("Siguiente"));
        siguiente.Click();

        cut.FindAll(".grupo-cola-subcabecera-trabajador").Should().HaveCount(2, "la segunda página solo tiene los 2 trabajadores restantes");
    }

    /// <summary>
    /// Defecto real (leyendo código, 2026-09-12): j/k en Bandeja.razor.cs
    /// recorren TODOS los ItemsFiltrados, ajenos a que PaginarPorGrupo solo
    /// pinta los 5 primeros trabajadores de cada Empresa — sin el salto de
    /// página, IdEnfocado puede apuntar a una fila que esta tarjeta no ha
    /// pintado nunca, y el gestor CAE no ve dónde está el foco.
    /// </summary>
    [Fact]
    public void IdEnfocado_fuera_de_la_pagina_visible_hace_saltar_a_la_pagina_que_lo_contiene()
    {
        var items = Enumerable.Range(1, 7).Select(n => Item(n, Guid.NewGuid())).ToList();
        var grupo = new GrupoColaDto("cliente-1", "Cliente X", true, items);

        var cut = Render<GrupoCola>(p => p
            .Add(c => c.Grupo, grupo)
            .Add(c => c.ExpandidaPorDefecto, true)
            .Add(c => c.PaginarPorGrupo, true));

        cut.FindAll(".panel-resolver-item-enfocado").Should().BeEmpty("todavía no hay foco");

        // item-7 es el 7º trabajador (uno por item, ver Item()) — en la
        // página 2, fuera de los 5 que la página 1 pinta.
        cut.Render(p => p.Add(c => c.IdEnfocado, "item-7"));

        cut.Find(".paginador-simple").TextContent.Should().Contain("Página 2 de 2",
            "sin el salto, item-7 se queda en una página que PaginarPorGrupo nunca pinta");
        cut.FindAll(".panel-resolver-item-enfocado").Should().HaveCount(1);
        cut.FindAll(".grupo-cola-subcabecera-trabajador").Should().Contain(e => e.TextContent == "Trabajador 7");
    }

    /// <summary>
    /// El salto solo ocurre cuando IdEnfocado CAMBIA — si el gestor CAE pagina
    /// a mano después (sin volver a tocar j/k), ese clic no se revierte solo
    /// en el siguiente render porque IdEnfocado sigue siendo el mismo.
    /// </summary>
    [Fact]
    public void Paginar_a_mano_tras_el_salto_no_se_revierte_sin_un_nuevo_cambio_de_foco()
    {
        var items = Enumerable.Range(1, 7).Select(n => Item(n, Guid.NewGuid())).ToList();
        var grupo = new GrupoColaDto("cliente-1", "Cliente X", true, items);

        var cut = Render<GrupoCola>(p => p
            .Add(c => c.Grupo, grupo)
            .Add(c => c.ExpandidaPorDefecto, true)
            .Add(c => c.PaginarPorGrupo, true)
            .Add(c => c.IdEnfocado, "item-7"));

        cut.Find(".paginador-simple").TextContent.Should().Contain("Página 2 de 2");

        var anterior = cut.FindAll("button").Single(b => b.TextContent.Contains("Anterior"));
        anterior.Click();
        cut.Find(".paginador-simple").TextContent.Should().Contain("Página 1 de 2");

        cut.Render();

        cut.Find(".paginador-simple").TextContent.Should().Contain("Página 1 de 2",
            "IdEnfocado no cambió desde el salto — un render de más no debe deshacer el clic manual");
    }
}
