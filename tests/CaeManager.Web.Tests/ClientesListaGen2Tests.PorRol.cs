using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La lista de Clientes 1:1 con «Lista Clientes TALVEG.dc.html» (filas en acordeón con los Centros con problema, un solo
/// verbo «Gestionar»), fijada por rol. Lo que tenía la lista anterior (QuickGrid) y sobrevive —enlaces, rótulos,
/// acciones y estados— se comprueba para CADA rol, Consulta incluido; lo que solo es de escritura aparece solo con
/// escritura. La página no añade comandos de escritura: los que existen (alta, baja) ya los guarda
/// AutorizacionEscrituraBehavior.
/// </summary>
public partial class ClientesListaGen2Tests
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

    private static CentroListaDto CentroConEstado(Guid clienteId, string nombre, EstadoCentro estado, int vencidos = 0)
    {
        var vencidas = Enumerable.Range(0, vencidos)
            .Select(_ => new IncidenciaCentroDto("Aptitud médica", AmbitoCausa.Trabajador, EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), null))
            .ToList();
        return new CentroListaDto(Guid.NewGuid(), nombre, null, clienteId, "Cliente", Guid.NewGuid(), "Empresa", estado, 60,
            new RecuentosCentroDto(vencidas, []));
    }

    /// <summary>Un Cliente empresarial crítico, sin contacto en la agenda, con tres Centros y su peor estado en Vencido.</summary>
    private static ClienteListaDto ClienteConProblemas(string razonSocial = "Umbrella Corporation Ibérica S.A.") =>
        Cliente(razonSocial, "B70005004", critico: true, centros: 3, peor: EstadoDocumento.Vencido, cantidad: 1) with { SinContactoEnAgenda = true };

    private static IElement Tarjeta(IRenderedComponent<Clientes> cut, string razonSocial) =>
        cut.FindAll(".tarjeta-fila-acordeon").Single(t => t.QuerySelector(".enlace-nombre-fila")?.TextContent.Trim() == razonSocial);

    private async Task<IElement> Desplegar(IRenderedComponent<Clientes> cut, string razonSocial)
    {
        await Tarjeta(cut, razonSocial).QuerySelector("button.boton-expandir-fila")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Tarjeta(cut, razonSocial).QuerySelector(".tarjeta-fila-acordeon-contenido")!
            .TextContent.Should().NotContain("Cargando"));
        return Tarjeta(cut, razonSocial).QuerySelector(".tarjeta-fila-acordeon-contenido")!;
    }

    // --- Lo que tenía la lista anterior y sigue en la fila, para cada rol ---------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Cada_rol_ve_en_la_fila_nombre_CIF_avisos_recuento_estado_y_enlace_360(string rol)
    {
        var cliente = ClienteConProblemas();
        var cut = Renderizar(new MediatorFalso { Almacen = { cliente } }, rol: rol);

        var fila = Fila(cut, cliente.RazonSocial);
        fila.QuerySelector("button.nombre-abre-vista-rapida")!.TextContent.Trim().Should().Be(cliente.RazonSocial);
        fila.QuerySelector(".celda-cliente-empresarial-cif")!.TextContent.Should().Contain("B70005004");
        fila.QuerySelector(".insignia-critico")!.TextContent.Should().Contain("Crítico");
        fila.QuerySelector("button.aviso-sin-agenda")!.TextContent.Trim().Should().Be("Sin contacto");
        fila.QuerySelector(".enlace-centros-cliente")!.TextContent.Trim().Should().Be("3");
        fila.QuerySelector(".col-estado")!.TextContent.Should().Contain("Vencido").And.Contain("1 documento");
        fila.QuerySelector($"a[href=\"/clientes/{cliente.Id}\"]").Should().NotBeNull("el icono 360 lleva a la ficha");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Cada_rol_ve_la_cabecera_de_columnas_de_la_maqueta_y_ninguna_columna_Gestor_CAE(string rol)
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { ClienteConProblemas() } }, rol: rol);

        cut.Find(".cabecera-columnas-clientes").TextContent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("Cliente", "Centros", "Estado", "documental");
        cut.Markup.Should().NotContain("celda-gestor-cae-pulsable", "el Gestor CAE de referencia se gestiona en Cliente 360");
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label"))
            .Should().NotContain("Gestor CAE");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task Cada_rol_abre_la_vista_rapida_desde_el_nombre_y_la_pestana_de_agenda_desde_Sin_contacto(string rol)
    {
        var cliente = ClienteConProblemas();
        var cut = Renderizar(new MediatorFalso { Almacen = { cliente } }, rol: rol);

        await Fila(cut, cliente.RazonSocial).QuerySelector("button.nombre-abre-vista-rapida")!.ClickAsync(new MouseEventArgs());
        FrameAbierto!.Tipo.Should().Be(EntidadWorkspace.Cliente);
        FrameAbierto.EntidadId.Should().Be(cliente.Id);

        await Fila(cut, cliente.RazonSocial).QuerySelector("button.aviso-sin-agenda")!.ClickAsync(new MouseEventArgs());
        FrameAbierto!.EntidadId.Should().Be(cliente.Id);
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Cada_rol_ve_la_cabecera_con_seleccion_multiple_exportar_y_atajos(string rol)
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { ClienteConProblemas() } }, rol: rol);

        var acciones = cut.Find("header.cabecera-pagina .acciones-cabecera");
        acciones.QuerySelectorAll("a.cabecera-listado-exportar").Should().ContainSingle();
        acciones.QuerySelector("button[aria-label=\"Atajos de teclado\"]").Should().NotBeNull();
        cut.Find(".cabecera-pagina button[aria-pressed]").Should().NotBeNull("seleccionar es lectura: el ☑ lo ven todos los roles");
        cut.Find("header.cabecera-pagina .cabecera-pagina-kicker, header.cabecera-pagina h1").TextContent.Should().NotContain("Negocio");
    }

    // --- Lo que solo es de escritura -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public void Con_escritura_la_cabecera_ofrece_Nuevo_Cliente(string rol)
    {
        var cut = Renderizar(new MediatorFalso(), rol: rol);

        cut.Find("header.cabecera-pagina .acciones-cabecera").TextContent.Should().Contain("+ Nuevo Cliente");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_la_cabecera_no_ofrece_Nuevo_Cliente_ni_el_vacio_la_creacion(string rol)
    {
        var cut = Renderizar(new MediatorFalso(), rol: rol);

        cut.Find("header.cabecera-pagina .acciones-cabecera").TextContent.Should().NotContain("Nuevo Cliente");
        cut.Markup.Should().NotContain("+ Nuevo Cliente");
    }

    // --- La fila desplegada: los Centros con problema, con «Gestionar» -----------------------------------

    [Fact]
    public void Solo_lleva_chevron_el_Cliente_con_alertas_y_con_Centros()
    {
        var conProblemas = ClienteConProblemas();
        var alCorriente = Cliente("Stark Industries S.A.", centros: 2);
        var sinCentros = Cliente("Cyberdyne Ibérica S.A.", centros: 0, peor: EstadoDocumento.Urgente, cantidad: 2);
        var cut = Renderizar(new MediatorFalso { Almacen = { conProblemas, alCorriente, sinCentros } });

        Tarjeta(cut, conProblemas.RazonSocial).QuerySelector("button.boton-expandir-fila").Should().NotBeNull();
        Tarjeta(cut, alCorriente.RazonSocial).QuerySelector("button.boton-expandir-fila").Should().BeNull("«Sin incidencias» no se despliega (maqueta)");
        Tarjeta(cut, sinCentros.RazonSocial).QuerySelector("button.boton-expandir-fila").Should().BeNull("sin Centros no hay nada que desplegar");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task Al_desplegar_cada_Centro_con_problema_lleva_pastilla_motivo_y_un_unico_Gestionar(string rol)
    {
        var cliente = ClienteConProblemas();
        var mediador = new MediatorFalso { Almacen = { cliente } };
        mediador.CentrosConEstadoPorCliente[cliente.Id] =
        [
            CentroConEstado(cliente.Id, "Sede Sevilla", EstadoCentro.Vencido, vencidos: 1),
            CentroConEstado(cliente.Id, "Planta Bilbao", EstadoCentro.Vigente),
            CentroConEstado(cliente.Id, "Almacén Vigo", EstadoCentro.SinGestionCae)
        ];
        var cut = Renderizar(mediador, rol: rol);

        var contenido = await Desplegar(cut, cliente.RazonSocial);

        var lineas = contenido.QuerySelectorAll("li.centro-pendiente-cliente");
        lineas.Should().ContainSingle("solo se listan los Centros que piden trabajo: ni los vigentes ni los que no requieren gestión CAE");
        lineas[0].Children.Select(h => h.LocalName).Should().Equal("span", "span", "span", "button");
        lineas[0].QuerySelector(".centro-pendiente-nombre")!.TextContent.Trim().Should().Be("Sede Sevilla");
        lineas[0].QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido");
        lineas[0].QuerySelector(".centro-pendiente-detalle")!.TextContent.Trim().Should().Be("1 vencido");
        var gestionar = lineas[0].QuerySelectorAll("button").Should().ContainSingle("un solo verbo por Centro").Subject;
        gestionar.TextContent.Trim().Should().Be("Gestionar");
        gestionar.GetAttribute("data-accion").Should().Be("gestionar");
        UltimaConsultaDeCentros(mediador).ClienteId.Should().Be(cliente.Id);
    }

    [Fact]
    public async Task Gestionar_abre_la_vista_rapida_del_Centro()
    {
        var cliente = ClienteConProblemas();
        var mediador = new MediatorFalso { Almacen = { cliente } };
        var centro = CentroConEstado(cliente.Id, "Sede Sevilla", EstadoCentro.Vencido, vencidos: 1);
        mediador.CentrosConEstadoPorCliente[cliente.Id] = [centro];
        var cut = Renderizar(mediador);
        var contenido = await Desplegar(cut, cliente.RazonSocial);

        await contenido.QuerySelector("button.centro-pendiente-gestionar")!.ClickAsync(new MouseEventArgs());

        FrameAbierto!.Tipo.Should().Be(EntidadWorkspace.Centro);
        FrameAbierto.EntidadId.Should().Be(centro.Id);
    }

    [Fact]
    public async Task Un_fallo_al_pedir_los_Centros_se_dice_en_la_fila_y_deja_reintentar_al_volver_a_desplegar()
    {
        var cliente = ClienteConProblemas();
        var mediador = new MediatorFalso { Almacen = { cliente }, CentrosConEstadoQueFallan = 1 };
        mediador.CentrosConEstadoPorCliente[cliente.Id] = [CentroConEstado(cliente.Id, "Sede Sevilla", EstadoCentro.Vencido, vencidos: 1)];
        var cut = Renderizar(mediador);

        await Tarjeta(cut, cliente.RazonSocial).QuerySelector("button.boton-expandir-fila")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(".centros-desplegados-aviso").TextContent.Should().Contain("No pudimos cargar los centros"));

        await Tarjeta(cut, cliente.RazonSocial).QuerySelector("button.boton-expandir-fila")!.ClickAsync(new MouseEventArgs());
        var contenido = await Desplegar(cut, cliente.RazonSocial);
        contenido.QuerySelectorAll("li.centro-pendiente-cliente").Should().ContainSingle();
    }

    private static ObtenerCentrosQuery UltimaConsultaDeCentros(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last();
}
