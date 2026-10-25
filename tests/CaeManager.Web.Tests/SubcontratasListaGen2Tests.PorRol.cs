using Bunit;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lo que la lista de Subcontratas ofrecia en main antes de alinearla 1:1 con «Subcontratas TALVEG.dc.html» (DDL-083),
/// fijado por rol: enlaces, rotulos, acciones y estados de cada elemento que sobrevive, incluido Consulta. Los
/// elementos que la maqueta retira o cambia estan en su propio test, con el motivo. La pagina no anade comandos de
/// escritura: los que existen (alta y baja en lote) ya los guarda AutorizacionEscrituraBehavior.
/// </summary>
public partial class SubcontratasListaGen2Tests
{
    private static readonly string[] RolesConEscritura = [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];
    private static readonly string[] RolesSoloLectura = [Roles.Consulta, Roles.Cliente];

    public static TheoryData<string> TodosLosRoles => Datos(RolesConEscritura.Concat(RolesSoloLectura));
    public static TheoryData<string> RolesDeEscritura => Datos(RolesConEscritura);
    public static TheoryData<string> RolesDeLectura => Datos(RolesSoloLectura);

    private static TheoryData<string> Datos(IEnumerable<string> roles)
    {
        var datos = new TheoryData<string>();
        foreach (var rol in roles) datos.Add(rol);
        return datos;
    }

    private static IncidenciaSubcontrataDto Corregible(string descripcion, EstadoDocumento estado) =>
        new(descripcion, estado, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid());

