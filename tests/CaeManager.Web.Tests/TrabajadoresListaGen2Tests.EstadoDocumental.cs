using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Listado de Trabajadores, «propuesta completa»: el motivo bajo la pastilla de «Estado documental» con su
/// desglose corregible, la columna «Registrados vigentes» y el filtro «Centro».
///
/// <para>
/// Lo que estos tests NO observan: que el clic en una incidencia no abra además la vista rápida de la fila.
/// Ese clic de fila lo atiende <c>atajos-lista.js</c>, que bUnit no ejecuta; aquí solo se fija que el botón
/// vive dentro de <c>.ventana-contexto-panel</c>, que es lo que ese guion excluye.
/// </para>
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private static IncidenciaDocumentalDto IncidenciaDe(string tipo, EstadoDocumento estado, int? diasHastaVencer = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tipo, estado,
            diasHastaVencer is { } dias ? DiaDeNegocio.Hoy().AddDays(dias) : null);

    private static Fila ConDesglose(Fila fila, int registrados, int vigentes, params IncidenciaDocumentalDto[] incidencias) =>
        fila with
        {
            Dto = fila.Dto with { Incidencias = incidencias, DocumentosRegistrados = registrados, DocumentosVigentes = vigentes }
        };

    private static AngleSharp.Dom.IElement CeldaDeEstado(IRenderedComponent<Trabajadores> cut, int fila = 0) =>
        FilasDeDatos(cut)[fila].QuerySelector("td.col-estado")!;

    /// <summary>Las consultas que hace el formulario de corrección al montarse, que el doble de la lista no conoce.</summary>
    private void ConFormularioDeCorreccion(MediatorFalso mediador)
    {
        this.ConServiciosDelFormularioDeDocumento();
        mediador.Retener = peticion => peticion switch
        {
            ObtenerDocumentoPorIdQuery => Task.FromResult<object>(null!),
            ObtenerTrabajadoresParaSelectorQuery => Task.FromResult<object>((IReadOnlyList<TrabajadorSelectorDto>)[]),
            ObtenerVehiculosParaSelectorQuery => Task.FromResult<object>((IReadOnlyList<VehiculoSelectorDto>)[]),
            ObtenerTiposDocumentoQuery => Task.FromResult<object>((IReadOnlyList<TipoDocumentoListaDto>)[]),
            _ => null
        };
    }

    // --- Estado documental: pastilla y motivo --------------------------------------------------

    [Fact]
    public void Con_una_incidencia_el_motivo_bajo_la_pastilla_dice_el_documento_que_la_causa()
    {
        var javier = ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Urgente), 6, 6,
            IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5));
        var cut = Renderizar(new MediatorFalso { Almacen = { javier } });

        var celda = CeldaDeEstado(cut);
        celda.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Por vencer", "la pastilla se pinta siempre");
        celda.QuerySelector(".estado-fila-motivo .motivo-incidencias-texto")!.TextContent.Trim()
            .Should().Be("Formación Art. 19 · Caduca en 5 días");
    }

    [Fact]
    public void Con_varias_incidencias_el_motivo_las_cuenta_por_clase_y_el_desglose_lleva_una_linea_por_documento()
    {
        var javier = ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 10, 7,
            IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Contrato", EstadoDocumento.Vencido, -3),
            IncidenciaDe("Formación Art. 19", EstadoDocumento.Proximo, 25),
            IncidenciaDe("Entrega de EPI", EstadoDocumento.SinConfirmar));
        var cut = Renderizar(new MediatorFalso { Almacen = { javier } });

        var celda = CeldaDeEstado(cut);
        celda.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido");
        celda.QuerySelector(".motivo-incidencias-texto")!.TextContent.Trim()
            .Should().Be("2 vencidos · 1 por vencer · 1 sin confirmar");
        celda.QuerySelectorAll("button.ventana-contexto-elemento .motivo-incidencias-tipo").Select(e => e.TextContent.Trim())
            .Should().Equal("Aptitud médica", "Contrato", "Formación Art. 19", "Entrega de EPI");
    }

    [Fact]
    public void Las_filas_con_incidencias_no_comparten_claves_aunque_haya_varias_en_la_pagina()
    {
        // Dos filas con desglose a la vez: un @key repetido entre hermanos tumba el circuito («wrong pooled»).
        var ana = ConDesglose(Trabajador("Ana", "Alonso", estado: EstadoDocumento.Vencido), 2, 0,
            IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -1), IncidenciaDe("Contrato", EstadoDocumento.Vencido, -2));
        var bea = ConDesglose(Trabajador("Bea", "Moreno", estado: EstadoDocumento.Vencido), 2, 0,
            IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -1), IncidenciaDe("Contrato", EstadoDocumento.Vencido, -2));

        var cut = Renderizar(new MediatorFalso { Almacen = { ana, bea } });

        FilasDeDatos(cut).Should().HaveCount(2);
        cut.FindAll("td.col-estado button.ventana-contexto-elemento").Should().HaveCount(4);
    }

    [Fact]
    public void Quien_solo_consulta_ve_el_desglose_sin_botones()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var javier = ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 3, 1,
            IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Entrega de EPI", EstadoDocumento.SinConfirmar));
        var cut = Renderizar(new MediatorFalso { Almacen = { javier } });

        var celda = CeldaDeEstado(cut);
        celda.QuerySelector(".motivo-incidencias-texto")!.TextContent.Trim().Should().Be("1 vencido · 1 sin confirmar",
            "control positivo: el motivo se ve igual");
        celda.QuerySelectorAll("button").Should().BeEmpty("no se ofrece un formulario que el comando va a denegar");
        celda.QuerySelectorAll(".ventana-linea").Should().HaveCount(2, "control positivo: el desglose se sigue viendo");
    }

    [Fact]
    public async Task Pulsar_una_incidencia_abre_la_correccion_de_ese_documento_sin_recargar_la_lista()
    {
        var vencida = IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -12);
        var urgente = IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5);
        var javier = ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 4, 3, vencida, urgente);
        var mediador = new MediatorFalso { Almacen = { javier } };
        ConFormularioDeCorreccion(mediador);
        var cut = Renderizar(mediador);
        var consultasDeListaAntes = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();
        var boton = CeldaDeEstado(cut).QuerySelectorAll("button.ventana-contexto-elemento")[1];
        boton.Closest(".ventana-contexto-panel").Should().NotBeNull(
            "es lo que «pulsarFila» de atajos-lista.js excluye para no abrir además la vista rápida");

        await boton.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(urgente.DocumentoId, "se corrige el documento de la línea pulsada, no el primero");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(consultasDeListaAntes, "pulsar no recarga");
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull("ni abre la vista rápida de la fila");
    }

    [Fact]
    public async Task Tras_corregir_se_vuelve_a_pedir_la_misma_pagina_y_la_seleccion_se_conserva()
    {
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno") } };
        var cut = Renderizar(mediador, "trabajadores?estado=Vigente");
        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        var antes = UltimaConsulta(mediador);
        var consultasDeListaAntes = mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Count();

        var correccion = cut.FindComponent<CorreccionIncidenciaDocumental>();
        await cut.InvokeAsync(() => correccion.Instance.OnCorregida.InvokeAsync());

        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().HaveCount(consultasDeListaAntes + 1,
            "el estado y el desglose de la fila cambian al corregir: la página se relee en sitio");
        UltimaConsulta(mediador).Should().Be(antes, "con los mismos filtros, orden y página");
        cut.WaitForAssertion(() =>
            cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").HasAttribute("checked").Should().BeTrue(
                "corregir un documento no es cambiar de lista: lo marcado sigue marcado"));
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");
    }

    [Fact]
    public async Task Una_recarga_que_no_viene_de_corregir_sigue_soltando_la_seleccion()
    {
        // Control del anterior: conservar la selección es solo de la recarga tras corregir.
        var mediador = new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso"), Trabajador("Ana", "Moreno") } };
        var cut = Renderizar(mediador);
        await AlternarSeleccionMultiple(cut);
        await cut.Find("tbody input[aria-label='Seleccionar a Bea Alonso']").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");

        await ElegirEnLaPastilla(cut, "Empresa", "Montajes Ebro S.L.");

        cut.WaitForAssertion(() => cut.FindAll(".barra-acciones-lote").Should().BeEmpty());
    }

    /// <summary>
    /// La fila que se refresca en sitio tras guardar en la vista rápida (consulta por id, ver
    /// TrabajadoresListaGen2Tests.FilaSeRefresca.cs) pide también el desglose y pinta el que vuelve: no pierde
    /// el motivo ni «Registrados vigentes».
    /// </summary>
    [Fact]
    public async Task La_fila_refrescada_tras_guardar_en_la_vista_rapida_conserva_el_motivo_y_los_registrados_vigentes()
    {
        var javier = ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 4, 2,
            IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Entrega de EPI", EstadoDocumento.SinConfirmar));
        var mediador = new MediatorFalso { Almacen = { javier, Trabajador("Zoe", "Zamora") } };
        var cut = Renderizar(mediador);
        TextoDelNombre(FilasDeDatos(cut)[0]).Should().Be("Javier Salas Moreno", "punto de partida: es la primera fila");
        CeldaDeEstado(cut).QuerySelector(".motivo-incidencias-texto")!.TextContent.Trim()
            .Should().Be("1 vencido · 1 sin confirmar", "punto de partida");

        // Lo que deja el guardado: el vencido se renovó y queda solo el sin confirmar.
        var indice = mediador.Almacen.FindIndex(f => f.Dto.Id == javier.Dto.Id);
        mediador.Almacen[indice] = ConDesglose(
            javier with { Dto = javier.Dto with { EstadoDocumental = EstadoDocumento.SinConfirmar } }, 4, 3,
            IncidenciaDe("Entrega de EPI", EstadoDocumento.SinConfirmar));
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, javier.Dto.Id);

        cut.WaitForAssertion(() => CeldaDeEstado(cut).QuerySelector(".motivo-incidencias-texto")!.TextContent.Trim()
            .Should().Be("Entrega de EPI", "la fila refrescada pinta el motivo que vuelve"));
        CeldaDeEstado(cut).QuerySelector(".badge")!.TextContent.Trim().Should().Be("Sin confirmar");
        FilasDeDatos(cut)[0].QuerySelector(".celda-registrados-vigentes")!.TextContent.Trim().Should().Be("3/4");
        UltimaConsulta(mediador).TrabajadorId.Should().Be(javier.Dto.Id, "control: fue la consulta por id, no una recarga de página");
        UltimaConsulta(mediador).ConDesgloseDocumental.Should().BeTrue("sin pedirlo, la fila refrescada volvería sin motivo ni fracción");
    }

    // --- Registrados vigentes -----------------------------------------------------------------

    [Fact]
    public void Registrados_vigentes_dice_cuantos_de_los_registrados_estan_al_dia_y_una_raya_si_no_hay_ninguno()
    {
        var conDocumentos = ConDesglose(Trabajador("Ana", "Alonso", estado: EstadoDocumento.Vencido), 10, 8,
            IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -1), IncidenciaDe("Contrato", EstadoDocumento.Vencido, -2));
        var sinDocumentos = Trabajador("Bea", "Moreno", estado: null);
        var cut = Renderizar(new MediatorFalso { Almacen = { conDocumentos, sinDocumentos } });

        var celdas = FilasDeDatos(cut).Select(tr => tr.QuerySelector("td.col-registrados-vigentes .celda-registrados-vigentes")!).ToList();

        celdas.Select(c => c.TextContent.Trim()).Should().Equal("8/10", "—");
        celdas[0].GetAttribute("title").Should().Be("8 de 10 documentos registrados al día");
    }

    // --- Filtro «Centro» ----------------------------------------------------------------------

    private static readonly Guid CentroNorte = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid CentroSur = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static MediatorFalso ConCentros(MediatorFalso mediador)
    {
        mediador.Centros.Add(new CentroSelectorDto(CentroNorte, "Planta Norte", "Refrielectric S.A.", "Montajes Ebro S.L."));
        mediador.Centros.Add(new CentroSelectorDto(CentroSur, "Planta Sur", "Dexter Industrial S.A.", "Montajes Ebro S.L."));
        return mediador;
    }

    [Fact]
    public async Task La_pastilla_Centro_ofrece_los_centros_visibles_con_su_cliente_y_al_elegir_uno_filtra_y_lo_lleva_a_la_URL()
    {
        var mediador = ConCentros(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } });
        var cut = Renderizar(mediador);
        UltimaConsulta(mediador).CentroId.Should().BeNull("punto de partida: sin filtro");

        (await OpcionesDeLaPastilla(cut, "Centro")).Should().Equal(
            "Todos", "Planta Norte (Refrielectric S.A.)", "Planta Sur (Dexter Industrial S.A.)");
        await cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]")
            .Single(i => i.TextContent.Trim() == "Planta Sur (Dexter Industrial S.A.)").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).CentroId.Should().Be(CentroSur);
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"centro={CentroSur}");
        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().Equal("Planta Sur"));
    }

    [Fact]
    public void El_centro_de_la_URL_llega_a_la_consulta_y_se_ve_como_chip()
    {
        var mediador = ConCentros(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } });

        var cut = Renderizar(mediador, $"trabajadores?centro={CentroNorte}");

        UltimaConsulta(mediador).CentroId.Should().Be(CentroNorte);
        TextosDeLosChips(cut).Should().Equal("Planta Norte");
    }

    [Fact]
    public void Un_centro_de_la_URL_que_no_es_un_identificador_no_filtra()
    {
        var mediador = ConCentros(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } });

        var cut = Renderizar(mediador, "trabajadores?centro=no-es-un-guid");

        UltimaConsulta(mediador).CentroId.Should().BeNull();
        TextosDeLosChips(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task Quitar_el_chip_del_centro_lo_quita_de_la_consulta_y_de_la_URL()
    {
        var mediador = ConCentros(new MediatorFalso { Almacen = { Trabajador("Bea", "Alonso") } });
        var cut = Renderizar(mediador, $"trabajadores?centro={CentroNorte}");
        UltimaConsulta(mediador).CentroId.Should().Be(CentroNorte, "control: el centro de la URL filtra");

        await cut.Find(".barra-filtros-pastillas .chip-filtro button").ClickAsync(new MouseEventArgs());

        UltimaConsulta(mediador).CentroId.Should().BeNull();
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("centro=");
        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().BeEmpty());
    }

    [Fact]
    public async Task Limpiar_todo_quita_tambien_el_centro_de_la_URL()
    {
        var mediador = ConCentros(new MediatorFalso { Almacen = { Trabajador("Javier", "Salas Moreno") } });
        var cut = Renderizar(mediador, $"trabajadores?q=Salas&centro={CentroNorte}");
        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().Equal("Búsqueda: \"Salas\"", "Planta Norte"));
        UltimaConsulta(mediador).CentroId.Should().Be(CentroNorte, "control: el centro de la URL filtra");

        await cut.Find(".barra-filtros-pastillas .limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("centro=").And.NotContain("q=");
        UltimaConsulta(mediador).CentroId.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().BeEmpty());
    }
}
