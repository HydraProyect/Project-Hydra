using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Listado de Clientes empresariales: el motivo bajo la pastilla de «Estado documental» abre una ventana de
/// contexto con las alertas de la fila (<see cref="ClienteListaDto.Incidencias"/>), una por línea, y quien
/// puede escribir las corrige ahí. Las alertas son de documentos de Trabajadores y de documentos que les
/// faltan en un Centro; por eso cada línea nombra el documento y el Trabajador, como en Centros.
///
/// <para>
/// Lo que estos tests NO observan: que el clic en una alerta no abra además la vista rápida de la fila (lo
/// atiende <c>atajos-lista.js</c>, que bUnit no ejecuta; aquí solo se fija que el botón vive dentro de
/// <c>.ventana-contexto-panel</c>, que es lo que ese guion excluye), ni qué alertas entrega la consulta, que
/// es de <c>DesgloseDocumentalDeClientesBajoAlcanceTests</c>.
/// </para>
/// </summary>
public partial class ClientesListaGen2Tests
{
    private static IncidenciaClienteDto AlertaDeDocumento(string tipo, string trabajador, EstadoDocumento estado, int diasHastaVencer)
    {
        var documentoId = Guid.NewGuid();
        return new IncidenciaClienteDto(
            $"incidencia-{documentoId}", estado, documentoId, Guid.NewGuid(), tipo, Guid.NewGuid(), trabajador,
            DiaDeNegocio.Hoy().AddDays(diasHastaVencer), CentroNombre: null);
    }

    private static IncidenciaClienteDto AlertaDeFaltante(string tipo, string trabajador, string centro)
    {
        var (tipoId, trabajadorId) = (Guid.NewGuid(), Guid.NewGuid());
        return new IncidenciaClienteDto(
            $"incidencia-{trabajadorId}-{tipoId}-{Guid.NewGuid()}", EstadoDocumento.Faltante, DocumentoId: null, tipoId, tipo,
            trabajadorId, trabajador, FechaVencimiento: null, centro);
    }

    /// <param name="totales">Las alertas del Cliente empresarial; por defecto, las que viajan en la fila.</param>
    private static ClienteListaDto ConIncidencias(ClienteListaDto fila, int? totales = null, params IncidenciaClienteDto[] incidencias) =>
        fila with { Incidencias = incidencias, IncidenciasTotales = totales ?? incidencias.Length };

    /// <summary>
    /// La fila con <paramref name="cuantas"/> alertas de un mismo estado, como la entrega la consulta: viajan
    /// las primeras hasta el tope y el total dice cuántas hay.
    /// </summary>
    private static ClienteListaDto ConAlertas(ClienteListaDto fila, EstadoDocumento estado, int cuantas) =>
        ConIncidencias(
            fila, cuantas,
            Enumerable.Range(1, Math.Min(cuantas, ObtenerClientesQuery.MaximoIncidenciasPorFila))
                .Select(n => AlertaDeDocumento($"Documento {n}", "Nora Vidal", estado, estado == EstadoDocumento.Vencido ? -n : n))
                .ToArray());

    private static IElement CeldaDeEstado(IRenderedComponent<Clientes> cut, string razonSocial) =>
        Fila(cut, razonSocial).QuerySelector("td.col-estado")!;

