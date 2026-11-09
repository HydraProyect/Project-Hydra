using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pestaña «Trabajadores» de Centro 360 (maqueta «Centro 360 TALVEG»): el primer nivel es una línea de resumen sin botones y el
/// segundo nivel, el desplegable, lleva los motivos y «Gestionar» por documento. Corrige «esto se ve deforme» (2026-11-09).
/// </summary>
public partial class Centro360Gen2Tests
{
    private static readonly DateOnly HoyDeLaPestana = DiaDeNegocio.Hoy();

    private sealed record EscenarioTrabajadores(Guid CentroId, TrabajadorAsignacionDocumentacionDto Trabajador, Guid EmpresaId);

    /// <summary>
    /// Un Trabajador con dos bloqueos propios (uno ausente, otro vencido), uno de la Empresa que ninguna fila recoge, un
    /// pendiente que no bloquea y un documento al día.
    /// </summary>
    private EscenarioTrabajadores MontarTrabajadorBloqueado()
    {
        var id = Guid.NewGuid();
        var aptitud = Documento("Certificado de aptitud médica", EstadoDocumento.Faltante);
        var prl = Documento("Formación PRL", EstadoDocumento.Vencido, HoyDeLaPestana.AddDays(-10));
        var epi = Documento("Entrega de EPI", EstadoDocumento.Urgente, HoyDeLaPestana.AddDays(3));
        var contrato = Documento("Contrato de trabajo", EstadoDocumento.Vigente, HoyDeLaPestana.AddDays(400));
        var trabajador = Asignado("Ruiz Peña, Ana", EstadoDocumento.Vencido, aptitud, prl, epi, contrato);
        var empresaId = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[id] = [trabajador];
        mediador.Bloqueos[id] =
        [
            new(id, "Centro Norte", trabajador.TrabajadorId, trabajador.TrabajadorNombre, aptitud.TipoDocumentoId, aptitud.TipoDocumentoNombre,
                Situacion: SituacionDeRequisitoBloqueante.Ausente),
            new(id, "Centro Norte", trabajador.TrabajadorId, trabajador.TrabajadorNombre, prl.TipoDocumentoId, prl.TipoDocumentoNombre,
                Situacion: SituacionDeRequisitoBloqueante.Vencido),
            new(id, "Centro Norte", trabajador.TrabajadorId, trabajador.TrabajadorNombre, Guid.NewGuid(), "Seguro de responsabilidad civil",
                EmpresaId: empresaId, Ambito: AmbitoAplicacion.Empresa, Situacion: SituacionDeRequisitoBloqueante.Vencido)
        ];
        return new EscenarioTrabajadores(id, trabajador, empresaId);
    }

