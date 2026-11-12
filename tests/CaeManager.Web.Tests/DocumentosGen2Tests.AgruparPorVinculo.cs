using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// P-1 (2026-11-12): «Agrupar por» Plataforma CAE y Cliente empresarial, el filtro «Estado en la plataforma», la cabecera
/// de grupo con los recuentos del servidor, «Abrir portal» y «Anotar vigencia (N)». Antes, lo que el listado ya tenía en
/// main (fila, acciones y permisos por rol) se fija también con las dos agrupaciones nuevas.
/// <para>
/// No observa el alcance de cartera ni la regla de «URL única» (las decide el handler, en Application); aquí la consulta
/// es un doble y solo se mira lo que la página hace con lo que recibe.
/// </para>
/// </summary>
public partial class DocumentosGen2Tests
{
    private static readonly Guid NalandaId = Guid.NewGuid();
    private static readonly Guid CtaimaId = Guid.NewGuid();
    private static readonly Guid ClienteNorteId = Guid.NewGuid();

    private sealed record EscenarioVinculos(
        DocumentoListaDto SinVigencia, DocumentoListaDto Rechazado, DocumentoListaDto Pendiente, DocumentoListaDto SinVinculo,
        Guid AcreditacionNorte, Guid AcreditacionSur, MediadorControlado Mediador);

    private static DocumentoListaDto EnPlataforma(DocumentoListaDto documento, Guid proveedorId, Guid acreditacionId,
        EstadoAcreditacion estado, string clienteEmpresarial, string? motivo = null) =>
        documento with
        {
            Grupo = new GrupoDeFilaDocumentalDto(proveedorId.ToString(), new AcreditacionEnGrupoDto(
                acreditacionId, estado, EstadoVigenciaEnPlataforma.SinConfirmar, clienteEmpresarial, motivo))
        };

    private static ResumenDeGrupoDocumental ResumenPlataforma(string nombre, int total, string? url,
        params (EstadoEnPlataformaFiltro Estado, int Cuantas)[] enPlataforma) =>
        new(total, new Dictionary<string, int>(), nombre, enPlataforma.ToDictionary(x => x.Estado, x => x.Cuantas), url);

    /// <summary>
    /// Cuatro Documentos: uno aceptado sin vigencia en Nalanda para dos Clientes empresariales (dos filas), uno rechazado
    /// en Nalanda, uno pendiente en Ctaima y uno sin vínculo. Los recuentos de las cabeceras son mayores que la página a
    /// propósito: la página debe pintar los del servidor, no contar sus filas.
    /// </summary>
    private static EscenarioVinculos ConVinculos(string? urlNalanda = "https://nalanda.example.com")
    {
        var sinVigencia = DocumentoDeFila();
        var rechazado = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Ana Ruiz Pardo" };
        var pendiente = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Luis Mora Gil" };
        var sinVinculo = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Eva Soto Paz" };
        var norte = Guid.NewGuid();
        var sur = Guid.NewGuid();
        var porPlataforma = new[]
        {
            EnPlataforma(sinVigencia, NalandaId, norte, EstadoAcreditacion.Aceptada, "Cliente Norte"),
            EnPlataforma(sinVigencia, NalandaId, sur, EstadoAcreditacion.Aceptada, "Cliente Sur"),
            EnPlataforma(rechazado, NalandaId, Guid.NewGuid(), EstadoAcreditacion.Rechazada, "Cliente Norte", "Ilegible"),
            EnPlataforma(pendiente, CtaimaId, Guid.NewGuid(), EstadoAcreditacion.PendienteDeSubir, "Cliente Norte"),
            sinVinculo with { Grupo = new GrupoDeFilaDocumentalDto(ObtenerDocumentosQuery.GrupoSin) },
        };
        var porCliente = new[]
        {
            sinVigencia with { Grupo = new GrupoDeFilaDocumentalDto(ClienteNorteId.ToString()) },
            rechazado with { Grupo = new GrupoDeFilaDocumentalDto(ClienteNorteId.ToString()) },
            sinVinculo with { Grupo = new GrupoDeFilaDocumentalDto(ObtenerDocumentosQuery.GrupoSin) },
        };
        var resumenPlataforma = new Dictionary<string, ResumenDeGrupo>
        {
            [NalandaId.ToString()] = ResumenPlataforma("Nalanda", 7, urlNalanda,
                (EstadoEnPlataformaFiltro.Rechazado, 1), (EstadoEnPlataformaFiltro.SinVigencia, 4)),
            [CtaimaId.ToString()] = ResumenPlataforma("Ctaima", 1, null, (EstadoEnPlataformaFiltro.PendienteDeSubir, 1)),
            [ObtenerDocumentosQuery.GrupoSin] = ResumenPlataforma(string.Empty, 1, null),
        };
        var resumenCliente = new Dictionary<string, ResumenDeGrupo>
        {
            [ClienteNorteId.ToString()] = new ResumenDeGrupoDocumental(12,
                new Dictionary<string, int> { [nameof(EstadoDocumento.Vencido)] = 3, [nameof(EstadoDocumento.Urgente)] = 1 },
                "Cliente Norte", new Dictionary<EstadoEnPlataformaFiltro, int>(), "https://no-debe-pintarse.example.com"),
            [ObtenerDocumentosQuery.GrupoSin] = ResumenPlataforma(string.Empty, 1, null),
        };
        var mediador = new MediadorControlado
        {
            Documentos = q => q.AgruparPor switch
            {
                AgrupacionDocumentos.PlataformaCae => porPlataforma,
                AgrupacionDocumentos.ClienteEmpresarial => porCliente,
                _ => [sinVigencia, rechazado, pendiente, sinVinculo],
            },
            ResumenesDeGrupo = q => q.AgruparPor switch
            {
                AgrupacionDocumentos.PlataformaCae => resumenPlataforma,
                AgrupacionDocumentos.ClienteEmpresarial => resumenCliente,
                _ => null,
            },
        };
        return new EscenarioVinculos(sinVigencia, rechazado, pendiente, sinVinculo, norte, sur, mediador);
    }

