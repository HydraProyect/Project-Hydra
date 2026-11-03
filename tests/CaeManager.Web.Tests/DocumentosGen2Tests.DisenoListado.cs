using Bunit;
using AngleSharp.Dom;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class DocumentosGen2Tests
{
    [Fact]
    public void El_listado_usa_el_diseno_nuevo_con_estado_visible_columnas_de_maqueta_y_pie_separado()
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila()));
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        cut.Find(".contenedor-pagina").ClassList.Should().Contain("lista-design");
        cut.FindAll(".cabecera-pagina button[aria-label='Selección múltiple']").Should().ContainSingle();
        cut.FindAll(".barra-herramientas-lista button[aria-label='Selección múltiple']").Should().BeEmpty();
        cut.FindAll(".franja-estado").Should().ContainSingle("el estado está visible sin abrir Más filtros");
        cut.FindAll(".documentos-tabla-visible thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Documento", "Propietario", "Vence", "Estado documental", "Acciones");
        cut.FindAll(".tarjeta-listado").Should().ContainSingle();
        cut.Find(".tarjeta-listado-pie").QuerySelector(".paginador-pie-listado").Should().NotBeNull();
        cut.Find(".tarjeta-listado-contenido").QuerySelector(".paginador-pie-listado").Should().BeNull();
        cut.Find(".documentos-acciones-fila").TextContent.Should().Contain("Ver").And.Contain("Gestionar");
    }

    [Fact]
    public void Un_documento_sin_vencimiento_dice_No_caduca_y_conserva_su_fecha_de_emision()
    {
        var documento = DocumentoDeFila() with { FechaVencimiento = null, Estado = EstadoDocumento.SinCaducidad };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find(".documentos-vigencia").TextContent.Should().Contain("No caduca"));
        cut.Find(".documentos-vigencia").TextContent.Should().Contain(documento.FechaEmision.ToString("dd/MM/yyyy"));
    }

    [Fact]
    public void Una_fecha_sin_confirmar_no_se_presenta_como_No_caduca()
    {
        var documento = DocumentoDeFila() with { FechaVencimiento = null, Estado = EstadoDocumento.SinConfirmar };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find(".documentos-vigencia").TextContent.Should().Contain("Sin confirmar"));
        cut.Find(".documentos-vigencia").TextContent.Should().NotContain("No caduca");
        cut.Find(".documentos-vigencia-subtitulo").TextContent.Should().Contain("Emisión " + documento.FechaEmision.ToString("dd/MM/yyyy"));
    }

    [Theory]
    [InlineData(-68, "hace 68 días")]
    [InlineData(-1, "hace 1 día")]
    [InlineData(0, "hoy")]
    [InlineData(1, "en 1 día")]
    [InlineData(5, "en 5 días")]
    public void Vence_muestra_el_relativo_de_la_fecha_sin_repetir_el_motivo_del_estado(int dias, string relativo)
    {
        var hoy = CaeManager.Domain.Common.DiaDeNegocio.Hoy();
        var documento = DocumentoDeFila() with
        {
            FechaVencimiento = hoy.AddDays(dias),
            Estado = dias < 0 ? EstadoDocumento.Vencido : EstadoDocumento.Urgente
        };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find(".documentos-vigencia-subtitulo").TextContent.Should().Be(relativo));
        cut.Find(".documentos-vigencia").QuerySelector(".documentos-ambito").Should().BeNull();
    }

    [Fact]
    public async Task Ordenar_por_emision_la_muestra_y_volver_a_vencimiento_restaura_el_relativo()
    {
        var documento = DocumentoDeFila() with
        {
            FechaEmision = new DateOnly(2026, 1, 8),
            FechaVencimiento = CaeManager.Domain.Common.DiaDeNegocio.Hoy().AddDays(5),
            Estado = EstadoDocumento.Urgente
        };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        await ElegirOrdenDocumentoFase1(cut, "FechaEmision");
        cut.WaitForAssertion(() => cut.Find(".documentos-vigencia-subtitulo").TextContent.Should().Be("Emisión 08/01/2026"));
        cut.Find(".documentos-tabla-visible tbody tr").TextContent.Should().Contain("08/01/2026");
        await ElegirOrdenDocumentoFase1(cut, "FechaVencimiento");
        cut.WaitForAssertion(() => cut.Find(".documentos-vigencia-subtitulo").TextContent.Should().Be("en 5 días"));
    }

    [Fact]
    public async Task Seleccionar_todos_solo_marca_los_grupos_visibles_y_plegarlos_vacia_la_seleccion()
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila(), DocumentoDeFila() with { Ambito = AmbitoAplicacion.Empresa }));
        await cut.Find("button[aria-label='Agrupar: no']").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Por ámbito").ClickAsync(new MouseEventArgs());
        await cut.Find("button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-tabla-visible tbody input[type=checkbox]").Should().BeEmpty();
        await cut.Find(".documentos-tabla-visible thead input[type=checkbox]").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();

        await cut.Find("button.grupo-lista-cabecera").ClickAsync(new MouseEventArgs());
        await cut.Find(".documentos-tabla-visible thead input[type=checkbox]").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        cut.FindAll(".documentos-tabla-visible tbody input:checked").Should().ContainSingle();
        cut.FindAll(".barra-acciones-lote").Should().ContainSingle();

        await cut.Find("button.grupo-lista-cabecera").ClickAsync(new MouseEventArgs());
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        cut.FindAll(".documentos-tabla-visible tbody input[type=checkbox]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador", true, true)]
    [InlineData("DireccionCae", true, true)]
    [InlineData("CoordinadorCae", true, true)]
    [InlineData("GestorCae", true, false)]
    [InlineData("Consulta", false, false)]
    [InlineData("Cliente", false, false)]
    public async Task El_diseno_conserva_las_acciones_y_secciones_de_main_por_rol(
        string rol, bool escritura, bool importacion)
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila()), rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        Pestanas(cut).Select(p => string.Concat(p.ChildNodes.Where(n => n.NodeType == NodeType.Text).Select(n => n.TextContent)).Trim()).Should().Equal(
            "Estado", "Plataformas CAE", "Reclamaciones", "Preventivo", "Revisión IA", "Plantillas");
        cut.FindAll("tbody button.enlace-nombre-fila").Should().Contain(b => b.TextContent == "Javier Salas Moreno");
        cut.FindAll("tbody button").Should().Contain(b => b.TextContent.Trim() == "Ver",
            "Consulta conserva el acceso de lectura al archivo");
        cut.FindAll("tbody .documentos-accion-rapida").Should().HaveCount(escritura ? 1 : 0);
        cut.FindAll("tbody a.boton-360-pagina").Should().HaveCount(escritura ? 1 : 0);
        if (escritura)
        {
            cut.Find("tbody .documentos-accion-rapida").TextContent.Trim().Should().Be("Gestionar");
            cut.Find("tbody a.boton-360-pagina").GetAttribute("href").Should().Be($"/documentos/tipos/{TipoId}");
        }

        await cut.Find(".cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        var cabecera = cut.Find(".cabecera-pagina");
        cabecera.QuerySelectorAll("a[href^='/documentos/exportar.xlsx']").Should().HaveCount(2);
        cabecera.TextContent.Contains("Nuevo documento").Should().Be(escritura);
        cabecera.TextContent.Contains("Subida múltiple").Should().Be(escritura);
        cabecera.TextContent.Contains("Importar documentos").Should().Be(importacion);
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty("abrir el listado nunca selecciona documentos");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente, true)]
    [InlineData(EstadoDocumento.SinCaducidad, true)]
    [InlineData(EstadoDocumento.Vencido, false)]
    [InlineData(EstadoDocumento.Urgente, false)]
    [InlineData(EstadoDocumento.Proximo, false)]
    public void Gestionar_mantiene_un_verbo_y_solo_los_documentos_correctos_tienen_tono_secundario(
        EstadoDocumento estado, bool tonoSecundario)
    {
        var documento = DocumentoDeFila() with { Estado = estado };
        var (cut, _) = Renderizar(ConDocumentos(documento));
        cut.WaitForAssertion(() => cut.Find("tbody .documentos-accion-rapida"));

        var accion = cut.Find("tbody .documentos-accion-rapida");
        accion.TextContent.Trim().Should().Be("Gestionar");
        accion.GetAttribute("aria-label").Should().Be("Gestionar Reconocimiento médico de Javier Salas Moreno");
        accion.ClassList.Contains("documentos-accion-correcta").Should().Be(tonoSecundario);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("Consulta")]
    public void El_archivo_ausente_conserva_su_estado_de_lectura_por_rol(string rol)
    {
        var (cut, _) = Renderizar(ConDocumentos(DocumentoDeFila(archivoUrl: null)), rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody tr").TextContent.Should().Contain("Sin adjunto"));
        cut.FindAll("tbody button").Should().NotContain(b => b.TextContent.Trim() == "Ver");
    }

    [Fact]
    public async Task Agrupar_por_ambito_muestra_titulos_plegables_y_permite_volver_al_listado()
    {
        var trabajador = DocumentoDeFila();
        var empresa = DocumentoDeFila() with { Ambito = AmbitoAplicacion.Empresa, PropietarioNombre = "Empresa de prueba" };
        var (cut, mediador) = Renderizar(ConDocumentos(trabajador, empresa));
        cut.WaitForAssertion(() => cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().HaveCount(2));
        var ordenOriginal = UltimaConsultaDeDocumentos(mediador).OrdenarPor;
        cut.FindAll("button.grupo-lista-cabecera").Should().BeEmpty();

        await cut.Find("button[aria-label='Agrupar: no']").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Por ámbito")
            .ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).AgruparPorAmbito.Should().BeTrue());
        UltimaConsultaDeDocumentos(mediador).TamanoPagina.Should().Be(AgrupacionDeLista.TamanoPaginaAgrupada,
            "agrupada, la pantalla pide todas las filas filtradas: el tamaño de página no corta los grupos");
        cut.FindAll(".documentos-cabecera-grupo").Should().HaveCount(2);
        var titulos = cut.FindAll("button.grupo-lista-cabecera");
        titulos.Should().HaveCount(2, "los títulos de agrupación son botones que pliegan y abren el grupo");
        titulos.Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "los grupos nacen plegados");
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().BeEmpty("las filas de un grupo plegado no se pintan");

        cut.AbrirGrupos();
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().HaveCount(2);

        await cut.Find("button[aria-label='Agrupar: por ámbito']").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Sin agrupar")
            .ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("button.grupo-lista-cabecera").Should().BeEmpty());
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).OrdenarPor.Should().Be(ordenOriginal));
        UltimaConsultaDeDocumentos(mediador).AgruparPorAmbito.Should().BeFalse();
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().HaveCount(2);
    }

    [Fact]
    public async Task Los_grupos_usan_el_resumen_de_la_consulta_y_la_tabla_visible_recibe_foco_seleccion_y_orden()
    {
        var trabajador = DocumentoDeFila();
        var empresa = DocumentoDeFila() with { Ambito = AmbitoAplicacion.Empresa };
        var mediador = ConDocumentos(trabajador, empresa);
        mediador.Interceptar = q => q is ObtenerDocumentosQuery { AgruparPorAmbito: true } consulta
            ? Task.FromResult<object?>(mediador.Pagina(consulta) with
            {
                ResumenPorGrupo = new Dictionary<string, ResumenDeGrupo>
                {
                    [nameof(AmbitoAplicacion.Trabajador)] = new(72, new Dictionary<string, int>())
                }
            }) : null;
        var (cut, _) = Renderizar(mediador);
        await cut.Find("button[aria-label='Agrupar: no']").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Por ámbito").ClickAsync(new MouseEventArgs());
        UltimaConsultaDeDocumentos(mediador).TamanoPagina.Should().Be(AgrupacionDeLista.TamanoPaginaAgrupada,
            "agrupada, la pantalla pide todas las filas filtradas: la 'página' es el listado entero y el grupo no se corta");
        cut.ContadoresDeGrupo().Should().Equal(["· 1", "· 1"],
            "el recuento del grupo es el de sus filas (todas las pedidas), no el resumen de la consulta ni «n de N»");
        cut.FindAll(".documentos-tabla-agrupada tbody tr:has(button.enlace-nombre-fila)").Should().BeEmpty("los grupos nacen plegados");
        cut.AbrirGrupos();
        cut.FindAll(".documentos-tabla-agrupada tbody tr:has(button.enlace-nombre-fila)").Should().HaveCount(2);

        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
        cut.FindAll(".fila-enfocada").Should().ContainSingle("la rejilla oculta nunca captura el foco");
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("Enter"));
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(trabajador.Id);

        await cut.Find("button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find(".documentos-tabla-agrupada thead input[type=checkbox]").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        cut.FindAll(".documentos-tabla-agrupada tbody input:checked").Should().HaveCount(2);

        await cut.FindAll(".documentos-orden-grupo").Single(b => b.TextContent.Trim() == "Documento").ClickAsync(new MouseEventArgs());
        UltimaConsultaDeDocumentos(mediador).OrdenarPor.Should().Be(nameof(DocumentoListaDto.TipoDocumentoNombre));
        UltimaConsultaDeDocumentos(mediador).AgruparPorAmbito.Should().BeTrue();
        cut.Find(".documentos-tabla-agrupada th[aria-sort='ascending']").TextContent.Trim().Should().Be("Documento");
    }

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    public async Task El_cheuron_despliega_destinos_del_documento_y_conserva_su_lectura_por_rol(string rol, bool escritura)
    {
        var centroId = Guid.NewGuid();
        var documento = DocumentoDeFila() with
        {
            DestinosEnCentros = [new(centroId, "Centro Norte", "Empresa de prueba", "Nalanda", EstadoDocumento.Vencido)]
        };
        var (cut, mediador) = Renderizar(ConDocumentos(documento), rol: rol);
        UltimaConsultaDeDocumentos(mediador).ConDestinosEnCentros.Should().BeTrue();
        cut.FindAll(".documentos-fila-hijos").Should().BeEmpty();
        var cheuron = cut.Find("button.documentos-expandir");
        cheuron.GetAttribute("aria-expanded").Should().Be("false");
        cheuron.GetAttribute("aria-label").Should().Be("Desplegar Reconocimiento médico");
        await cheuron.ClickAsync(new MouseEventArgs());
        cut.Find("button.documentos-expandir").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".documentos-fila-hijos a").GetAttribute("href").Should().Be($"/centros/{centroId}");
        cut.Find(".documentos-fila-hijos").TextContent.Should().Contain("Empresa de prueba · Nalanda").And.Contain("Vencido");
        cut.FindAll(".documentos-fila-hijos .documentos-accion-rapida").Should().HaveCount(escritura ? 1 : 0);
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse("desplegar no abre la vista rápida ni envía una escritura");
        mediador.Enviadas.Should().NotContain(p => p.GetType().Name.EndsWith("Command", StringComparison.Ordinal));
        await cut.Find("button.documentos-expandir").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-fila-hijos").Should().BeEmpty();

        await cut.Find(".cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".cabecera-pagina [role=menuitem]").Single(b => b.TextContent.Trim() == "Expandir todo").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-fila-hijos").Should().ContainSingle();
        await cut.Find(".cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".cabecera-pagina [role=menuitem]").Single(b => b.TextContent.Trim() == "Contraer todo").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-fila-hijos").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_documento_con_muchos_destinos_ensena_diez_y_pide_el_resto_con_Ver_los_N_restantes()
    {
        var documento = DocumentoDeFila() with
        {
            DestinosEnCentros = [.. Enumerable.Range(1, 25).Select(i =>
                new DocumentoEnCentroListaDto(Guid.NewGuid(), $"Centro {i}", "Empresa de prueba", "Nalanda", EstadoDocumento.Vencido))]
        };
        var (cut, _) = Renderizar(ConDocumentos(documento), rol: "Administrador");
        await cut.Find("button.documentos-expandir").ClickAsync(new MouseEventArgs());

        cut.FindAll(".documentos-hijo").Should().HaveCount(10);
        cut.Find("button.ver-restantes-fila").TextContent.Trim().Should().Be("Ver los 15 restantes");

        await cut.Find("button.ver-restantes-fila").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-hijo").Should().HaveCount(25);
        cut.FindAll("button.ver-restantes-fila").Should().BeEmpty();

        await cut.Find("button.documentos-expandir").ClickAsync(new MouseEventArgs());
        await cut.Find("button.documentos-expandir").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-hijo").Should().HaveCount(10, "recoger la fila reinicia el recorte");

        await cut.Find("button.ver-restantes-fila").ClickAsync(new MouseEventArgs());
        await cut.Find(".cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".cabecera-pagina [role=menuitem]").Single(b => b.TextContent.Trim() == "Contraer todo").ClickAsync(new MouseEventArgs());
        await cut.Find(".cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".cabecera-pagina [role=menuitem]").Single(b => b.TextContent.Trim() == "Expandir todo").ClickAsync(new MouseEventArgs());
        cut.FindAll(".documentos-hijo").Should().HaveCount(10, "«Contraer todo» también reinicia el recorte");
    }
}