    /// <summary>Una Subcontrata con todo lo que la fila pinta: CIF, dos trabajadores, nivel, cumplimiento y un vencido corregible.</summary>
    private static SubcontrataListaDto Completa(Guid id) =>
        Subcontrata("Andamios Bidasoa S.L.", id: id, cumplimiento: 60, nivel: NivelServicioSubcontrata.Supervisada,
            vencidas: [Corregible("Aptitud médica — Sonia Cano", EstadoDocumento.Vencido)],
            estado: EstadoDocumento.Vencido, trabajadoresBloqueados: 1) with
        {
            Trabajadores = [new TrabajadorIdentidadSubcontrataDto(Guid.NewGuid(), "Sonia", "Cano"), new(Guid.NewGuid(), "Luis", "Sanz")]
        };

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void La_fila_conserva_para_todos_los_roles_nombre_CIF_trabajadores_nivel_cumplimiento_estado_y_ficha_360(string rol)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(id)] });

        var fila = cut.Find(".tarjeta-fila-acordeon-cabecera");
        fila.QuerySelector("button.enlace-nombre-fila")!.GetAttribute("aria-label").Should().Be("Abrir la vista rápida de Andamios Bidasoa S.L.");
        var cif = fila.QuerySelector(".boton-copiar-en-linea")!;
        cif.TextContent.Trim().Should().Be("B-20.774.115");
        cif.GetAttribute("aria-label").Should().Be("Copiar el CIF B-20.774.115");
        var trabajadores = cut.FindComponents<VentanaContexto>().Single(v => v.Instance.Titulo == "Trabajadores");
        trabajadores.Find(".ventana-contexto-disparador").TextContent.Should().Be("2 trabajadores");
        trabajadores.FindAll(".ventana-contexto-panel button").Select(b => b.TextContent.Trim()).Should().Equal("Sonia Cano", "Luis Sanz");
        // Rediseño 1:1 (DDL-083): la maqueta pinta el nivel como texto plano, no como pastilla; la explicación pasa al título.
        fila.QuerySelector(".celda-nivel-subcontrata")!.TextContent.Trim().Should().Be("Supervisada");
        fila.QuerySelector(".celda-nivel-subcontrata[title^='Supervisada:']").Should().NotBeNull("el nivel se explica sin atribuir la gestion a TALVEG");
        fila.QuerySelector("[role=progressbar], .barra-cumplimiento")!.TextContent.Should().Contain("60");
        fila.QuerySelector(".estado-fila .badge")!.TextContent.Trim().Should().Be("Vencido");
        fila.QuerySelector(".estado-fila-motivo")!.TextContent.Trim().Should().Be("1 Trabajador bloqueado en algún Centro");
        fila.QuerySelector("a.boton-360-pagina")!.GetAttribute("href").Should().Be($"/subcontratas/{id}");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void La_cabecera_conserva_para_todos_los_roles_titulo_contador_seleccion_exportar_teclado_y_menu(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.Find("h1").TextContent.Trim().Should().Be("Subcontratas");
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("1");
        cut.Find("button[aria-label='Selección múltiple']").GetAttribute("aria-pressed").Should().Be("false",
            "seleccionar es lectura: el ☑ lo ven todos los roles");
        cut.Find("a[data-keytip=X]").GetAttribute("href").Should().Be("/subcontratas/exportar.xlsx");
        cut.Find("button[aria-label='Atajos de teclado']").Should().NotBeNull();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().ContainSingle("el menú ⋯ con la exportación");
    }

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public void Con_escritura_la_cabecera_ofrece_Nueva_subcontrata(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.FindAll("header.cabecera-pagina .acciones-cabecera button").Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Contain("+ Nueva subcontrata");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_la_cabecera_no_ofrece_Nueva_subcontrata(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("+ Nueva subcontrata");
        cut.Markup.Should().Contain("Andamios Bidasoa S.L.", "control positivo: la lista se ve");
    }

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public async Task Con_escritura_marcar_una_fila_ofrece_la_barra_de_lote_con_Eliminar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.Find("button[aria-label='Selección múltiple']").Click();
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.Find(".barra-acciones-lote").TextContent.Should().Contain("Eliminar seleccionados");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public async Task Sin_escritura_se_puede_marcar_pero_no_hay_barra_de_lote(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.Find("button[aria-label='Selección múltiple']").Click();
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Eliminar seleccionados");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Los_filtros_conservan_buscador_nivel_estado_y_filtros_guardados_para_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.Find(".barra-filtros-pastillas input[type=text]").GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: razón social o CIF");
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label")!)
            .Should().Contain("Nivel");
        cut.FindAll(".franja-estado .franja-estado-boton").Select(b => b.GetAttribute("data-estado") ?? "todas")
            .Should().Contain("Vencido", "la franja ofrece los estados documentales");
    }

    /// <summary>
    /// Corregir una incidencia (el formulario de siempre) solo se ofrece con escritura: el comando se lo denegaria a Consulta.
    /// En la maqueta el documento vive en la fila desplegada, con su unico verbo «Gestionar».
    /// </summary>
    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public async Task Con_escritura_el_documento_del_desglose_abre_la_correccion_con_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        this.ConServiciosDelFormularioDeDocumento();
        var mediador = new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] };
        var cut = Renderizar(mediador);
        cut.Find(".boton-expandir-fila").Click();

        await cut.Find("button.documento-pendiente-gestionar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId.ObtenerDocumentoPorIdQuery>().Should().ContainSingle();
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_el_documento_del_desglose_se_lee_pero_no_se_corrige(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });
        cut.Find(".boton-expandir-fila").Click();

        cut.Find(".tarjeta-fila-acordeon-contenido").TextContent.Should().Contain("Aptitud médica — Sonia Cano", "control positivo: el desglose se ve");
        cut.FindAll(".tarjeta-fila-acordeon-contenido button").Should().BeEmpty();
    }

    /// <summary>
    /// Pieza de main que la maqueta no tiene y se retira (DDL-083): la linea «N de M subcontratas» de la barra de filtros. El
    /// recuento ya lo dan el contador de la cabecera y el pie «Mostrando N de M».
    /// </summary>
    [Fact]
    public void La_barra_de_filtros_ya_no_lleva_la_linea_de_conteo()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Completa(Guid.NewGuid())] });

        cut.FindAll(".conteo-subcontratas").Should().BeEmpty();
        cut.Find(".paginador-pie-listado .paginador-texto").TextContent.Trim().Should().EndWith("Mostrando 1 de 1");
    }
}