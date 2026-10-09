using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila de /trabajadores se refresca en sitio tras guardar en la vista rápida (panel del
/// Context Workspace, que vive en MainLayout y guarda sin pasar por la página). El panel avisa
/// por <see cref="ContextWorkspaceService.OnEntidadGuardada"/> y la página vuelve a pedir SOLO
/// esa fila, con el filtro por id de la consulta de lista. Aquí no se monta el panel: el aviso se
/// emite a mano sobre el servicio que la página tiene inyectado. Que el panel lo emite al guardar
/// lo prueba <see cref="TrabajadorWorkspacePanelAvisoDeGuardadoTests"/>.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    /// <summary>
    /// El aviso tal como lo emite el panel. La página lo atiende con un <c>InvokeAsync</c> que no
    /// se espera: se emite dentro del despachador y lo que pinte se afirma con WaitForAssertion.
    /// </summary>
    private Task AvisarGuardadoAsync(IRenderedComponent<Trabajadores> cut, EntidadWorkspace tipo, Guid id) =>
        cut.InvokeAsync(() => Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(tipo, id));

    private static List<string> NombresDeLasFilas(IRenderedComponent<Trabajadores> cut) =>
        FilasDeDatos(cut).Select(TextoDelNombre).ToList();

    /// <summary>Cambia lo guardado en el doble, como haría el comando de edición del panel.</summary>
    private static void Renombrar(MediatorFalso mediador, Guid id, string? nombre = null, string? apellidos = null)
    {
        var indice = mediador.Almacen.FindIndex(f => f.Dto.Id == id);
        var fila = mediador.Almacen[indice];
        mediador.Almacen[indice] = fila with
        {
            Dto = fila.Dto with { Nombre = nombre ?? fila.Dto.Nombre, Apellidos = apellidos ?? fila.Dto.Apellidos }
        };
    }

    /// <summary>
    /// El apellido nuevo mandaría la fila al final con el orden por apellidos: que siga la
    /// primera es lo que distingue «sustituir en sitio» de «recargar la página».
    /// </summary>
    [Fact]
    public async Task El_aviso_de_guardado_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var bea = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { bea, Trabajador("Ana", "Moreno") } };
        var cut = Renderizar(mediador);
        NombresDeLasFilas(cut).Should().Equal(["Bea Alonso", "Ana Moreno"], "punto de partida");
        var consultasAntes = ConsultasDeLista(mediador);

        Renombrar(mediador, bea.Dto.Id, nombre: "Beatriz", apellidos: "Zamora");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, bea.Dto.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Beatriz Zamora", "Ana Moreno"],
            "la fila enseña el dato nuevo y no cambia de sitio"));
        UltimaConsulta(mediador).TrabajadorId.Should().Be(bea.Dto.Id, "se pide solo esa fila");
        UltimaConsulta(mediador).Busqueda.Should().BeNull();
        UltimaConsulta(mediador).ConRecuentosPorEstado.Should().BeTrue(
            "misma pregunta de estado que la carga de página: sin ella, quien no tiene documentos pasaría de «Sin incidencias» a «Sin documentos»");
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "la consulta por id y ninguna de página detrás");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count(q => q.TrabajadorId is null).Should().Be(consultasAntes,
            "ninguna consulta de página nueva");
    }

    [Fact]
    public async Task El_aviso_de_guardado_conserva_la_pagina_la_seleccion_multiple_y_la_fila_enfocada()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Trabajador("Nombre", $"Apellido {i:00}"));
        var editado = mediador.Almacen.Single(f => f.Dto.Apellidos == "Apellido 23").Dto.Id;
        var cut = Renderizar(mediador);
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(5).And.StartWith("Nombre Apellido 21"));
        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Nombre Apellido 22']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("tbody input[aria-label='Seleccionar a Nombre Apellido 23']").ChangeAsync(new ChangeEventArgs { Value = true });
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        ClasesDeLasFilas(cut)[0].Should().Contain("fila-enfocada", "punto de partida: la j enfocó la primera fila");
        var consultasAntes = ConsultasDeLista(mediador);

        Renombrar(mediador, editado, nombre: "Renombrado");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, editado);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(
            "Nombre Apellido 21", "Nombre Apellido 22", "Renombrado Apellido 23", "Nombre Apellido 24", "Nombre Apellido 25"));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 trabajador(es)");
        cut.Find("tbody input[aria-label='Seleccionar a Nombre Apellido 22']").HasAttribute("checked").Should().BeTrue(
            "la selección de otra fila sigue marcada");
        cut.Find("tbody input[aria-label='Seleccionar a Renombrado Apellido 23']").HasAttribute("checked").Should().BeTrue(
            "la fila sustituida sigue seleccionada");
        cut.FindAll("tbody input[type=checkbox]").Count(c => c.HasAttribute("checked")).Should().Be(2);
        ClasesDeLasFilas(cut).Select(c => c.Contains("fila-enfocada")).Should().Equal(true, false, false, false, false);
        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "una carga de página habría limpiado selección y foco");
        UltimaConsulta(mediador).TrabajadorId.Should().Be(editado);
    }

    /// <summary>
    /// La búsqueda activa es «Alonso» y el guardado cambia el apellido: la fila ya no casa con el
    /// filtro, pero permanece hasta la siguiente carga, y el filtro no se toca.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_el_cambio_la_saque_del_filtro_activo()
    {
        var bea = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { bea, Trabajador("Ana", "Moreno") } };
        var cut = Renderizar(mediador, "trabajadores?q=Alonso");
        NombresDeLasFilas(cut).Should().Equal(["Bea Alonso"], "punto de partida: el filtro deja una fila");

        Renombrar(mediador, bea.Dto.Id, apellidos: "Zamora");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, bea.Dto.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Bea Zamora"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Alonso");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Last(q => q.TrabajadorId is null).Busqueda.Should().Be("Alonso",
            "la última consulta de página sigue siendo la del filtro");
    }

    /// <summary>
    /// Tres avisos que no son de esta página: otro tipo de entidad con un id que sí está a la
    /// vista, un Trabajador que no existe y uno que está en la página 1 mientras se mira la 2.
    /// El aviso se atiende en línea dentro del despachador y el doble responde en síncrono, así
    /// que al volver de cada uno ya no queda nada pendiente; el control positivo del final
    /// demuestra que el contador ve la consulta cuando sí la hay.
    /// </summary>
    [Fact]
    public async Task Un_aviso_de_otra_entidad_o_de_un_id_fuera_de_la_pagina_no_consulta_nada()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Trabajador("Nombre", $"Apellido {i:00}"));
        var dePagina1 = mediador.Almacen.Single(f => f.Dto.Apellidos == "Apellido 01").Dto.Id;
        var dePagina2 = mediador.Almacen.Single(f => f.Dto.Apellidos == "Apellido 21").Dto.Id;
        var cut = Renderizar(mediador);
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(5).And.StartWith("Nombre Apellido 21"));
        var enviadasAntes = mediador.Enviadas.Count;
        Renombrar(mediador, dePagina1, nombre: "Renombrado");
        Renombrar(mediador, dePagina2, nombre: "Renombrado");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Vehiculo, dePagina2);
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, Guid.NewGuid());
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, dePagina1);

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "ninguno de los tres avisos es de una fila de esta página");
        NombresDeLasFilas(cut).Should().StartWith("Nombre Apellido 21", "sin consulta no hay dato nuevo que pintar");

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, dePagina2);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().StartWith("Renombrado Apellido 21"));
        mediador.Enviadas.Should().HaveCount(enviadasAntes + 1, "control positivo: el aviso de una fila a la vista sí consulta");
    }

    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_no_se_ensena_ningun_error_y_la_fila_se_queda()
    {
        var bea = Trabajador("Bea", "Alonso");
        var mediador = new MediatorFalso { Almacen = { bea, Trabajador("Ana", "Moreno") } };
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);
        mediador.Retener = peticion => peticion is ObtenerTrabajadoresQuery { TrabajadorId: not null }
            ? Task.FromException<object>(new InvalidOperationException("Fallo simulado de la relectura."))
            : null;

        Renombrar(mediador, bea.Dto.Id, nombre: "Beatriz");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, bea.Dto.Id);

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1, "control positivo: la relectura se intentó");
        NombresDeLasFilas(cut).Should().Equal(["Bea Alonso", "Ana Moreno"], "la fila conserva el dato anterior");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el guardado ya es firme: no hay error que enseñar");
        cut.Markup.Should().NotContain("Fallo simulado");

        // La página sigue viva: el siguiente aviso, ya sin fallo, sí sustituye la fila.
        mediador.Retener = null;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, bea.Dto.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Beatriz Alonso", "Ana Moreno"));
    }
}