    private IRenderedComponent<AcordeonAsignacionesCentro> Renderizar360(Guid centroId, bool totales = false) =>
        Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, centroId).Add(x => x.CentroNombre, "Centro Norte")
            .Add(x => x.MostrarEstadoPlataforma, true).Add(x => x.MostrarTotales, totales));

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> Desplegado360Async(Guid centroId, bool totales = false)
    {
        var cut = Renderizar360(centroId, totales);
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion-rejilla").Should().NotBeEmpty());
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        return cut;
    }

    [Fact]
    public void El_primer_nivel_dice_una_linea_sin_botones_y_no_repite_los_motivos()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = Renderizar360(escenario.CentroId);
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion-rejilla").Should().NotBeEmpty());

        var fila = cut.Find(".lista-trabajadores-acordeon .fila-relacion-rejilla");
        fila.QuerySelector(".fila-relacion-nombre")!.TextContent.Should().Contain("Ruiz Peña, Ana");
        fila.QuerySelector("[data-resumen-fila]")!.TextContent.Trim().Should().Be("3 documentos bloquean el acceso");
        fila.QuerySelector(".fila-relacion-estado .badge")!.TextContent.Trim().Should().Be("Bloqueado");
        fila.QuerySelector(".fila-trabajador-fraccion").Should().NotBeNull("el recuento al día sigue en la fila");

        fila.QuerySelectorAll(".fila-relacion-detalle button, .fila-relacion-detalle a").Should().BeEmpty("el resumen no lleva botones");
        cut.FindAll(".motivo-bloqueo, .motivo-bloqueo-pedir").Should().BeEmpty("los motivos viven solo en el desplegable");
        cut.Markup.Should().NotContain("Además, por atender", "ese texto era el segundo bloque del primer nivel");
        // Lo único que lleva la columna de acciones de la fila es el icono 360, con nombre accesible.
        var acciones = fila.QuerySelectorAll(".fila-relacion-acciones button, .fila-relacion-acciones a").ToList();
        acciones.Should().ContainSingle().Which.ClassList.Should().Contain("boton-360");
        acciones[0].GetAttribute("aria-label").Should().Contain("Ruiz Peña, Ana");
        acciones[0].TextContent.Trim().Should().BeEmpty("es solo un icono");
    }

    [Fact]
    public void El_resumen_del_primer_nivel_usa_el_singular_con_un_solo_documento_que_bloquea()
    {
        var id = Guid.NewGuid();
        var prl = Documento("Formación PRL", EstadoDocumento.Vencido, HoyDeLaPestana.AddDays(-10));
        var trabajador = Asignado("Ruiz Peña, Ana", EstadoDocumento.Vencido, prl);
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[id] = [trabajador];
        mediador.Bloqueos[id] = [new(id, "Centro Norte", trabajador.TrabajadorId, trabajador.TrabajadorNombre, prl.TipoDocumentoId, prl.TipoDocumentoNombre,
            Situacion: SituacionDeRequisitoBloqueante.Vencido)];
        var cut = Renderizar360(id);
        cut.WaitForAssertion(() => cut.Find("[data-resumen-fila]").TextContent.Trim().Should().Be("1 documento bloquea el acceso"));
    }

    [Fact]
    public async Task El_desplegable_ofrece_un_Gestionar_por_cada_documento_pendiente_y_ninguno_al_resto()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = await Desplegado360Async(escenario.CentroId);

        var filas = cut.FindAll(".fila-plataforma-centro");
        filas.Should().HaveCount(4);
        string Nombre(AngleSharp.Dom.IElement f) => f.QuerySelector(".identidad-documento-plataforma")!.TextContent;
        var conGestionar = filas.Where(f => f.QuerySelector("[data-accion=gestionar-documento]") is not null).Select(Nombre).ToList();
        conGestionar.Should().HaveCount(3);
        conGestionar.Should().Contain(n => n.Contains("Certificado de aptitud médica"))
            .And.Contain(n => n.Contains("Formación PRL")).And.Contain(n => n.Contains("Entrega de EPI"));
        var alDia = filas.Single(f => Nombre(f).Contains("Contrato de trabajo"));
        alDia.QuerySelector(".celda-accion-plataforma")!.QuerySelectorAll("button, a").Should().BeEmpty("sin nada que gestionar no hay acción");

        foreach (var boton in cut.FindAll("[data-accion=gestionar-documento]"))
        {
            boton.TextContent.Trim().Should().Be("Gestionar");
            boton.ClassList.Should().Contain("boton-secundario").And.Contain("boton-pequeno").And.NotContain("boton-primario");
        }
        cut.Find(".fila-plataforma-centro [data-accion=gestionar-documento][aria-label='Gestionar Formación PRL']").Should().NotBeNull();
    }

    [Fact]
    public async Task El_enlace_de_texto_Actualizar_en_Trabajador_360_ya_no_existe_en_el_desplegable()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = await Desplegado360Async(escenario.CentroId);

        cut.FindAll("[data-accion=actualizar-en-ficha]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Actualizar en Trabajador 360");
        cut.FindAll(".fila-plataforma-centro a[href^='/trabajadores/']").Should().BeEmpty("a la ficha se va con el icono 360 de la fila");
    }

    [Fact]
    public async Task Todas_las_filas_del_desplegable_reservan_la_celda_de_accion_aunque_este_vacia()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = await Desplegado360Async(escenario.CentroId);

        var filas = cut.FindAll(".fila-plataforma-centro");
        filas.Should().OnlyContain(f => f.QuerySelectorAll("[role=cell]").Length == 5, "Documento, Estado, Caducidad, Bloquea y Acción en todas");
        filas.Should().OnlyContain(f => f.QuerySelector(".celda-accion-plataforma") != null);
        var contrato = filas.Single(f => f.TextContent.Contains("Contrato de trabajo"));
        contrato.QuerySelector(".celda-accion-plataforma")!.Children.Should().BeEmpty("vacía, pero presente");
    }

    [Fact]
    public async Task Gestionar_de_un_documento_abre_el_mismo_flujo_de_reclamacion_apuntado_al_Trabajador()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = await Desplegado360Async(escenario.CentroId);

        await cut.Find("[data-accion=gestionar-documento][aria-label='Gestionar Formación PRL']").ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerReclamacionLote>().Instance;
        drawer.Visible.Should().BeTrue();
        drawer.AmbitoInicial.Should().Be(AmbitoAplicacion.Trabajador);
        drawer.EntidadIdInicial.Should().Be(escenario.Trabajador.TrabajadorId);
    }

    [Fact]
    public async Task El_bloqueo_de_la_Empresa_que_ninguna_fila_recoge_conserva_su_motivo_y_su_Gestionar_dentro_del_desplegable()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = await Desplegado360Async(escenario.CentroId);

        var motivo = cut.Find(".fila-relacion-desplegado .motivo-bloqueo");
        motivo.TextContent.Should().Contain("Seguro de responsabilidad civil");
        motivo.QuerySelectorAll(".motivo-bloqueo-linea").Should().ContainSingle("solo lo que no tiene fila propia");
        await motivo.QuerySelector(".motivo-bloqueo-pedir")!.ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerReclamacionLote>().Instance;
        drawer.AmbitoInicial.Should().Be(AmbitoAplicacion.Empresa);
        drawer.EntidadIdInicial.Should().Be(escenario.EmpresaId);
    }

    [Fact]
    public async Task Consulta_ve_el_resumen_y_la_tabla_pero_ningun_Gestionar()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var escenario = MontarTrabajadorBloqueado();
        var cut = await Desplegado360Async(escenario.CentroId);

        cut.Find("[data-resumen-fila]").TextContent.Should().Contain("bloquean el acceso");
        cut.FindAll(".fila-plataforma-centro").Should().HaveCount(4);
        cut.FindAll("[data-accion=gestionar-documento], .motivo-bloqueo-pedir").Should().BeEmpty();
    }

    [Fact]
    public async Task La_pestana_conserva_chevron_selector_asignar_y_filtro_de_estado()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = Renderizar360(escenario.CentroId, totales: true);
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion-rejilla").Should().NotBeEmpty());

        var chevron = cut.Find("button.fila-relacion-desplegar");
        chevron.GetAttribute("aria-expanded").Should().Be("false");
        await chevron.ClickAsync(new MouseEventArgs());
        cut.Find("button.fila-relacion-desplegar").GetAttribute("aria-expanded").Should().Be("true");

        cut.Find("[data-recurso=trabajadores]").TextContent.Should().Contain("Trabajadores");
        cut.Find("[data-recurso=vehiculos]").TextContent.Should().Contain("Vehículos");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("+ Asignar trabajador");
        cut.FindAll(".fila-relacion-estado .badge").Select(b => b.TextContent.Trim()).Should().Contain("Bloqueado");
        cut.FindComponent<FranjaEstado>().Instance.Recuentos!["Bloqueado"].Should().Be(1);
    }

    [Fact]
    public void El_pie_separa_las_cifras_de_la_nota_de_lista_completa()
    {
        var escenario = MontarTrabajadorBloqueado();
        var cut = Renderizar360(escenario.CentroId, totales: true);
        cut.WaitForAssertion(() => cut.FindAll("[data-totales-documentales]").Should().NotBeEmpty());

        var pie = cut.Find("[data-totales-documentales]");
        pie.QuerySelector(".totales-cifras")!.TextContent.Should().Contain("exigidos").And.NotContain("Se calcula");
        pie.QuerySelector("[data-totales-nota]")!.TextContent.Trim().Should().Be("Se calcula sobre la lista completa, no sobre la filtrada.");
        System.Text.RegularExpressions.Regex.IsMatch(pie.TextContent, @"pendientes?\s+Se calcula")
            .Should().BeTrue("las dos frases no pueden quedar pegadas: «pendientesSe calcula…»");
    }
}
