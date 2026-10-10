using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Listado de Empresas, cierre de la maqueta de listados (línea I): la columna «Estado documental» con el
/// motivo bajo la pastilla y su desglose corregible sin salir del listado, y las mismas incidencias en la fila
/// desplegada, antes de los Clientes de la Empresa.
///
/// <para>
/// El doble del mediador (<c>MediatorFalso</c>) hace con el desglose lo mismo que el handler: sin
/// <c>ConDesgloseDocumental</c> devuelve las filas sin incidencias. El contenido del desglose y su alcance se
/// miden bajo RLS en <c>DesgloseDocumentalDeEmpresasBajoRlsTests</c>.
/// </para>
///
/// <para>
/// Lo que estos tests NO observan: el cuarto argumento de la corrección (el Id de la Empresa de la fila). Una
/// incidencia del desglose es siempre un documento que existe, y con Id de documento el formulario abre su
/// renovación y lee de él a quién pertenece: el Id de Empresa no llega a ninguna petición. Lo que sí se fija
/// es que se abre el documento de la línea pulsada, en la fila pulsada.
/// </para>
/// </summary>
public partial class EmpresasListaGen2Tests
{
    private static IncidenciaDocumentalDto IncidenciaDe(string tipo, EstadoDocumento estado, int? diasHastaVencer = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tipo, estado,
            diasHastaVencer is { } dias ? DiaDeNegocio.Hoy().AddDays(dias) : null);

    private static EmpresaListaDto EmpresaCon(string razonSocial, EstadoDocumento? estado, params IncidenciaDocumentalDto[] incidencias) =>
        Empresa(razonSocial, estado: estado) with { Incidencias = incidencias };

    /// <summary>Monta la página con los servicios que pide el formulario de corrección, como el rol indicado.</summary>
    private IRenderedComponent<Empresas> RenderizarComo(MediatorFalso mediador, string rol = Roles.GestorCae, string url = "empresas")
    {
        this.ConRolDeEscritura(rol);
        this.ConServiciosDelFormularioDeDocumento();
        return Renderizar(mediador, url);
    }

    private static IElement CeldaDeEstado(IRenderedComponent<Empresas> cut, string razonSocial) =>
        Tarjeta(cut, razonSocial).QuerySelector(".tarjeta-fila-acordeon-cabecera .celda-estado-empresa")!;

    private static string? MotivoDe(IRenderedComponent<Empresas> cut, string razonSocial) =>
        CeldaDeEstado(cut, razonSocial).QuerySelector(".estado-fila-motivo .motivo-incidencias-texto")?.TextContent.Trim();

    private static Task DesplegarAsync(IRenderedComponent<Empresas> cut, string razonSocial) =>
        Tarjeta(cut, razonSocial).QuerySelector(".boton-expandir-fila")!.ClickAsync(new MouseEventArgs());

    private static Task CorregirAsync(IRenderedComponent<Empresas> cut) =>
        cut.InvokeAsync(() => cut.FindComponent<CorreccionIncidenciaDocumental>().Instance.OnCorregida.InvokeAsync());

    private static IElement CasillaDe(IRenderedComponent<Empresas> cut, string razonSocial) =>
        Tarjeta(cut, razonSocial).QuerySelector("input[type=checkbox]")!;

    private static DocumentoDetalleDto DocumentoDe(IncidenciaDocumentalDto incidencia, string propietario, Guid version) => new(
        incidencia.DocumentoId, AmbitoAplicacion.Empresa, propietario, incidencia.TipoDocumentoNombre,
        TipoDocumentoAplicaVencimientoAutomatico: true, DiaDeNegocio.Hoy().AddYears(-1), incidencia.FechaVencimiento,
        EstadoVigenciaDocumento.VenceEnFecha, ArchivoUrl: null, Comentarios: null, null, null, null, null,
        version, PerfilDocumentoOficial.Ninguno, EmpresaId: null);

    // --- Columna de estado: pastilla y motivo --------------------------------------------------

