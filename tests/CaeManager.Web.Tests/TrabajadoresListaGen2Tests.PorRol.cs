using System.Globalization;
using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La lista de Trabajadores 1:1 con la maqueta (filas en acordeón, agrupar «Por Empresa», un solo verbo
/// «Gestionar»), fijada por rol: lo que sobrevive del markup anterior se comprueba para cada rol y lo que
/// solo es de escritura aparece solo con escritura. La página no añade comandos de escritura nuevos: los que
/// existen (alta, baja y asignar a centro) ya los guarda AutorizacionEscrituraBehavior.
/// </summary>
public partial class TrabajadoresListaGen2Tests
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

    private Fila ConIncidencias() =>
        ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 4, 2,
            IncidenciaDe("Contrato", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5));

    // --- «Gestionar»: el único verbo de acción por documento --------------------------------------

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public void Con_escritura_cada_documento_pendiente_ofrece_un_unico_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { ConIncidencias() } });

        var detalle = AbrirIncidencias(cut);

        var lineas = detalle.QuerySelectorAll("li.documento-pendiente-trabajador");
        lineas.Should().HaveCount(2);
        foreach (var linea in lineas)
        {
            var botones = linea.QuerySelectorAll("button");
            botones.Should().ContainSingle("un solo verbo por documento");
            botones[0].TextContent.Trim().Should().Be("Gestionar");
            botones[0].GetAttribute("data-accion").Should().Be("gestionar");
        }
        detalle.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().OnlyContain(t => t == "Gestionar");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_el_desglose_se_ve_pero_no_ofrece_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { ConIncidencias() } });

        var detalle = AbrirIncidencias(cut);

        detalle.QuerySelectorAll("li.documento-pendiente-trabajador").Should().HaveCount(2, "control positivo: los documentos se ven");
        detalle.QuerySelectorAll("li.documento-pendiente-trabajador .badge").Should().HaveCount(2);
        detalle.QuerySelectorAll("button").Should().BeEmpty("no se ofrece una acción que el comando va a denegar");
        cut.Markup.Should().NotContain("Gestionar");
    }

    // --- Fila expandida 1:1 con «Lista Trabajadores TALVEG.dc.html» --------------------------------

    /// <summary>
    /// La maqueta pinta cada documento en UNA línea flex: nombre (220 px), pastilla de estado, el texto de vigencia
    /// (la fecha absoluta, nunca «hace N días») que ocupa el resto y, solo con escritura, el «Gestionar». Sin fecha
    /// aparte debajo, sin segunda línea y sin más acción.
    /// </summary>
    [Fact]
    public void La_fila_expandida_pinta_cada_documento_en_una_linea_con_nombre_pastilla_fecha_y_Gestionar()
    {
        var vencido = IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -80);
        var porVencer = IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5);
        var sinConfirmar = IncidenciaDe("Contrato de trabajo", EstadoDocumento.SinConfirmar);
        var cut = Renderizar(new MediatorFalso { Almacen = { ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 6, 3, vencido, porVencer, sinConfirmar) } });

        var detalle = AbrirIncidencias(cut);

        detalle.QuerySelectorAll("ul.documentos-pendientes-trabajador").Should().ContainSingle();
        var lineas = detalle.QuerySelectorAll("li.documento-pendiente-trabajador");
        lineas.Should().HaveCount(3);
        foreach (var linea in lineas)
        {
            // Hijos directos y en este orden: nombre, pastilla, texto de vigencia y acción.
            linea.Children.Select(h => h.LocalName + (h.ClassList.FirstOrDefault(c => c.StartsWith("documento-pendiente") || c == "badge") is { } c ? "." + c : ""))
                .Should().Equal("span.documento-pendiente-nombre", "span.badge", "span.documento-pendiente-detalle", "button.documento-pendiente-gestionar");
        }
        string Texto(int i, string clase) => lineas[i].QuerySelector("." + clase)!.TextContent.Trim();
        Texto(0, "documento-pendiente-nombre").Should().Be("Aptitud médica");
        Texto(0, "badge").Should().Be("Vencido");
        Texto(0, "documento-pendiente-detalle").Should().Be("Desde el " + vencido.FechaVencimiento!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        Texto(1, "badge").Should().Be("Por vencer");
        Texto(1, "documento-pendiente-detalle").Should().Be("Caduca el " + porVencer.FechaVencimiento!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
        Texto(2, "badge").Should().Be("Sin confirmar");
        Texto(2, "documento-pendiente-detalle").Should().Be("Falta la fecha de fin", "el contrato de trabajo no «vence»: acaba, como dice la maqueta");
        detalle.TextContent.Should().NotContain("Hace ", "la maqueta no muestra el plazo relativo en la fila expandida");
    }

    /// <summary>Un «sin confirmar» que no es el contrato sigue diciendo «Falta la fecha de vencimiento» (maqueta, fila de Formación PRL).</summary>
    [Fact]
    public void Un_sin_confirmar_que_no_es_el_contrato_pide_la_fecha_de_vencimiento()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.SinConfirmar), 2, 0, IncidenciaDe("Formación PRL", EstadoDocumento.SinConfirmar)) } });

        AbrirIncidencias(cut).QuerySelector(".documento-pendiente-detalle")!.TextContent.Trim().Should().Be("Falta la fecha de vencimiento");
    }

    /// <summary>
    /// El texto de vigencia es el mismo para todos los roles; «Gestionar» es lo único que cambia con la escritura.
    /// </summary>
    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void El_texto_de_vigencia_de_cada_estado_no_depende_del_rol(string rol)
    {
        this.ConRolDeEscritura(rol);
        var vencido = IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -80);
        var porVencer = IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5);
        var sinConfirmar = IncidenciaDe("Formación PRL", EstadoDocumento.SinConfirmar);
        var cut = Renderizar(new MediatorFalso { Almacen = { ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 6, 3, vencido, porVencer, sinConfirmar) } });

        AbrirIncidencias(cut).QuerySelectorAll(".documento-pendiente-detalle").Select(d => d.TextContent.Trim()).Should().Equal(
            "Desde el " + vencido.FechaVencimiento!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            "Caduca el " + porVencer.FechaVencimiento!.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            "Falta la fecha de vencimiento");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_la_fila_expandida_deja_la_linea_de_tres_celdas_sin_Gestionar(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { ConIncidencias() } });

        foreach (var linea in AbrirIncidencias(cut).QuerySelectorAll("li.documento-pendiente-trabajador"))
            linea.Children.Select(h => h.LocalName).Should().Equal("span", "span", "span");
    }

    // --- Lo que sobrevive del markup de main, por rol ---------------------------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void La_fila_conserva_para_todos_los_roles_nombre_DNI_empresa_cumplimiento_estado_y_ficha_360(string rol)
    {
        this.ConRolDeEscritura(rol);
        var javier = ConDesglose(Trabajador("Javier", "Salas Moreno", estado: EstadoDocumento.Vencido), 4, 2,
            IncidenciaDe("Contrato", EstadoDocumento.Vencido, -12));
        var cut = Renderizar(new MediatorFalso { Almacen = { javier } });

        var fila = FilasDeDatos(cut).Should().ContainSingle().Subject;
        fila.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim().Should().Be("Javier Salas Moreno", "el nombre abre la vista rápida");
        fila.QuerySelector(".celda-trabajador-dni")!.TextContent.Trim().Should().NotStartWith("DNI", "el DNI va sin prefijo");
        fila.QuerySelector(".celda-trabajador-dni .boton-copiar-en-linea").Should().NotBeNull("el documento se copia con un clic");
        fila.QuerySelector(".celda-empresa-trabajador")!.TextContent.Trim().Should().Be("Montajes Ebro S.L.");
        fila.QuerySelector(".col-registrados-vigentes").Should().NotBeNull();
        fila.QuerySelector(".col-estado .badge")!.TextContent.Trim().Should().Be("Vencido");
        fila.QuerySelector(".col-estado .estado-fila-motivo")!.TextContent.Trim().Should().Be("Contrato");
        fila.QuerySelector("a.boton-360-pagina").Should().NotBeNull("la ficha 360 es lectura");
        fila.QuerySelector(".boton-expandir-fila").Should().NotBeNull("hay incidencias que desplegar");
        fila.QuerySelectorAll(".menu-acciones-disparador").Should().BeEmpty("la fila no lleva menú «⋯»");
        fila.QuerySelectorAll(".lista-filas-acordeon input[type=checkbox]").Should().BeEmpty("sin modo de selección no hay casillas");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Sin_incidencias_la_fila_no_tiene_chevron_ni_documentos_para_ningun_rol(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Ana", "Alonso", estado: EstadoDocumento.Vigente) } });

        FilasDeDatos(cut)[0].QuerySelector(".boton-expandir-fila").Should().BeNull();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void La_barra_de_filtros_y_la_exportacion_se_ven_para_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Ana", "Alonso") } });

        Pastilla(cut, "Filtros guardados").Should().NotBeNull();
        cut.Find("header.cabecera-pagina a[data-keytip=X]").GetAttribute("aria-label").Should().Be("Exportar a Excel");
        cut.Find("button[aria-label='Atajos de teclado']").Should().NotBeNull();
        cut.FindAll(".paginador-texto").Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public void Con_escritura_la_cabecera_ofrece_alta_y_seleccion_multiple(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Ana", "Alonso") } });

        var acciones = cut.Find("header.cabecera-pagina").QuerySelectorAll("button")
            .Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim()).ToList();
        acciones.Should().Contain(["Selección múltiple", "Atajos de teclado", "Más acciones"]);
        cut.Find("header.cabecera-pagina").TextContent.Should().Contain("Nuevo trabajador");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_la_cabecera_no_ofrece_alta_ni_seleccion_multiple(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(new MediatorFalso { Almacen = { Trabajador("Ana", "Alonso") } });

        cut.Find("header.cabecera-pagina").TextContent.Should().NotContain("Nuevo trabajador");
        cut.FindAll("header.cabecera-pagina button[aria-label='Selección múltiple']").Should().BeEmpty();
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
    }

    // --- Agrupar «Por Empresa» ---------------------------------------------------------------------

    private static MediatorFalso ConTresEmpresas() => new()
    {
        Almacen =
        {
            Trabajador("Ana", "Alonso"),
            Trabajador("Bea", "Moreno", empresaId: EmpresaDexter),
            Trabajador("Carla", "Navas")
        }
    };

    [Fact]
    public void Sin_agrupar_la_lista_no_lleva_cabeceras_de_grupo()
    {
        var cut = Renderizar(ConTresEmpresas());

        FilasDeDatos(cut).Should().HaveCount(3);
        cut.FindAll("[data-grupo]").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Agrupar_por_Empresa_pone_una_cabecera_por_empleador_con_su_recuento_y_las_filas_debajo(string rol)
    {
        this.ConRolDeEscritura(rol);
        var mediador = ConTresEmpresas();

        var cut = Renderizar(mediador, "trabajadores?agrupar=empresa");

        var cabeceras = cut.FindAll("[data-grupo]");
        cabeceras.Select(c => c.GetAttribute("data-grupo-empresa")).Should().Equal("Dexter Industrial S.A.", "Montajes Ebro S.L.");
        cabeceras.Select(c => c.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim()).Should().Equal("Dexter Industrial S.A.", "Montajes Ebro S.L.");
        cabeceras.Select(c => c.QuerySelector(".grupo-lista-contador")!.TextContent.Trim()).Should().Equal("· 1", "· 2");
        FilasDeDatos(cut).Should().BeEmpty("los grupos nacen plegados: sus filas no se pintan");
        cut.AbrirGrupos();
        FilasDeDatos(cut).Select(f => f.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim())
            .Should().Equal("Bea Moreno", "Ana Alonso", "Carla Navas");
        UltimaConsulta(mediador).OrdenarPor.Should().Be(nameof(TrabajadorListaDto.EmpleadorNombre),
            "el servidor ordena por Empresa para que cada grupo quede contiguo");
        UltimaConsulta(mediador).Descendente.Should().BeFalse();
    }

    [Fact]
    public void Agrupada_se_pide_una_sola_pagina_con_todas_las_filas_y_el_recuento_del_grupo_es_el_total()
    {
        // Agrupada, la pantalla pide todas las filas filtradas: con 21 Trabajadores el grupo no se corta en dos páginas
        // y su recuento es el total de la Empresa, sin «en esta página».
        var mediador = new MediatorFalso();
        for (var i = 0; i < 21; i++) mediador.Almacen.Add(Trabajador($"Persona{i:00}", "Alonso"));

        var cut = Renderizar(mediador, "trabajadores?agrupar=empresa");

        UltimaConsulta(mediador).TamanoPagina.Should().Be(AgrupacionDeLista.TamanoPaginaAgrupada);
        cut.Find("[data-grupo] .grupo-lista-contador").TextContent.Trim().Should().Be("· 21");
        cut.AbrirGrupos();
        FilasDeDatos(cut).Should().HaveCount(21, "el grupo trae todas sus filas aunque el tamaño de página sea menor");
    }

    [Fact]
    public async Task Ordenar_por_una_columna_desactiva_la_agrupacion_y_lo_escribe_en_la_URL()
    {
        var cut = Renderizar(ConTresEmpresas(), "trabajadores?agrupar=empresa");
        cut.FindAll("[data-grupo]").Should().HaveCount(2, "control positivo: estaba agrupada");

        await cut.FindAll(".cabecera-columnas-trabajadores button.col-title").First().ClickAsync(new MouseEventArgs());

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain("orden=").And.NotContain("agrupar=");
        cut.WaitForAssertion(() => cut.FindAll("[data-grupo]").Should().BeEmpty());
    }
}
