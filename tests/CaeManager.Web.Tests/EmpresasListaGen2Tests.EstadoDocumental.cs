using CaeManager.Web.Components.DesignSystem;
using System.Globalization;
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
/// desplegada, cada una con la única acción «Gestionar».
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
        RolEfectivo = rol;
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
        var cut = RenderizarComo(mediador);
        await AlternarSeleccionMultiple(cut);
        await CasillaDe(cut, "Montajes Ebro S.L.").ChangeAsync(new ChangeEventArgs { Value = true });
        await DesplegarAsync(cut, "Montajes Ebro S.L.");
        cut.WaitForAssertion(() => Tarjeta(cut, "Montajes Ebro S.L.").TextContent.Should().Contain("Sin documentos pendientes de gestionar"));
        var pregunta = UltimaConsulta(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await CeldaDeEstado(cut, "Aislamientos Nervión S.L.").QuerySelector("button.ventana-contexto-elemento")!.ClickAsync(new MouseEventArgs());
        var formulario = cut.FindComponent<DrawerGestionDocumento>();
        // La corrección cambia lo guardado: la Empresa pasa a estar al día.
        mediador.Almacen[mediador.Almacen.FindIndex(e => e.Id == nervion.Id)] = Empresa("Aislamientos Nervión S.L.", estado: EstadoDocumento.Vigente) with { Id = nervion.Id };
        await EmisionDeRenovacion.EscribirAsync(formulario);
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
            .Should().Contain("Sin documentos pendientes de gestionar", "y lo desplegado sigue desplegado");
        ConsultasDeClientes(mediador).Should().Be(0, "ni antes ni después se pregunta por Clientes");
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
        await cut.InvokeAsync(() => cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Raz\u00f3n social").Instance.ValorChanged.InvokeAsync("Empresa de prueba"));
        await cut.InvokeAsync(() => cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Identificaci\u00f3n fiscal *").Instance.ValorChanged.InvokeAsync("B10380186"));

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

    // --- Fila desplegada: documentos pendientes con una sola acción, «Gestionar» --------------------

    private static IReadOnlyList<IElement> DocumentosPendientes(IRenderedComponent<Empresas> cut) =>
        cut.FindAll(".tarjeta-fila-acordeon-contenido [data-pieza='documentos-pendientes'] .documento-pendiente-empresa").ToList();

    /// <summary>
    /// Maqueta de Empresas y regla 8 de LEEME-AUTORIDAD: cada documento que pide trabajo lleva nombre, pastilla
    /// de estado, el detalle de su vigencia y UNA acción, «Gestionar» (revisar, subir, confirmar o reclamar viven
    /// detrás de ese verbo). Abre el formulario de corrección del documento de su línea, sin abrir la vista rápida.
    /// </summary>
    [Fact]
    public async Task La_fila_desplegada_lista_cada_documento_pendiente_con_una_sola_accion_Gestionar()
    {
        var vencida = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12);
        var sinConfirmar = IncidenciaDe("Modalidad preventiva", EstadoDocumento.SinConfirmar);
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, vencida, sinConfirmar);
        var mediador = new MediatorFalso { Almacen = { nervion }, Documento = DocumentoDe(sinConfirmar, "Aislamientos Nervión S.L.", Guid.NewGuid()) };
        var cut = RenderizarComo(mediador);

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        var documentos = DocumentosPendientes(cut);
        documentos.Select(d => d.QuerySelector(".documento-pendiente-nombre")!.TextContent.Trim())
            .Should().Equal(["Seguro de responsabilidad civil", "Modalidad preventiva"], "los mismos documentos que la ventana del motivo, en su orden");
        documentos.Select(d => d.QuerySelector("[data-pieza='pastilla']")!.TextContent.Trim()).Should().HaveCount(2).And.OnlyContain(t => t.Length > 0);
        documentos[0].QuerySelector(".documento-pendiente-detalle")!.TextContent.Trim().Should().Be("Venció el " + FechaMaqueta(vencida), "dice cuándo venció, con la fecha absoluta de la maqueta");
        documentos[1].QuerySelector(".documento-pendiente-detalle")!.TextContent.Trim().Should().Be("Falta la fecha de vencimiento", "dice qué falta para saber su vigencia");
        documentos.Should().AllSatisfy(d =>
        {
            d.QuerySelectorAll("button").Should().ContainSingle("una sola acción por documento")
                .Which.TextContent.Trim().Should().Be("Gestionar");
        });
        cut.Find(".tarjeta-fila-acordeon-contenido").TextContent.Should().NotContainAny("Revisar", "Subir", "Confirmar", "Reclamar");
        cut.FindAll(".titulo-clientes-empresa").Should().BeEmpty();

        await documentos[1].QuerySelector("button")!.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(sinConfirmar.DocumentoId,
            "«Gestionar» abre el documento de su línea");
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull("gestionar desde la fila desplegada tampoco abre la vista rápida");
        ConsultasDeClientes(mediador).Should().Be(0);
    }

    private static string FechaMaqueta(IncidenciaDocumentalDto incidencia) =>
        incidencia.FechaVencimiento!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// «Empresas TALVEG.dc.html», fila desplegada: lo vencido dice «Venció el dd/MM/yyyy» (fecha absoluta, nunca
    /// «Hace N días»), literal de esa maqueta. «Caduca en N días» (por vencer) y «Falta la fecha de vencimiento»
    /// (sin confirmar) son una extrapolación de la maqueta de Trabajadores: la de Empresas solo trae «Venció el».
    /// </summary>
    [Fact]
    public async Task La_fila_desplegada_dice_Vencio_el_de_la_maqueta_y_extrapola_el_resto_de_Trabajadores()
    {
        var vencido = IncidenciaDe("Certificado de estar al corriente con la TGSS", EstadoDocumento.Vencido, -80);
        var enTolerancia = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.EnTolerancia, -3);
        var urgente = IncidenciaDe("Modalidad preventiva", EstadoDocumento.Urgente, 5);
        var proximo = IncidenciaDe("Memoria de actividades", EstadoDocumento.Proximo, 20);
        var sinConfirmar = IncidenciaDe("Póliza de accidentes", EstadoDocumento.SinConfirmar);
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido, vencido, enTolerancia, urgente, proximo, sinConfirmar);
        var cut = RenderizarComo(new MediatorFalso { Almacen = { nervion } });

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        DocumentosPendientes(cut).Select(d => d.QuerySelector(".documento-pendiente-detalle")!.TextContent.Trim()).Should().Equal(
            "Venció el " + FechaMaqueta(vencido),
            "Venció el " + FechaMaqueta(enTolerancia),
            "Caduca en 5 días",
            "Caduca en 20 días",
            "Falta la fecha de vencimiento");
        cut.Find(".tarjeta-fila-acordeon-contenido").TextContent.Should().NotContain("Hace ", "la maqueta no muestra el plazo relativo de lo vencido");
    }

    /// <summary>Un «sin confirmar» que es el contrato de trabajo pide la fecha de FIN, como en la lista de Trabajadores.</summary>
    [Fact]
    public async Task Un_contrato_de_trabajo_sin_confirmar_pide_la_fecha_de_fin()
    {
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.SinConfirmar,
            IncidenciaDe("Contrato de trabajo", EstadoDocumento.SinConfirmar));
        var cut = RenderizarComo(new MediatorFalso { Almacen = { nervion } });

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        DocumentosPendientes(cut).Single().QuerySelector(".documento-pendiente-detalle")!.TextContent.Trim().Should().Be("Falta la fecha de fin");
    }

    [Fact]
    public async Task El_boton_Gestionar_nombra_el_documento_y_la_empresa_para_un_lector_de_pantalla()
    {
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido,
            IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12));
        var cut = RenderizarComo(new MediatorFalso { Almacen = { nervion } });

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        DocumentosPendientes(cut).Single().QuerySelector("button")!.GetAttribute("aria-label")
            .Should().Be("Gestionar Seguro de responsabilidad civil de Aislamientos Nervión S.L.");
    }

    [Fact]
    public async Task Sin_documentos_pendientes_la_fila_desplegada_dice_que_esta_al_dia()
    {
        var ebro = Empresa("Montajes Ebro S.L.", estado: EstadoDocumento.Vigente);
        var cut = RenderizarComo(new MediatorFalso { Almacen = { ebro } });

        await DesplegarAsync(cut, "Montajes Ebro S.L.");

        var contenido = cut.Find(".tarjeta-fila-acordeon-contenido");
        contenido.TextContent.Trim().Should().Be("Sin documentos pendientes de gestionar");
        cut.FindAll("[data-pieza='documentos-pendientes']").Should().BeEmpty();
        contenido.QuerySelectorAll("button").Should().BeEmpty("sin documentos no hay nada que gestionar");
    }

    /// <summary>
    /// Por rol: quien puede escribir (<c>Roles.ConEscrituraCsv</c>) ve «Gestionar» en cada documento; quien solo
    /// consulta (o el rol Cliente, de portal) ve los mismos documentos sin el botón: no se le ofrece un formulario
    /// que el comando le va a denegar.
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.Cliente, false)]
    public async Task Gestionar_solo_se_ofrece_al_rol_que_puede_escribir(string rol, bool ofreceGestionar)
    {
        var vencida = IncidenciaDe("Seguro de responsabilidad civil", EstadoDocumento.Vencido, -12);
        var nervion = EmpresaCon("Aislamientos Nervión S.L.", EstadoDocumento.Vencido,
            vencida,
            IncidenciaDe("Modalidad preventiva", EstadoDocumento.SinConfirmar));
        var cut = RenderizarComo(new MediatorFalso { Almacen = { nervion } }, rol: rol);

        await DesplegarAsync(cut, "Aislamientos Nervión S.L.");

        DocumentosPendientes(cut).Should().HaveCount(2, "control positivo: los documentos se ven con cualquier rol");
        DocumentosPendientes(cut).Select(d => d.QuerySelector(".documento-pendiente-detalle")!.TextContent.Trim()).Should().Equal(
            ["Venció el " + FechaMaqueta(vencida), "Falta la fecha de vencimiento"], "el texto de vigencia no depende del rol");
        cut.FindAll(".tarjeta-fila-acordeon-contenido button").Should().HaveCount(ofreceGestionar ? 2 : 0);
        cut.FindAll(".tarjeta-fila-acordeon-contenido button").Where(b => b.TextContent.Trim() != "Gestionar").Should().BeEmpty("el único verbo es «Gestionar»");
    }
}