    [Fact]
    public void Con_una_incidencia_el_motivo_bajo_la_pastilla_dice_el_documento_que_la_causa()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -10)),
                EmpresaCon("Montajes Ebro S.L.", EstadoDocumento.Urgente, IncidenciaDe("Certificado de Hacienda", EstadoDocumento.Urgente, 11))
            }
        };
        var cut = RenderizarComo(mediador);

        CeldaDeEstado(cut, "Aislamientos Nervión S.L.").QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido", "la pastilla se pinta siempre");
        MotivoDe(cut, "Aislamientos Nervión S.L.").Should().Be("Seguro de responsabilidad civil", "el motivo no repite el texto de la pastilla");
        CeldaDeEstado(cut, "Montajes Ebro S.L.").QuerySelector(".badge")!.TextContent.Trim().Should().Be("Por vencer");
        MotivoDe(cut, "Montajes Ebro S.L.").Should().Be("Certificado de Hacienda · Caduca en 11 días");
        UltimaConsulta(mediador).ConDesgloseDocumental.Should().BeTrue("la carga de página es quien pide el desglose que pinta");
    }

    [Fact]
    public void Con_varias_incidencias_el_motivo_las_cuenta_por_clase_y_la_ventana_lleva_una_linea_por_documento()
    {
        var cut = RenderizarComo(new MediatorFalso
        {
            Almacen =
            {
                EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido,
                    IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12),
                    IncidenciaDe("Certificado de Hacienda", EstadoDocumento.Vencido, -3),
                    IncidenciaDe("Modalidad preventiva", EstadoDocumento.Proximo, 25))
            }
        });

        var celda = CeldaDeEstado(cut, "Aislamientos Nervión S.L.");
        celda.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido");
        MotivoDe(cut, "Aislamientos Nervión S.L.").Should().Be("2 vencidos · 1 por vencer");
        celda.QuerySelectorAll(".ventana-contexto-panel button.ventana-contexto-elemento .motivo-incidencias-tipo")
            .Select(e => e.TextContent.Trim())
            .Should().Equal("Seguro de responsabilidad civil", "Certificado de Hacienda", "Modalidad preventiva");
    }

    [Fact]
    public void Sin_incidencias_la_celda_lleva_el_estado_y_ningun_motivo()
    {
        var cut = RenderizarComo(new MediatorFalso
        {
            Almacen =
            {
                EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -10)),
                Empresa("Montajes Ebro S.L.", estado: EstadoDocumento.Vigente),
                Empresa("Talleres Berriz S. Coop.")
            }
        });

        MotivoDe(cut, "Aislamientos Nervión S.L.").Should().Be("Seguro de responsabilidad civil", "control positivo: la fila con incidencias sí lleva motivo");
        foreach (var razonSocial in new[] { "Montajes Ebro S.L.", "Talleres Berriz S. Coop." })
        {
            var celda = CeldaDeEstado(cut, razonSocial);
            celda.QuerySelector(".estado-fila")!.TextContent.Trim().Should().NotBeEmpty("el estado se pinta siempre");
            celda.QuerySelectorAll(".estado-fila-motivo").Should().BeEmpty("sin incidencias no hay nada que explicar");
            celda.QuerySelectorAll(".ventana-contexto").Should().BeEmpty();
        }
    }

    [Fact]
    public void Quien_solo_consulta_ve_el_desglose_sin_botones_en_la_celda()
    {
        var cut = RenderizarComo(
            new MediatorFalso
            {
                Almacen =
                {
                    EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido,
                        IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12),
                        IncidenciaDe("Modalidad preventiva", EstadoDocumento.SinConfirmar))
                }
            },
            rol: Roles.Consulta);

        var celda = CeldaDeEstado(cut, "Aislamientos Nervión S.L.");
        MotivoDe(cut, "Aislamientos Nervión S.L.").Should().Be("1 vencido · 1 sin confirmar", "control positivo: el motivo se ve igual");
        celda.QuerySelectorAll("button").Should().BeEmpty("no se ofrece un formulario que el comando va a denegar");
        celda.QuerySelectorAll(".ventana-linea").Should().HaveCount(2, "control positivo: el desglose se sigue viendo");
    }

    // --- Corrección en la misma pantalla -------------------------------------------------------

    [Fact]
    public async Task Pulsar_una_incidencia_de_la_ventana_abre_la_correccion_de_ese_documento_sin_recargar_ni_abrir_la_vista_rapida_ni_desplegar()
    {
        var deOtraFila = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -30);
        var vencida = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12);
        var urgente = IncidenciaDe("Certificado de Hacienda", EstadoDocumento.Urgente, 5);
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, deOtraFila),
                EmpresaCon("Montajes Ebro S.L.", EstadoDocumento.Vencido, vencida, urgente)
            }
        };
        mediador.Documento = DocumentoDe(urgente, "Montajes Ebro S.L.", Guid.NewGuid());
        var cut = RenderizarComo(mediador);
        var consultasDeListaAntes = ConsultasDeLista(mediador);
        var boton = CeldaDeEstado(cut, "Montajes Ebro S.L.").QuerySelectorAll("button.ventana-contexto-elemento")[1];
        boton.Closest(".ventana-contexto-panel").Should().NotBeNull("la incidencia se pulsa dentro de la ventana del motivo");

        await boton.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(urgente.DocumentoId, "se corrige el documento de la línea pulsada de esa fila, no el primero ni el de otra");
        cut.FindComponent<DrawerGestionDocumento>().Markup.Should().Contain("Renovar documento");
        ConsultasDeLista(mediador).Should().Be(consultasDeListaAntes, "pulsar no recarga");
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull("ni abre la vista rápida de la fila");
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty("ni despliega la fila");
        ConsultasDeClientes(mediador).Should().Be(0);
    }

    [Fact]
    public async Task Al_guardar_la_correccion_se_renueva_ese_documento_y_se_relee_la_pagina_conservando_seleccion_y_filas_desplegadas()
    {
        var vencida = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12);
        var version = Guid.NewGuid();
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, vencida);
        var ebro = Empresa("Montajes Ebro S.L.", estado: EstadoDocumento.Vigente);
        var mediador = new MediatorFalso { Almacen = { nervion, ebro }, Documento = DocumentoDe(vencida, "Aislamientos Nervión S.L.", version) };
        mediador.ClientesDe[ebro.Id] = [ClienteEmpresarial("Orion Cliente S.L.", "B10000016")];
        var cut = RenderizarComo(mediador);
        await AlternarSeleccionMultiple(cut);
        await CasillaDe(cut, "Montajes Ebro S.L.").ChangeAsync(new ChangeEventArgs { Value = true });
        await DesplegarAsync(cut, "Montajes Ebro S.L.");
        cut.WaitForAssertion(() => Tarjeta(cut, "Montajes Ebro S.L.").TextContent.Should().Contain("Orion Cliente S.L."));
        var pregunta = UltimaConsulta(mediador);
        var consultasAntes = ConsultasDeLista(mediador);
        var consultasDeClientesAntes = ConsultasDeClientes(mediador);

        await CeldaDeEstado(cut, "Aislamientos Nervión S.L.").QuerySelector("button.ventana-contexto-elemento")!.ClickAsync(new MouseEventArgs());
        var formulario = cut.FindComponent<DrawerGestionDocumento>();
        // La corrección cambia lo guardado: la Empresa pasa a estar al día.
        mediador.Almacen[mediador.Almacen.FindIndex(e => e.Id == nervion.Id)] = Empresa("Aislamientos Nervión S.L.", estado: EstadoDocumento.Vigente) with { Id = nervion.Id };
        await formulario.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        var renovacion = mediador.Enviadas.OfType<RenovarDocumentoCommand>().Should().ContainSingle().Subject;
        renovacion.Id.Should().Be(vencida.DocumentoId);
        renovacion.Version.Should().Be(version);
        cut.WaitForAssertion(() => ConsultasDeLista(mediador).Should().Be(consultasAntes + 1,
            "el estado y el motivo de la fila cambian al corregir: la página se relee en sitio"));
        UltimaConsulta(mediador).Should().Be(pregunta, "con los mismos filtros, página y tamaño");
        cut.WaitForAssertion(() => MotivoDe(cut, "Aislamientos Nervión S.L.").Should().BeNull("la fila corregida ya no tiene incidencias"));
        CasillaDe(cut, "Montajes Ebro S.L.").HasAttribute("checked").Should().BeTrue(
            "corregir un documento no es cambiar de lista: lo marcado sigue marcado");
        Tarjeta(cut, "Montajes Ebro S.L.").QuerySelector(".tarjeta-fila-acordeon-contenido")!.TextContent
            .Should().Contain("Orion Cliente S.L.", "y lo desplegado sigue desplegado, con sus Clientes");
        ConsultasDeClientes(mediador).Should().Be(consultasDeClientesAntes, "los Clientes ya cargados no se vuelven a pedir");
    }

    /// <summary>
    /// Conservar la selección vale para el refresco tras corregir y para nada más: una recarga posterior que
    /// repite la misma pregunta sin venir de una corrección (aquí, el alta de otra Empresa) la suelta.
    /// </summary>
    [Fact]
    public async Task La_seleccion_conservada_tras_corregir_no_sobrevive_a_una_recarga_posterior_con_la_misma_pregunta()
    {
        var mediador = new MediatorFalso { Almacen = { Empresa("Aislamientos Nervión S.L."), Empresa("Montajes Ebro S.L.") } };
        var cut = RenderizarComo(mediador);
        await AlternarSeleccionMultiple(cut);
        await CasillaDe(cut, "Aislamientos Nervión S.L.").ChangeAsync(new ChangeEventArgs { Value = true });
        var pregunta = UltimaConsulta(mediador);

        await CorregirAsync(cut);
        cut.WaitForAssertion(() => CasillaDe(cut, "Aislamientos Nervión S.L.").HasAttribute("checked").Should().BeTrue(
            "control positivo: la recarga tras corregir conserva lo marcado"));
        var consultasTrasCorregir = ConsultasDeLista(mediador);

        // Segunda recarga, con la misma pregunta, que no viene de una corrección: el alta de otra Empresa.
        await cut.FindAll("header.cabecera-pagina .acciones-cabecera button").Single(b => b.TextContent.Trim() == "+ Nueva empresa").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".drawer-pie button.boton-espera-boton").Single().ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearEmpresaCommand>().Should().ContainSingle("control: el alta se guardó");
        cut.WaitForAssertion(() => ConsultasDeLista(mediador).Should().BeGreaterThan(consultasTrasCorregir, "control: el alta recargó la lista"));
        UltimaConsulta(mediador).Should().Be(pregunta, "control: con la misma pregunta que el refresco tras corregir");
        cut.WaitForAssertion(() => cut.FindAll(".barra-acciones-lote").Should().BeEmpty(
            "una recarga que no viene de corregir limpia la selección aunque repita la pregunta"));
    }

    [Fact]
    public async Task La_fila_refrescada_tras_guardar_en_la_vista_rapida_pide_el_desglose_y_conserva_el_motivo()
    {
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -10));
        var mediador = new MediatorFalso { Almacen = { nervion } };
        var cut = RenderizarComo(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await AvisarGuardadoAsync(cut, EntidadWorkspace.Empresa, nervion.Id);

        cut.WaitForAssertion(() => ConsultasDeLista(mediador).Should().Be(consultasAntes + 1));
        var porId = UltimaConsulta(mediador);
        porId.EmpresaId.Should().Be(nervion.Id);
        porId.ConDesgloseDocumental.Should().BeTrue("el refresco por id también pinta el motivo: sin pedirlo la fila lo perdería");
        MotivoDe(cut, "Aislamientos Nervión S.L.").Should().Be("Seguro de responsabilidad civil");
    }

    // --- Fila desplegada: incidencias y Clientes -----------------------------------------------

    [Fact]
    public async Task La_fila_desplegada_lleva_las_incidencias_antes_de_los_Clientes_y_conserva_los_Clientes()
    {
        var vencida = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12);
        var sinConfirmar = IncidenciaDe("Modalidad preventiva", EstadoDocumento.SinConfirmar);
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, vencida, sinConfirmar);
        var mediador = new MediatorFalso { Almacen = { nervion }, Documento = DocumentoDe(sinConfirmar, "Aislamientos Nervión S.L.", Guid.NewGuid()) };
        mediador.ClientesDe[nervion.Id] = [ClienteEmpresarial("Orion Cliente S.L.", "B10000016")];
        var cut = RenderizarComo(mediador);

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        cut.WaitForAssertion(() => cut.FindAll(".tarjeta-fila-acordeon-contenido li").Should().ContainSingle());
        var contenido = cut.Find(".tarjeta-fila-acordeon-contenido");
        var incidencias = contenido.QuerySelector("[data-pieza='incidencias-fila']")!;
        incidencias.QuerySelector(".titulo-incidencias-empresa")!.TextContent.Trim().Should().NotBeEmpty();
        incidencias.QuerySelectorAll("button.ventana-contexto-elemento .motivo-incidencias-tipo").Select(e => e.TextContent.Trim())
            .Should().Equal(["Seguro de responsabilidad civil", "Modalidad preventiva"], "las mismas líneas que la ventana del motivo, en su orden");
        contenido.QuerySelector(".titulo-clientes-empresa")!.TextContent.Trim().Should().Be("Clientes");
        contenido.QuerySelector("li")!.TextContent.Should().Contain("Orion Cliente S.L.");
        (incidencias.CompareDocumentPosition(contenido.QuerySelector(".titulo-clientes-empresa")!) & DocumentPositions.Following)
            .Should().Be(DocumentPositions.Following, "las incidencias van antes que los Clientes");

        // Misma corrección que la ventana.
        await incidencias.QuerySelectorAll("button.ventana-contexto-elemento")[1].ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(sinConfirmar.DocumentoId);
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull("corregir desde la fila desplegada tampoco abre la vista rápida");
    }

    [Fact]
    public async Task Sin_incidencias_la_fila_desplegada_solo_lleva_los_Clientes()
    {
        var ebro = Empresa("Montajes Ebro S.L.", estado: EstadoDocumento.Vigente);
        var mediador = new MediatorFalso { Almacen = { ebro } };
        mediador.ClientesDe[ebro.Id] = [ClienteEmpresarial("Orion Cliente S.L.", "B10000016")];
        var cut = RenderizarComo(mediador);

        await DesplegarAsync(cut, "Montajes Ebro S.L.");

        cut.WaitForAssertion(() => cut.FindAll(".tarjeta-fila-acordeon-contenido li").Should().ContainSingle());
        cut.FindAll("[data-pieza='incidencias-fila']").Should().BeEmpty();
        cut.FindAll(".titulo-incidencias-empresa").Should().BeEmpty();
    }

    [Fact]
    public async Task Quien_solo_consulta_ve_las_incidencias_de_la_fila_desplegada_sin_botones()
    {
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido,
            IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Modalidad preventiva", EstadoDocumento.SinConfirmar));
        var cut = RenderizarComo(new MediatorFalso { Almacen = { nervion } }, rol: Roles.Consulta);

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        var incidencias = cut.Find(".tarjeta-fila-acordeon-contenido [data-pieza='incidencias-fila']");
        incidencias.QuerySelectorAll(".ventana-linea").Should().HaveCount(2, "control positivo: las líneas se ven");
        incidencias.QuerySelectorAll("button").Should().BeEmpty("no se ofrece un formulario que el comando va a denegar");
    }
}