    private static async Task Agrupar(IRenderedComponent<PaginaDocumentos> cut, string opcion)
    {
        await cut.Find("button[aria-label^='Agrupar']").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    private static IElement CabeceraDeVinculo(IRenderedComponent<PaginaDocumentos> cut, string clave) =>
        cut.Find($"tr.documentos-cabecera-grupo-vinculo[data-grupo='{clave}']");

    private static IReadOnlyList<string> NombresDeVinculo(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindAll("tr.documentos-cabecera-grupo-vinculo .grupo-lista-nombre").Select(n => n.TextContent.Trim()).ToList();

    private static IReadOnlyList<string> ResumenDeVinculo(IElement cabecera) =>
        cabecera.QuerySelectorAll(".grupo-lista-resumen-estado").Select(n => n.TextContent.Trim()).ToList();

    // ------------------------------------------------------------- lo de main, con las agrupaciones nuevas

    /// <summary>
    /// Inventario de lo que la fila tenía en main y sigue igual al agrupar por vínculo: el nombre que abre la vista rápida,
    /// el propietario, las insignias de plataformas, «Ver», «Gestionar» y el 360 solo con escritura, y «Selección
    /// múltiple» solo con escritura. Sin agrupar, la tabla no gana la columna nueva.
    /// </summary>
    [Theory]
    [InlineData("Administrador", true, "Por Plataforma CAE")]
    [InlineData("GestorCae", true, "Por Plataforma CAE")]
    [InlineData("Consulta", false, "Por Plataforma CAE")]
    [InlineData("Cliente", false, "Por Plataforma CAE")]
    [InlineData("Administrador", true, "Por Cliente")]
    [InlineData("Consulta", false, "Por Cliente")]
    [InlineData("Cliente", false, "Por Cliente")]
    public async Task Agrupar_por_vinculo_conserva_la_fila_y_las_acciones_de_main_por_rol(string rol, bool escritura, string agrupacion)
    {
        var e = ConVinculos();
        var conInsignia = e.SinVigencia with { Acreditaciones = [new AcreditacionResumenDto(e.AcreditacionNorte, "Nalanda", EstadoAcreditacion.Aceptada)] };
        var documentos = e.Mediador.Documentos;
        e.Mediador.Documentos = q => documentos(q).Select(d => d.Id == conInsignia.Id ? d with { Acreditaciones = conInsignia.Acreditaciones } : d).ToList();
        var (cut, _) = Renderizar(e.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        cut.FindAll(".documentos-tabla-visible thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Documento", "Propietario", "Vence", "Estado documental", "Acciones");

        await Agrupar(cut, agrupacion);

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().NotBeEmpty());
        var filas = cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)");
        filas.Should().NotBeEmpty();
        foreach (var fila in filas)
        {
            fila.QuerySelectorAll("button.enlace-nombre-fila").Should().HaveCount(2, "nombre del documento y propietario abren la vista rápida");
            fila.QuerySelectorAll("button").Should().Contain(b => b.TextContent.Trim() == "Ver");
            fila.QuerySelectorAll(".documentos-accion-rapida").Should().HaveCount(escritura ? 1 : 0);
            fila.QuerySelectorAll("a.boton-360-pagina").Should().HaveCount(escritura ? 1 : 0);
        }
        cut.FindAll(".documentos-tabla-visible tbody .documentos-plataformas").Should().NotBeEmpty(
            "las insignias de plataformas de la fila siguen ahí");
        cut.FindAll(".cabecera-pagina button[aria-label='Selección múltiple']").Should().HaveCount(escritura ? 1 : 0);
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        var columnas = cut.FindAll(".documentos-tabla-visible thead th").Select(h => h.TextContent.Trim()).ToList();
        if (agrupacion == "Por Plataforma CAE")
            columnas.Should().Equal("Documento", "Propietario", "Vence", "Estado documental", "Estado en la plataforma", "Acciones");
        else
            columnas.Should().Equal("Documento", "Propietario", "Vence", "Estado documental", "Acciones");
    }

    // ------------------------------------------------------------- opciones de «Agrupar»

    [Fact]
    public async Task Agrupar_ofrece_las_cuatro_opciones_y_cada_una_pide_su_agrupacion()
    {
        var e = ConVinculos();
        var (cut, mediador) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await cut.Find("button[aria-label='Agrupar: no']").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=menuitemradio]").Select(b => b.TextContent.Trim())
            .Should().Equal("Sin agrupar", "Por ámbito", "Por Plataforma CAE", "Por Cliente");
        await cut.FindAll("[role=menuitemradio]").Single(b => b.TextContent.Trim() == "Por Plataforma CAE").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).AgruparPor.Should().Be(AgrupacionDocumentos.PlataformaCae));
        UltimaConsultaDeDocumentos(mediador).AgruparPorAmbito.Should().BeFalse();
        UltimaConsultaDeDocumentos(mediador).TamanoPagina.Should().Be(AgrupacionDeLista.TamanoPaginaAgrupada,
            "agrupada por vínculo, la pantalla también pide todas las filas filtradas");
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label^='Agrupar']").Select(b => b.GetAttribute("aria-label")).Should().Equal("Agrupar por: Plataforma CAE"));

        await Agrupar(cut, "Por Cliente");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).AgruparPor.Should().Be(AgrupacionDocumentos.ClienteEmpresarial));
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label^='Agrupar']").Select(b => b.GetAttribute("aria-label")).Should().Equal("Agrupar por: Cliente"));

        await Agrupar(cut, "Por ámbito");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).AgruparPorAmbito.Should().BeTrue());
        UltimaConsultaDeDocumentos(mediador).AgruparPor.Should().BeNull("por Ámbito sigue siendo su propia agrupación");
        cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().BeEmpty();

        await Agrupar(cut, "Sin agrupar");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).AgruparPorAmbito.Should().BeFalse());
        UltimaConsultaDeDocumentos(mediador).AgruparPor.Should().BeNull();
        cut.FindAll("button.grupo-lista-cabecera").Should().BeEmpty();
    }

    // ------------------------------------------------------------- cabecera de grupo

    [Fact]
    public async Task Por_Plataforma_CAE_la_cabecera_pinta_los_recuentos_del_servidor_y_el_grupo_sin_va_al_final_plegado()
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => NombresDeVinculo(cut).Should().Equal("Ctaima", "Nalanda", "Sin Plataforma CAE"));
        var nalanda = CabeceraDeVinculo(cut, NalandaId.ToString());
        ResumenDeVinculo(nalanda).Should().Equal(["1 rechazado", "4 sin vigencia"], "un cero no se pinta");
        nalanda.QuerySelector(".grupo-lista-contador")!.TextContent.Trim().Should().Be("3 de 7",
            "el total es el del servidor, no las tres filas de la página");
        ResumenDeVinculo(CabeceraDeVinculo(cut, CtaimaId.ToString())).Should().Equal("1 pendiente de subir");
        CabeceraDeVinculo(cut, ObtenerDocumentosQuery.GrupoSin).QuerySelector("button.grupo-lista-cabecera")!
            .GetAttribute("aria-expanded").Should().Be("false", "el grupo «Sin Plataforma CAE» nace plegado");
        nalanda.QuerySelector("button.grupo-lista-cabecera")!.GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("tbody button.enlace-nombre-fila").Should().NotContain(b => b.TextContent.Contains("Eva Soto Paz"),
            "las filas de un grupo plegado no se pintan");

        var enPlataforma = cut.FindAll("tbody .documentos-en-plataforma");
        enPlataforma.Should().HaveCount(4);
        enPlataforma.Select(c => c.QuerySelector(".documentos-en-plataforma-cliente")!.TextContent.Trim())
            .Should().Equal("Cliente Norte", "Cliente Norte", "Cliente Sur", "Cliente Norte");
        var rechazado = enPlataforma.Single(c => c.QuerySelector(".documentos-en-plataforma-motivo") is not null);
        rechazado.TextContent.Should().Contain("Rechazad").And.Contain("Ilegible");
        enPlataforma.Count(c => c.TextContent.Contains("Sin vigencia")).Should().Be(2);

        await CabeceraDeVinculo(cut, ObtenerDocumentosQuery.GrupoSin).QuerySelector("button.grupo-lista-cabecera")!
            .ClickAsync(new MouseEventArgs());
        cut.FindAll("tbody button.enlace-nombre-fila").Should().Contain(b => b.TextContent.Contains("Eva Soto Paz"));
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Last().QuerySelector(".documentos-celda-en-plataforma")!
            .TextContent.Trim().Should().BeEmpty("una fila sin Plataforma CAE no tiene estado en la plataforma");
    }

    [Fact]
    public async Task Por_Cliente_empresarial_la_cabecera_dice_documentos_vencidos_y_urgentes_y_no_ofrece_portal()
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Cliente");

        cut.WaitForAssertion(() => NombresDeVinculo(cut).Should().Equal("Cliente Norte", "Sin Cliente"));
        var norte = CabeceraDeVinculo(cut, ClienteNorteId.ToString());
        ResumenDeVinculo(norte).Should().Equal("3 vencidos", "1 urgente");
        norte.QuerySelector(".grupo-lista-subtitulo")!.TextContent.Trim().Should().Be("Cliente · 12 documentos");
        cut.FindAll(".documentos-abrir-portal").Should().BeEmpty("«Abrir portal» es de los grupos por Plataforma CAE");
        cut.FindAll(".documentos-anotar-vigencia").Should().BeEmpty();
        cut.FindAll("tbody .documentos-celda-en-plataforma").Should().BeEmpty();
    }

    // ------------------------------------------------------------- Abrir portal

    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("GestorCae", true)]
    [InlineData("Consulta", true)]
    [InlineData("Cliente", false)]
    public async Task Abrir_portal_sale_solo_con_la_direccion_unica_del_servidor_y_nunca_para_el_rol_Cliente(string rol, bool ofrece)
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().HaveCount(3));
        var portales = cut.FindAll(".documentos-abrir-portal");
        portales.Should().HaveCount(ofrece ? 1 : 0, "Ctaima no tiene una dirección única y el grupo «sin» no tiene portal");
        if (!ofrece) return;
        var portal = CabeceraDeVinculo(cut, NalandaId.ToString()).QuerySelector(".documentos-abrir-portal")!;
        portal.GetAttribute("href").Should().StartWith("https://nalanda.example.com");
        portal.GetAttribute("target").Should().Be("_blank");
        portal.GetAttribute("rel").Should().Be("noopener noreferrer");
        portal.GetAttribute("aria-label").Should().Be("Abrir el portal de Nalanda en una pestaña nueva");
    }

    [Fact]
    public async Task Abrir_portal_no_abre_una_direccion_que_no_sea_http()
    {
        var e = ConVinculos(urlNalanda: "ftp://nalanda.example.com");
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().HaveCount(3));
        cut.FindAll(".documentos-abrir-portal").Should().BeEmpty();
    }

    // ------------------------------------------------------------- Anotar vigencia (N)

    [Fact]
    public async Task Anotar_vigencia_cuenta_las_acreditaciones_sin_vigencia_de_lo_seleccionado_y_anota_cada_una()
    {
        var e = ConVinculos();
        var anotadas = new List<ConfirmarVigenciaAcreditacionCommand>();
        e.Mediador.Interceptar = p => p is ConfirmarVigenciaAcreditacionCommand c
            ? Task.FromResult<object?>(Anotar(anotadas, c))
            : null;
        var (cut, mediador) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().HaveCount(3));
        cut.FindAll(".documentos-anotar-vigencia").Should().BeEmpty("sin nada seleccionado no hay nada que anotar");

        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await CasillaDe(cut, e.Rechazado).ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".documentos-anotar-vigencia").Should().BeEmpty("una rechazada no es «Sin vigencia»");

        await CasillaDe(cut, e.SinVigencia).ChangeAsync(new ChangeEventArgs { Value = true });
        var anotar = cut.FindAll(".documentos-anotar-vigencia").Should().ContainSingle().Subject;
        anotar.TextContent.Trim().Should().Be("Anotar vigencia (2)",
            "seleccionar el Documento marca sus dos filas de Nalanda, y cada una es una acreditación sin vigencia");
        CabeceraDeVinculo(cut, NalandaId.ToString()).QuerySelector(".documentos-anotar-vigencia").Should().NotBeNull();

        await anotar.ClickAsync(new MouseEventArgs());
        var dialogo = cut.Find("[role=dialog]");
        dialogo.TextContent.Should().Contain("Anotar vigencia").And.Contain("Se anotará la misma vigencia en 2 acreditaciones validadas.");
        anotadas.Should().BeEmpty("abrir no anota");

        await dialogo.QuerySelector("select")!.ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoVigenciaEnPlataforma.NoVenceAqui) });
        var consultasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => anotadas.Select(c => c.AcreditacionId).Should().BeEquivalentTo([e.AcreditacionNorte, e.AcreditacionSur]));
        anotadas.Should().OnlyContain(c => c.Vigencia == VigenciaEnPlataforma.NoVenceAqui);
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count().Should().BeGreaterThan(consultasAntes,
            "tras anotar, el listado se recarga"));
        Toasts().Should().Contain(t => t.TextoAccion == "Deshacer");
    }

    [Fact]
    public async Task Anotar_vigencia_desaparece_al_plegar_el_grupo_porque_no_se_anota_lo_que_no_se_ve()
    {
        // Un Documento sin vigencia en dos Plataformas CAE: plegar una no lo deselecciona, porque sigue visible en la otra.
        var documento = DocumentoDeFila();
        var mediador = new MediadorControlado
        {
            Documentos = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? [EnPlataforma(documento, NalandaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte"),
                   EnPlataforma(documento, CtaimaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte")]
                : [documento],
            ResumenesDeGrupo = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? new Dictionary<string, ResumenDeGrupo>
                {
                    [NalandaId.ToString()] = ResumenPlataforma("Nalanda", 1, null, (EstadoEnPlataformaFiltro.SinVigencia, 1)),
                    [CtaimaId.ToString()] = ResumenPlataforma("Ctaima", 1, null, (EstadoEnPlataformaFiltro.SinVigencia, 1)),
                }
                : null,
        };
        var (cut, _) = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().HaveCount(2));
        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find(".documentos-tabla-visible tbody input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".documentos-anotar-vigencia").Should().HaveCount(2);

        await CabeceraDeVinculo(cut, CtaimaId.ToString()).QuerySelector("button.grupo-lista-cabecera")!.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => CabeceraDeVinculo(cut, CtaimaId.ToString()).QuerySelector("button.grupo-lista-cabecera")!
            .GetAttribute("aria-expanded").Should().Be("false"));
        CabeceraDeVinculo(cut, CtaimaId.ToString()).QuerySelector(".documentos-anotar-vigencia").Should().BeNull(
            "con el grupo plegado sus filas no se ven, y la acción no anota acreditaciones ocultas");
        CabeceraDeVinculo(cut, NalandaId.ToString()).QuerySelector(".documentos-anotar-vigencia")!.TextContent.Trim()
            .Should().Be("Anotar vigencia (1)", "el Documento sigue seleccionado en el grupo abierto");
    }

    [Fact]
    public async Task Sin_agrupar_la_tabla_es_un_solo_bloque_y_conserva_el_orden_entre_Ambitos()
    {
        var primero = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Primero Trabajador", Ambito = AmbitoAplicacion.Trabajador };
        var segundo = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Segundo Cliente", Ambito = AmbitoAplicacion.Cliente };
        var tercero = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Tercero Trabajador", Ambito = AmbitoAplicacion.Trabajador };
        var (cut, _) = Renderizar(new MediadorControlado { Documentos = _ => [primero, segundo, tercero] });
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        cut.FindAll(".documentos-tabla-visible tbody").Should().ContainSingle("sin agrupar no se parte la tabla por Ámbito");
        cut.FindAll(".documentos-tabla-visible tbody tr:has(button.enlace-nombre-fila)")
            .Select(f => f.TextContent).Should().SatisfyRespectively(
                f => f.Should().Contain("Primero Trabajador"),
                f => f.Should().Contain("Segundo Cliente"),
                f => f.Should().Contain("Tercero Trabajador"));
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Sin_escritura_no_hay_seleccion_ni_Anotar_vigencia(string rol)
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().HaveCount(3));
        cut.FindAll(".documentos-tabla-visible tbody input[type=checkbox]").Should().BeEmpty();
        cut.FindAll(".documentos-anotar-vigencia").Should().BeEmpty();
        ResumenDeVinculo(CabeceraDeVinculo(cut, NalandaId.ToString())).Should().Equal("1 rechazado", "4 sin vigencia");
    }

    // ------------------------------------------------------------- pie de la vista agrupada (Codex r1, MEDIO)

    /// <summary>
    /// Agrupada, la consulta pide siempre la página 1 con el tope de filas: un paginador con más páginas repetiría la 1. Como
    /// en la maqueta, la vista agrupada no pagina: el pie dice cuántas filas y Documentos hay, sin flechas ni tamaño de
    /// página, y sin agrupar vuelve el paginador.
    /// </summary>
    [Theory]
    [InlineData("Por Plataforma CAE", "Mostrando 5 filas de 4 documentos")]
    [InlineData("Por Cliente", "Mostrando 3 filas de 3 documentos")]
    [InlineData("Por ámbito", "Mostrando 4 documentos")]
    public async Task Agrupada_no_hay_paginador_y_el_pie_cuenta_filas_y_documentos(string agrupacion, string pie)
    {
        var e = ConVinculos();
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        cut.FindAll(".paginador-opciones-tamano").Should().NotBeEmpty("control: sin agrupar hay paginador");

        await Agrupar(cut, agrupacion);

        cut.WaitForAssertion(() => TextoDelPie(cut).Should().Be(pie));
        cut.FindAll(".paginador-opciones-tamano").Should().BeEmpty("agrupada no se elige tamaño de página");
        cut.FindAll("[data-testid=documentos-aviso-tope]").Should().BeEmpty("por debajo del tope no hay nada que avisar");
        e.Mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Last().Pagina.Should().Be(1);

        await Agrupar(cut, "Sin agrupar");

        cut.WaitForAssertion(() => cut.FindAll(".paginador-opciones-tamano").Should().NotBeEmpty());
        TextoDelPie(cut).Should().NotBe(pie);
    }

    private static string TextoDelPie(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.Find(".paginador-pie-listado .paginador-texto").TextContent.Trim();

    [Fact]
    public async Task Agrupada_por_encima_del_tope_avisa_de_que_hay_mas_documentos_de_los_que_se_ven()
    {
        var e = ConVinculos();
        // El servidor dice que el universo filtrado tiene más Documentos que el tope de la vista agrupada.
        e.Mediador.Interceptar = p => p is ObtenerDocumentosQuery { AgruparPor: AgrupacionDocumentos.PlataformaCae } q
            ? Task.FromResult<object?>(e.Mediador.Pagina(q) with { TotalElementos = AgrupacionDeLista.TamanoPaginaAgrupada + 1200 })
            : null;
        var (cut, _) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() =>
        {
            var aviso = cut.Find("[data-testid=documentos-aviso-tope]").TextContent;
            aviso.Should().MatchRegex(@"5[.,]000").And.MatchRegex(@"6[.,]200").And.Contain("afina los filtros");
        });
        cut.FindAll(".paginador-opciones-tamano").Should().BeEmpty("tampoco con más del tope aparece un paginador que repita la página 1");
    }

    private static Result<ResultadoAnotacionAcreditacionDto> Anotar(List<ConfirmarVigenciaAcreditacionCommand> anotadas,
        ConfirmarVigenciaAcreditacionCommand comando)
    {
        anotadas.Add(comando);
        return Result.Exito(new ResultadoAnotacionAcreditacionDto(
            comando.AcreditacionId, EstadoAcreditacion.Aceptada, VigenciaEnPlataforma.SinConfirmar, Guid.NewGuid()));
    }

    private static IElement CasillaDe(IRenderedComponent<PaginaDocumentos> cut, DocumentoListaDto documento) =>
        cut.FindAll(".documentos-tabla-visible tbody tr:has(input[type=checkbox])")
            .First(f => f.TextContent.Contains(documento.PropietarioNombre))
            .QuerySelector("input[type=checkbox]")!;

    // ------------------------------------------------------------- filtro «Estado en la plataforma»

    [Fact]
    public async Task El_filtro_Estado_en_la_plataforma_viaja_en_la_consulta_y_en_la_URL_y_su_chip_lo_quita()
    {
        var e = ConVinculos();
        var (cut, mediador) = Renderizar(e.Mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma.Should().BeNull();

        await ElegirPastillaDocumentoFase1(cut, "Estado en la plataforma", "Rechazado");

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma.Should().Equal(EstadoEnPlataformaFiltro.Rechazado));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("EstadoPlataforma=Rechazado");
        var chip = cut.FindAll(".chip-filtro").Single(c => c.TextContent.Contains("Estado en la plataforma: Rechazado"));
        chip.QuerySelector("button[aria-label='Quitar filtro de estado en la plataforma']").Should().NotBeNull();

        await chip.QuerySelector("button[aria-label='Quitar filtro de estado en la plataforma']")!.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma.Should().BeNull());
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("EstadoPlataforma");
        cut.FindAll(".chip-filtro").Should().NotContain(c => c.TextContent.Contains("Estado en la plataforma"));
    }

    [Theory]
    [InlineData("PendienteDeSubir", EstadoEnPlataformaFiltro.PendienteDeSubir)]
    [InlineData("Rechazado", EstadoEnPlataformaFiltro.Rechazado)]
    [InlineData("SinVigencia", EstadoEnPlataformaFiltro.SinVigencia)]
    [InlineData("Inventado", null)]
    [InlineData("7", null)]
    [InlineData("1", null)]
    [InlineData("PendienteDeSubir,Rechazado", null)]
    [InlineData("rechazado", null)]
    public void El_enlace_con_EstadoPlataforma_pide_ese_estado_y_descarta_lo_que_no_es_un_estado(string valor, EstadoEnPlataformaFiltro? esperado)
    {
        var (cut, mediador) = Renderizar(ConVinculos().Mediador, url: $"documentos?EstadoPlataforma={valor}");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());

        var pedidos = UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma;
        if (esperado is null)
            pedidos.Should().BeNull();
        else
            pedidos.Should().Equal(esperado.Value);
        cut.FindAll(".chip-filtro").Any(c => c.TextContent.Contains("Estado en la plataforma")).Should().Be(esperado is not null);
    }

    [Fact]
    public async Task Limpiar_filtros_quita_tambien_Estado_en_la_plataforma()
    {
        var (cut, mediador) = Renderizar(ConVinculos().Mediador, url: "documentos?EstadoPlataforma=SinVigencia");
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma.Should().Equal(EstadoEnPlataformaFiltro.SinVigencia));

        await cut.InvokeAsync(() => cut.FindComponent<BarraFiltros>().Instance.OnLimpiarTodo.InvokeAsync());

        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma.Should().BeNull());
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("EstadoPlataforma");
    }

    // ------------------------------------------------------------- el rol Cliente (decisión de Chris del 2026-11-12, opción B)

    /// <summary>
    /// El rol Cliente ve la columna «Estado en la plataforma» de las acreditaciones de sus Centros visibles, y el filtro,
    /// pero no el motivo literal de rechazo. El servidor ya no se lo manda (lo fija la prueba de Application); aquí el doble
    /// lo manda igualmente y la página no lo pinta.
    /// </summary>
    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("GestorCae", true)]
    [InlineData("Consulta", true)]
    [InlineData("Cliente", false)]
    public async Task El_estado_en_la_plataforma_se_ve_con_cualquier_rol_y_el_motivo_de_rechazo_no_con_el_rol_Cliente(string rol, bool veMotivo)
    {
        var e = ConVinculos();
        var (cut, mediador) = Renderizar(e.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));

        await Agrupar(cut, "Por Plataforma CAE");

        cut.WaitForAssertion(() => cut.FindAll("tbody .documentos-en-plataforma").Should().HaveCount(4));
        cut.FindAll(".documentos-tabla-visible thead th").Select(h => h.TextContent.Trim()).Should().Contain("Estado en la plataforma");
        var rechazado = cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Single(f => f.TextContent.Contains("Ana Ruiz Pardo"))
            .QuerySelector(".documentos-celda-en-plataforma")!;
        rechazado.TextContent.Should().Contain("Rechazad", "el estado se ve con cualquier rol");
        rechazado.QuerySelectorAll(".documentos-en-plataforma-motivo").Should().HaveCount(veMotivo ? 1 : 0);
        cut.Markup.Contains("Ilegible").Should().Be(veMotivo, "el motivo no se pinta en ningún sitio para el rol Cliente");
        cut.FindAll("tbody .documentos-en-plataforma").Count(c => c.TextContent.Contains("Sin vigencia")).Should().Be(2);

        await ElegirPastillaDocumentoFase1(cut, "Estado en la plataforma", "Rechazado");
        // El filtro solo muestra estados: también lo tiene el rol Cliente.
        cut.WaitForAssertion(() => UltimaConsultaDeDocumentos(mediador).EstadosEnPlataforma.Should().Equal(EstadoEnPlataformaFiltro.Rechazado));
    }

    // ------------------------------------------------------------- refresco tras guardar en la vista rápida

    /// <summary>
    /// Un Documento aceptado «Sin vigencia» en dos Plataformas CAE, seleccionado: cada grupo ofrece «Anotar vigencia (1)».
    /// Guardar en la vista rápida una versión nueva devuelve sus acreditaciones a «Pendiente de subir». Agrupada por
    /// vínculo, la página recarga la lista entera (grupos, estados y recuentos del servidor) en vez de sustituir la fila
    /// conservando su grupo viejo: el Documento sigue en sus dos grupos, con el estado nuevo, y «Anotar vigencia» ya no
    /// cuenta lo que dejó de estar «Sin vigencia», ni aunque se vuelva a seleccionar.
    /// </summary>
    [Fact]
    public async Task Tras_guardar_en_la_vista_rapida_agrupada_por_Plataforma_CAE_recarga_y_Anotar_vigencia_no_cuenta_lo_devuelto_a_pendiente()
    {
        var documento = DocumentoDeFila();
        var estado = EstadoAcreditacion.Aceptada;
        var mediador = new MediadorControlado
        {
            Documentos = q => q.DocumentoId is not null
                ? [documento]
                : q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                    ? [EnPlataforma(documento, NalandaId, Guid.NewGuid(), estado, "Cliente Norte"),
                       EnPlataforma(documento, CtaimaId, Guid.NewGuid(), estado, "Cliente Norte")]
                    : [documento],
            ResumenesDeGrupo = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? new Dictionary<string, ResumenDeGrupo>
                {
                    [NalandaId.ToString()] = ResumenPlataforma("Nalanda", 1, null, Recuento(estado)),
                    [CtaimaId.ToString()] = ResumenPlataforma("Ctaima", 1, null, Recuento(estado)),
                }
                : null,
        };
        var (cut, _) = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => cut.FindAll("tr.documentos-cabecera-grupo-vinculo").Should().HaveCount(2));
        await cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find(".documentos-tabla-visible tbody input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".documentos-anotar-vigencia").Select(b => b.TextContent.Trim())
            .Should().Equal(["Anotar vigencia (1)", "Anotar vigencia (1)"], "punto de partida: una sin vigencia en cada grupo");
        var paginasAntes = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count(q => q.DocumentoId is null);

        estado = EstadoAcreditacion.PendienteDeSubir;
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Documento, documento.Id);

        cut.WaitForAssertion(() => cut.FindAll("tbody .documentos-en-plataforma").Should().HaveCount(2)
            .And.OnlyContain(c => c.TextContent.Contains("Pendiente") && !c.TextContent.Contains("Sin vigencia"),
                "las dos filas enseñan el estado nuevo, no el que arrastraría el grupo viejo"));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count(q => q.DocumentoId is null).Should().BeGreaterThan(paginasAntes,
            "agrupada por vínculo, el aviso recarga la lista");
        UltimaConsultaDeDocumentos(mediador).AgruparPor.Should().Be(AgrupacionDocumentos.PlataformaCae);
        NombresDeVinculo(cut).Should().Equal(["Ctaima", "Nalanda"], "el Documento sigue en sus dos grupos y no cae en «Sin Plataforma CAE»");
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Should().HaveCount(2);
        ResumenDeVinculo(CabeceraDeVinculo(cut, NalandaId.ToString())).Should().Equal("1 pendiente de subir");
        cut.FindAll(".documentos-anotar-vigencia").Should().BeEmpty();

        await cut.Find(".documentos-tabla-visible tbody input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".documentos-anotar-vigencia").Should().BeEmpty(
            "seleccionado otra vez, ya no hay acreditaciones «Sin vigencia» que anotar");
    }

    private static (EstadoEnPlataformaFiltro, int) Recuento(EstadoAcreditacion estado) =>
        (estado == EstadoAcreditacion.Aceptada ? EstadoEnPlataformaFiltro.SinVigencia : EstadoEnPlataformaFiltro.PendienteDeSubir, 1);

    // ------------------------------------------------------------- j/k con un Documento en varias filas

    /// <summary>
    /// Un Documento en dos Plataformas CAE ocupa dos filas seguidas (Ctaima y Nalanda) y otro va detrás en Nalanda. j/k
    /// recorren Documentos, no filas: la segunda j pasa al otro Documento en vez de quedarse en la segunda fila del primero.
    /// </summary>
    [Fact]
    public async Task J_y_k_recorren_Documentos_aunque_un_Documento_ocupe_varias_filas()
    {
        var repetido = DocumentoDeFila();
        var otro = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Ana Ruiz Pardo" };
        var mediador = new MediadorControlado
        {
            Documentos = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? [EnPlataforma(repetido, CtaimaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte"),
                   EnPlataforma(repetido, NalandaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte"),
                   EnPlataforma(otro, NalandaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte")]
                : [repetido, otro],
            ResumenesDeGrupo = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? new Dictionary<string, ResumenDeGrupo>
                {
                    [NalandaId.ToString()] = ResumenPlataforma("Nalanda", 2, null),
                    [CtaimaId.ToString()] = ResumenPlataforma("Ctaima", 1, null),
                }
                : null,
        };
        var (cut, _) = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => NombresDeVinculo(cut).Should().Equal("Ctaima", "Nalanda"));
        List<string> Enfocadas() => cut.FindAll("tbody tr.fila-enfocada").Select(f => f.TextContent).ToList();
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
        Enfocadas().Should().HaveCount(2, "el Documento enfocado se marca en sus dos filas")
            .And.OnlyContain(f => f.Contains(repetido.PropietarioNombre) && !f.Contains("Ana Ruiz Pardo"));

        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
        Enfocadas().Should().ContainSingle("la segunda j pasa al otro Documento, no a la segunda fila del primero")
            .Which.Should().Contain("Ana Ruiz Pardo");

        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("k"));
        Enfocadas().Should().HaveCount(2).And.OnlyContain(f => f.Contains(repetido.PropietarioNombre));
    }

    /// <summary>
    /// La página llega en el orden del servidor (A en Nalanda, B en Ctaima, C en Nalanda), pero la tabla pinta los grupos
    /// por nombre: Ctaima {B} y luego Nalanda {A, C}. j baja por la tabla en el orden en que se ve, B, A, C, sin saltar
    /// de Nalanda a Ctaima y de vuelta.
    /// </summary>
    [Fact]
    public async Task J_sigue_el_orden_en_que_se_pintan_los_grupos_y_no_el_de_la_pagina()
    {
        var a = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Ana Ruiz Pardo" };
        var b = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Luis Mora Gil" };
        var c = DocumentoDeFila() with { Id = Guid.NewGuid(), PropietarioNombre = "Eva Soto Paz" };
        var mediador = new MediadorControlado
        {
            Documentos = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? [EnPlataforma(a, NalandaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte"),
                   EnPlataforma(b, CtaimaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte"),
                   EnPlataforma(c, NalandaId, Guid.NewGuid(), EstadoAcreditacion.Aceptada, "Cliente Norte")]
                : [a, b, c],
            ResumenesDeGrupo = q => q.AgruparPor == AgrupacionDocumentos.PlataformaCae
                ? new Dictionary<string, ResumenDeGrupo>
                {
                    [NalandaId.ToString()] = ResumenPlataforma("Nalanda", 2, null),
                    [CtaimaId.ToString()] = ResumenPlataforma("Ctaima", 1, null),
                }
                : null,
        };
        var (cut, _) = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Find("tbody button.enlace-nombre-fila"));
        await Agrupar(cut, "Por Plataforma CAE");
        cut.WaitForAssertion(() => NombresDeVinculo(cut).Should().Equal("Ctaima", "Nalanda"));
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)").Select(f => f.TextContent).Should().SatisfyRespectively(
            f => f.Should().Contain("Luis Mora Gil"), f => f.Should().Contain("Ana Ruiz Pardo"), f => f.Should().Contain("Eva Soto Paz"));
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        var enfocados = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
            enfocados.Add(cut.Find("tbody tr.fila-enfocada").TextContent);
        }

        enfocados.Should().SatisfyRespectively(
            f => f.Should().Contain("Luis Mora Gil"), f => f.Should().Contain("Ana Ruiz Pardo"), f => f.Should().Contain("Eva Soto Paz"));
    }
}
