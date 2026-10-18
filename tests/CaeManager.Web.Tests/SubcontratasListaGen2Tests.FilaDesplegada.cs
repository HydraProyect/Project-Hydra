using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Web.Components.Workspace;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// La fila desplegada de la lista de Subcontratas, 1:1 con «Subcontratas TALVEG.dc.html» (DDL-083): los documentos de sus
/// trabajadores que piden trabajo, cada uno en UNA línea con nombre, pastilla de estado, el texto de vigencia (fecha
/// absoluta) y, solo con escritura, la única acción «Gestionar» (regla 8 de LEEME-AUTORIDAD).
/// </summary>
public partial class SubcontratasListaGen2Tests
{
    private static IncidenciaSubcontrataDto CorregibleCon(string descripcion, EstadoDocumento estado, DateOnly? vence) =>
        new(descripcion, estado, Guid.NewGuid(), Guid.NewGuid(), vence, Guid.NewGuid());

    private static SubcontrataListaDto ConDocumentosPendientes() =>
        Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60,
            vencidas: [Corregible("Aptitud médica — Sonia Cano", EstadoDocumento.Vencido), Corregible("Contrato — Luis Sanz", EstadoDocumento.Vencido)]);

    private IElement AbrirDocumentosPendientes(IRenderedComponent<CaeManager.Web.Features.Subcontratas.Pages.Subcontratas> cut)
    {
        cut.Find(".boton-expandir-fila").Click();
        return cut.Find(".tarjeta-fila-acordeon-contenido");
    }

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public void Con_escritura_cada_documento_pendiente_ofrece_un_unico_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [ConDocumentosPendientes()] });

        var detalle = AbrirDocumentosPendientes(cut);

        var lineas = detalle.QuerySelectorAll("li.documento-pendiente-subcontrata");
        lineas.Should().HaveCount(2);
        foreach (var linea in lineas)
        {
            var botones = linea.QuerySelectorAll("button");
            botones.Should().ContainSingle("un solo verbo por documento");
            botones[0].TextContent.Trim().Should().Be("Gestionar");
            botones[0].GetAttribute("data-accion").Should().Be("gestionar");
        }
        lineas[0].QuerySelector("button")!.GetAttribute("aria-label").Should().Be("Gestionar el documento Aptitud médica — Sonia Cano de Andamios Bidasoa S.L.");
        detalle.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().OnlyContain(t => t == "Gestionar");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_el_desglose_se_ve_pero_no_ofrece_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [ConDocumentosPendientes()] });

        var detalle = AbrirDocumentosPendientes(cut);

        detalle.QuerySelectorAll("li.documento-pendiente-subcontrata").Should().HaveCount(2, "control positivo: los documentos se ven");
        detalle.QuerySelectorAll("li.documento-pendiente-subcontrata .badge").Should().HaveCount(2);
        detalle.QuerySelectorAll("button").Should().BeEmpty("no se ofrece una acción que el comando va a denegar");
        cut.Markup.Should().NotContain("Gestionar");
    }

    /// <summary>
    /// La maqueta pinta cada documento en UNA línea flex: nombre (220 px), pastilla de estado, el texto de vigencia (la fecha
    /// absoluta, nunca «hace N días») que ocupa el resto y, solo con escritura, el «Gestionar».
    /// </summary>
    [Fact]
    public void La_fila_expandida_pinta_cada_documento_en_una_linea_con_nombre_pastilla_fecha_y_Gestionar()
    {
        var vencida = CorregibleCon("Aptitud médica — Sonia Cano", EstadoDocumento.Vencido, new DateOnly(2026, 9, 30));
        var porVencer = CorregibleCon("Formación Art. 19 — Luis Sanz", EstadoDocumento.Urgente, new DateOnly(2026, 10, 28));
        var sinConfirmar = CorregibleCon("Contrato — Ane Etxeberria", EstadoDocumento.SinConfirmar, null);
        var fila = Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60, vencidas: [vencida], proximas: [porVencer], sinConfirmar: [sinConfirmar]);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [fila] });

        var detalle = AbrirDocumentosPendientes(cut);

        detalle.QuerySelectorAll("ul.documentos-pendientes-subcontrata").Should().ContainSingle();
        var lineas = detalle.QuerySelectorAll("li.documento-pendiente-subcontrata");
        lineas.Should().HaveCount(3);
        foreach (var linea in lineas)
        {
            // Hijos directos y en este orden: nombre, pastilla, texto de vigencia y acción.
            linea.Children.Select(h => h.LocalName + (h.ClassList.FirstOrDefault(c => c.StartsWith("documento-pendiente") || c == "badge") is { } c ? "." + c : ""))
                .Should().Equal("span.documento-pendiente-nombre", "span.badge", "span.documento-pendiente-detalle", "button.documento-pendiente-gestionar");
        }
        string Texto(int i, string clase) => lineas[i].QuerySelector("." + clase)!.TextContent.Trim();
        Texto(0, "documento-pendiente-nombre").Should().Be("Aptitud médica — Sonia Cano");
        Texto(0, "badge").Should().Be("Vencido");
        Texto(0, "documento-pendiente-detalle").Should().Be("Venció el 30/09/2026");
        Texto(1, "badge").Should().Be("Por vencer");
        Texto(1, "documento-pendiente-detalle").Should().Be("Caduca el 28/10/2026");
        Texto(2, "badge").Should().Be("Sin confirmar");
        Texto(2, "documento-pendiente-detalle").Should().Be("Falta la fecha de vencimiento");
        detalle.TextContent.Should().NotContain("Hace ", "la maqueta no muestra el plazo relativo en la fila expandida");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_la_fila_expandida_deja_la_linea_de_tres_celdas_sin_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [ConDocumentosPendientes()] });

        foreach (var linea in AbrirDocumentosPendientes(cut).QuerySelectorAll("li.documento-pendiente-subcontrata"))
            linea.Children.Select(h => h.LocalName).Should().Equal("span", "span", "span");
    }

    /// <summary>
    /// «Gestionar» abre la corrección del documento (el formulario de siempre) y nada más: el botón vive fuera de la fila
    /// pulsable, así que no abre además la vista rápida.
    /// </summary>
    [Fact]
    public async Task Gestionar_abre_la_correccion_del_documento_sin_abrir_la_vista_rapida()
    {
        this.ConServiciosDelFormularioDeDocumento();
        var vencida = new IncidenciaSubcontrataDto(
            "Aptitud médica — Sonia Cano", EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid());
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60, vencidas: [vencida])] };
        var cut = Renderizar(mediador);
        AbrirDocumentosPendientes(cut);

        await cut.Find("button.documento-pendiente-gestionar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId.ObtenerDocumentoPorIdQuery>()
            .Should().ContainSingle().Which.Id.Should().Be(vencida.DocumentoId!.Value);
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull();
    }

    [Fact]
    public void Una_incidencia_sin_trabajador_ni_documento_se_ve_pero_no_ofrece_Gestionar()
    {
        var mediador = new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60,
                vencidas: [Incidencia("Aptitud médica — Sonia Cano", EstadoDocumento.Vencido)])]
        };
        var cut = Renderizar(mediador);

        var detalle = AbrirDocumentosPendientes(cut);

        detalle.QuerySelector(".documento-pendiente-nombre")!.TextContent.Should().Be("Aptitud médica — Sonia Cano");
        detalle.QuerySelectorAll("button").Should().BeEmpty();
    }

    /// <summary>
    /// «Por vencer» es el rótulo de Urgente y de Próximo (vocabulario único, 2026-10-08, y maqueta). Lo que los separa en el
    /// desglose es el tono de gravedad —rojo para lo urgente— y, para quien no ve el color, el título de la pastilla.
    /// </summary>
    [Fact]
    public void El_desglose_distingue_Urgente_de_Proximo_por_el_tono_y_el_titulo_de_su_pastilla()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("Andamios Bidasoa S.L.",
                    proximas: [
                        Incidencia("EPIs — Iñaki Otaegi", EstadoDocumento.Urgente),
                        Incidencia("Reconocimiento médico — Miguel Sanz", EstadoDocumento.Proximo),
                    ]),
            ]
        });

        var lineas = AbrirDocumentosPendientes(cut).QuerySelectorAll("li.documento-pendiente-subcontrata");

        var urgente = lineas.Single(l => l.TextContent.Contains("EPIs — Iñaki Otaegi")).QuerySelector(".badge")!;
        urgente.TextContent.Trim().Should().Be("Por vencer");
        urgente.ClassList.Should().Contain("badge-peligro", "lo urgente sigue en rojo: es lo único que lo separa de lo próximo");
        urgente.GetAttribute("title").Should().Be("Por vencer (urgente)");

        var proximo = lineas.Single(l => l.TextContent.Contains("Reconocimiento médico — Miguel Sanz")).QuerySelector(".badge")!;
        proximo.TextContent.Trim().Should().Be("Por vencer");
        proximo.ClassList.Should().Contain("badge-advertencia").And.NotContain("badge-peligro");
    }
    /// <summary>
    /// Solo las filas con documentos que piden trabajo se despliegan (la maqueta no pinta chevron en las demás) y, si ninguna
    /// lo hace, «Expandir todo» no tiene nada que abrir.
    /// </summary>
    [Fact]
    public void Sin_documentos_pendientes_la_fila_no_se_despliega_ni_hay_Expandir_todo()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        cut.FindAll(".boton-expandir-fila").Should().BeEmpty();
        cut.FindAll(".barra-herramientas-lista").Should().BeEmpty();
        cut.Find(".tarjeta-fila-acordeon-cabecera").Children.Length.Should().Be(cut.Find(".cabecera-columnas-subcontratas").Children.Length,
            "la celda del chevron se reserva aunque esté vacía, para que las columnas no se corran");
    }

    [Fact]
    public void Con_alguna_fila_con_pendientes_solo_esa_se_despliega_y_aparece_Expandir_todo()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [ConDocumentosPendientes(), Subcontrata("Grúas Urola S.L.")] });

        cut.FindAll(".boton-expandir-fila").Should().ContainSingle();
        cut.Find(".barra-herramientas-lista button").TextContent.Trim().Should().Be("Expandir todo");
    }
    [Fact]
    public void El_nivel_es_texto_plano_con_su_explicacion_en_el_titulo()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", nivel: NivelServicioSubcontrata.Gestionada)]
        });

        var nivel = cut.Find(".tarjeta-fila-acordeon-cabecera .celda-nivel-subcontrata");
        nivel.TextContent.Trim().Should().Be("Gestionada");
        nivel.QuerySelector(".badge").Should().BeNull("la maqueta pinta el nivel como texto, no como pastilla");
        nivel.GetAttribute("title").Should().StartWith("Gestionada:");
    }
}