    private static string Fecha(IncidenciaClienteDto incidencia) =>
        incidencia.FechaVencimiento!.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);

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

    // --- Lo que se pide --------------------------------------------------------------------------

    [Fact]
    public void La_carga_de_pagina_pide_el_desglose_y_sin_el_el_motivo_seria_solo_texto()
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 1), null,
            AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12));
        var mediador = new MediatorFalso { Almacen = { alfa } };

        var cut = Renderizar(mediador);

        UltimaConsulta(mediador).ConDesgloseDocumental.Should().BeTrue("la ventana del motivo necesita las alertas de la fila");
        CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelectorAll(".ventana-contexto-panel").Should().ContainSingle();
        mediador.Filtrar(UltimaConsulta(mediador) with { ConDesgloseDocumental = false }).Elementos.Single().Incidencias
            .Should().BeEmpty("control del doble: como el handler, sin pedirlo no entrega incidencias");
    }

    [Fact]
    public async Task El_refresco_de_una_fila_tras_guardar_en_la_vista_rapida_pide_el_desglose_y_conserva_la_ventana()
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 1), null,
            AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12));
        var mediador = new MediatorFalso { Almacen = { alfa } };
        var cut = Renderizar(mediador);

        RenombrarEnElAlmacen(mediador, alfa.Id, "Alfa Renombrada S.L.");
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Cliente, alfa.Id);

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Alfa Renombrada S.L."));
        UltimaConsulta(mediador).Id.Should().Be(alfa.Id, "control positivo: la última consulta es la de la fila");
        UltimaConsulta(mediador).ConDesgloseDocumental.Should().BeTrue();
        CeldaDeEstado(cut, "Alfa Renombrada S.L.").QuerySelectorAll("button.ventana-contexto-elemento").Should().ContainSingle(
            "la fila sustituida no pierde su ventana de incidencias");
    }

    // --- Lo que se ve ----------------------------------------------------------------------------

    [Fact]
    public void El_motivo_abre_una_ventana_con_una_linea_por_alerta_que_nombra_el_documento_y_el_trabajador()
    {
        var vencida = AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12);
        var faltante = AlertaDeFaltante("Formación Art. 19", "Javier Salas", "Planta de Zaragoza");
        var proxima = AlertaDeDocumento("Contrato", "Nora Vidal", EstadoDocumento.Proximo, 25);
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 1), null, vencida, faltante, proxima);
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } });

        var celda = CeldaDeEstado(cut, "Alfa Montajes S.L.");
        celda.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido", "la pastilla se pinta siempre");
        celda.QuerySelector(".estado-fila-motivo .ventana-contexto-disparador .motivo-incidencias-cliente")!.TextContent.Trim()
            .Should().Be("1 documento", "el motivo sigue contando las alertas del peor estado");
        celda.QuerySelector(".ventana-contexto-disparador")!.GetAttribute("aria-label")
            .Should().Be("Vencido: 1 documento. 3 documentos con incidencia entre sus trabajadores");
        celda.QuerySelector(".ventana-contexto-titulo")!.TextContent.Trim()
            .Should().Be("3 documentos con incidencia entre sus trabajadores", "la ventana las lista todas, no solo las del peor estado");

        var lineas = celda.QuerySelectorAll("button.ventana-contexto-elemento");
        lineas.Select(l => l.QuerySelector(".incidencia-cliente-texto")!.TextContent.Trim()).Should().Equal(
            ["Aptitud médica — Nora Vidal", "Formación Art. 19 — Javier Salas", "Contrato — Nora Vidal"],
            "en el orden en que las entrega la consulta: la más grave primero");
        lineas.Select(l => l.QuerySelector(".badge")!.TextContent.Trim()).Should().Equal("Vencido", "Pendiente", "Por vencer");
        lineas.Select(l => l.QuerySelector(".ventana-contexto-elemento-secundario")!.TextContent.Trim()).Should().Equal(
            [Fecha(vencida), "Planta de Zaragoza", Fecha(proxima)],
            "la fecha de vencimiento o, si el documento falta, el Centro donde se pide");
        celda.QuerySelector(".ventana-contexto-pie")!.TextContent.Trim().Should().Be("Clic en una para corregirla aquí");
    }

    [Fact]
    public void Con_una_sola_alerta_el_titulo_de_la_ventana_va_en_singular()
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Urgente, cantidad: 1), null,
            AlertaDeDocumento("Contrato", "Nora Vidal", EstadoDocumento.Urgente, 5));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } });

        CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelector(".ventana-contexto-titulo")!.TextContent.Trim()
            .Should().Be("1 documento con incidencia entre sus trabajadores");
    }

    [Fact]
    public void Dos_faltantes_del_mismo_trabajador_y_tipo_en_centros_distintos_son_dos_lineas()
    {
        // Mismo Trabajador y mismo Tipo: solo el Centro distingue las claves. Con @key repetido, Blazor lanza.
        var (tipoId, trabajadorId) = (Guid.NewGuid(), Guid.NewGuid());
        IncidenciaClienteDto EnCentro(string centro) => new(
            $"incidencia-{trabajadorId}-{tipoId}-{Guid.NewGuid()}", EstadoDocumento.Faltante, null, tipoId, "Formación Art. 19",
            trabajadorId, "Javier Salas", null, centro);
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Faltante, cantidad: 2), null,
            EnCentro("Planta de Zaragoza"), EnCentro("Almacén de Huesca"));

        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } });

        CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelectorAll("button.ventana-contexto-elemento .ventana-contexto-elemento-secundario")
            .Select(e => e.TextContent.Trim()).Should().Equal("Planta de Zaragoza", "Almacén de Huesca");
    }

    /// <summary>
    /// Con la consulta de la página no ocurre (un estado sale de alertas, y las alertas son las incidencias): es
    /// la guarda que evita abrir una ventana sin líneas si la fila llegara sin ellas.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.Consulta)]
    public void En_la_ventana_lo_urgente_va_en_rojo_y_lo_proximo_en_ambar_aunque_los_dos_digan_Por_vencer(string rol)
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Urgente, cantidad: 1), null,
            AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Urgente, 5),
            AlertaDeDocumento("Contrato", "Nora Vidal", EstadoDocumento.Proximo, 25));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } }, rol: rol);

        var celda = CeldaDeEstado(cut, "Alfa Montajes S.L.");
        var deLaVentana = celda.QuerySelectorAll(".ventana-contexto-panel .badge");
        deLaVentana.Select(b => b.TextContent.Trim()).Should().Equal(["Por vencer", "Por vencer"],
            "el rótulo no las distingue: las separa el color");
        deLaVentana[0].ClassList.Should().Contain("badge-peligro", "una línea de desglose colorea por gravedad, como en Trabajadores");
        deLaVentana[1].ClassList.Should().Contain("badge-advertencia");
        celda.QuerySelector(".badge")!.ClassList.Should().Contain("badge-advertencia",
            "la pastilla de la fila colorea por rótulo, y «Por vencer» es ámbar");
    }

    [Theory]
    [InlineData(EstadoDocumento.Faltante, "pendiente")]
    [InlineData(EstadoDocumento.Vencido, "vencido")]
    public void El_tooltip_de_la_pastilla_habla_de_alertas_documentales_y_no_solo_de_vigencia(EstadoDocumento peor, string rotulo)
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: peor, cantidad: 1), null,
            peor == EstadoDocumento.Faltante
                ? AlertaDeFaltante("Formación Art. 19", "Javier Salas", "Planta de Zaragoza")
                : AlertaDeDocumento("Contrato", "Nora Vidal", peor, -3));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } });

        CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelector(".badge")!.GetAttribute("title").Should().Be(
            $"Peor estado entre las alertas documentales abiertas de sus trabajadores: {rotulo}",
            "el agregado cuenta también los documentos que faltan, que no son alertas de vigencia");
    }

    [Fact]
    public void Una_fila_con_estado_y_sin_incidencias_entregadas_conserva_el_motivo_de_texto()
    {
        var alfa = Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 2);
        var beta = ConIncidencias(Cliente("Beta Talleres Coop.", peor: EstadoDocumento.Vencido, cantidad: 1), null,
            AlertaDeDocumento("Contrato", "Nora Vidal", EstadoDocumento.Vencido, -3));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa, beta } });

        var celda = CeldaDeEstado(cut, "Alfa Montajes S.L.");
        celda.QuerySelector(".estado-fila-motivo")!.TextContent.Trim().Should().Be("2 documentos");
        celda.QuerySelectorAll(".ventana-contexto").Should().BeEmpty("sin líneas que enseñar no se abre una ventana vacía");
        CeldaDeEstado(cut, "Beta Talleres Coop.").QuerySelectorAll(".ventana-contexto").Should().ContainSingle(
            "control positivo: la fila que sí trae incidencias lleva su ventana");
    }

    [Fact]
    public void Quien_solo_consulta_ve_las_mismas_lineas_sin_botones()
    {
        var vencida = AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12);
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 1), null,
            vencida, AlertaDeFaltante("Formación Art. 19", "Javier Salas", "Planta de Zaragoza"));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } }, rol: Roles.Consulta);

        var celda = CeldaDeEstado(cut, "Alfa Montajes S.L.");
        celda.QuerySelectorAll("button").Should().BeEmpty("no se ofrece un formulario que el comando va a denegar");
        celda.QuerySelectorAll(".ventana-linea .incidencia-cliente-texto").Select(e => e.TextContent.Trim())
            .Should().Equal(["Aptitud médica — Nora Vidal", "Formación Art. 19 — Javier Salas"], "control positivo: el desglose se sigue viendo");
        celda.QuerySelectorAll(".ventana-linea .incidencia-cliente-secundario").Select(e => e.TextContent.Trim())
            .Should().Equal(Fecha(vencida), "Planta de Zaragoza");
        celda.QuerySelectorAll(".ventana-contexto-pie").Should().BeEmpty("sin nada que pulsar ni que quede fuera, no hay pie");
    }

    [Fact]
    public void Si_no_caben_todas_el_pie_dice_cuantas_quedan_fuera()
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 14), totales: 14,
            AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12),
            AlertaDeDocumento("Contrato", "Nora Vidal", EstadoDocumento.Vencido, -3));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } });

        var celda = CeldaDeEstado(cut, "Alfa Montajes S.L.");
        celda.QuerySelector(".ventana-contexto-titulo")!.TextContent.Trim()
            .Should().Be("14 documentos con incidencia entre sus trabajadores", "el título cuenta todas, no las que caben");
        celda.QuerySelectorAll("button.ventana-contexto-elemento").Should().HaveCount(2);
        celda.QuerySelector(".ventana-contexto-pie")!.TextContent.Trim()
            .Should().Be("Clic en una para corregirla aquí. Y 12 más: aparecen aquí al corregir estas.");
    }

    [Fact]
    public void A_quien_solo_consulta_el_pie_le_dice_cuantas_quedan_fuera_sin_hablarle_de_corregir()
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 14), totales: 14,
            AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12));
        var cut = Renderizar(new MediatorFalso { Almacen = { alfa } }, rol: Roles.Consulta);

        CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelector(".ventana-contexto-pie")!.TextContent.Trim().Should().Be("Y 13 más.");
    }

    // --- Lo que hace al pulsar -------------------------------------------------------------------

    [Fact]
    public async Task Pulsar_una_alerta_con_documento_abre_la_correccion_de_ese_documento_sin_recargar_la_lista()
    {
        var vencida = AlertaDeDocumento("Aptitud médica", "Nora Vidal", EstadoDocumento.Vencido, -12);
        var urgente = AlertaDeDocumento("Contrato", "Javier Salas", EstadoDocumento.Urgente, 5);
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Vencido, cantidad: 1), null, vencida, urgente);
        var mediador = new MediatorFalso { Almacen = { alfa } };
        ConFormularioDeCorreccion(mediador);
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);
        var boton = CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelectorAll("button.ventana-contexto-elemento")[1];
        boton.Closest(".ventana-contexto-panel").Should().NotBeNull(
            "es lo que «pulsarFila» de atajos-lista.js excluye para no abrir además la vista rápida");

        await boton.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(urgente.DocumentoId!.Value, "se corrige el documento de la línea pulsada, no el primero");
        ConsultasDeLista(mediador).Should().Be(consultasAntes, "pulsar no recarga");
        // Que pulsar no abra ADEMÁS la vista rápida de la fila no se puede afirmar aquí: la abre «pulsarFila» de
        // atajos-lista.js, que bUnit no ejecuta. Lo único que este caso fija de esa propiedad es el Closest de arriba.
    }

    [Fact]
    public async Task Pulsar_un_faltante_abre_el_alta_del_documento_que_falta_y_no_la_edicion_de_ninguno()
    {
        var alfa = ConIncidencias(Cliente("Alfa Montajes S.L.", peor: EstadoDocumento.Faltante, cantidad: 1), null,
            AlertaDeFaltante("Formación Art. 19", "Javier Salas", "Planta de Zaragoza"));
        var mediador = new MediatorFalso { Almacen = { alfa } };
        ConFormularioDeCorreccion(mediador);
        var cut = Renderizar(mediador);

        await CeldaDeEstado(cut, "Alfa Montajes S.L.").QuerySelector("button.ventana-contexto-elemento")!.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle(
            "sin documento, lo que se abre es el alta: el formulario carga sus catálogos");
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty("no hay documento que editar");
    }

    [Fact]
    public async Task Tras_corregir_se_vuelve_a_pedir_la_misma_pagina_y_se_conservan_la_seleccion_y_la_fila_enfocada()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Alfa Montajes S.L."), Cliente("Beta Talleres Coop.") } };
        var cut = Renderizar(mediador, "clientes?q=a");
        await AlternarSeleccionMultiple(cut);
        await Fila(cut, "Beta Talleres Coop.").QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        var antes = UltimaConsulta(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        var correccion = cut.FindComponent<CorreccionIncidenciaDocumental>();
        await cut.InvokeAsync(() => correccion.Instance.OnCorregida.InvokeAsync());

        ConsultasDeLista(mediador).Should().Be(consultasAntes + 1,
            "el estado y las alertas de las filas cambian al corregir: la página se relee en sitio");
        UltimaConsulta(mediador).Should().Be(antes, "con los mismos filtros, orden y página");
        UltimaConsulta(mediador).Id.Should().BeNull("la página entera, no solo la fila pulsada");
        cut.WaitForAssertion(() =>
            FilasConDatos(cut).Select(tr => tr.QuerySelector("input[type=checkbox]")!.HasAttribute("checked")).Should().Equal(
                [false, true], "corregir un documento no es cambiar de lista: lo marcado sigue marcado"));
        FilasConDatos(cut).Select(tr => (tr.ClassName ?? string.Empty).Contains("fila-enfocada")).Should().Equal(true, false);
    }

    [Fact]
    public async Task La_seleccion_conservada_tras_corregir_no_sobrevive_a_la_siguiente_recarga()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Alfa Montajes S.L."), Cliente("Beta Talleres Coop.") } };
        var cut = Renderizar(mediador);
        await AlternarSeleccionMultiple(cut);
        await Fila(cut, "Beta Talleres Coop.").QuerySelector("input[type=checkbox]")!.ChangeAsync(new ChangeEventArgs { Value = true });
        var correccion = cut.FindComponent<CorreccionIncidenciaDocumental>();
        await cut.InvokeAsync(() => correccion.Instance.OnCorregida.InvokeAsync());
        cut.WaitForAssertion(() =>
            Fila(cut, "Beta Talleres Coop.").QuerySelector("input[type=checkbox]")!.HasAttribute("checked").Should().BeTrue(
                "punto de partida: la corrección conservó la selección"));

        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "talleres" });

        cut.WaitForAssertion(() =>
        {
            NombresDeLasFilas(cut).Should().Equal(["Beta Talleres Coop."], "control positivo: la búsqueda recargó la lista");
            Fila(cut, "Beta Talleres Coop.").QuerySelector("input[type=checkbox]")!.HasAttribute("checked").Should().BeFalse(
                "el permiso de conservar valía para una sola carga: una búsqueda sigue soltando la selección");
        });
    }
}
