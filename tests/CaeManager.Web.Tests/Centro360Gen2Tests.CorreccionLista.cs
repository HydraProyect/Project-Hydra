using System.Globalization;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Theory]
    [InlineData("Vencido", "Ana Vencida")]
    [InlineData("Faltante", "")]
    [InlineData("Bloqueado", "Zoe Bloqueada")]
    [InlineData("EnTolerancia", "Tina Tolerada")]
    public void El_filtro_usa_el_peor_estado_con_Vencido_incluyendo_EnTolerancia(string estado, string nombre)
    {
        var id = Guid.NewGuid();
        var vencida = Asignado("Ana Vencida", EstadoDocumento.Vencido,
            Documento("Caducado", EstadoDocumento.Vencido), Documento("Falta", EstadoDocumento.Faltante));
        var bloqueada = Asignado("Zoe Bloqueada", EstadoDocumento.Vencido,
            Documento("Caducado", EstadoDocumento.Vencido));
        var tolerada = Asignado("Tina Tolerada", EstadoDocumento.EnTolerancia,
            Documento("En tolerancia", EstadoDocumento.EnTolerancia, new(2026, 10, 10)) with { EnToleranciaHasta = new(2026, 10, 20) });
        var mediador = Registrar(new MediatorFalso());
        mediador.Asignaciones[id] = [vencida, bloqueada, tolerada];
        mediador.Bloqueos[id] = [new(id, "Centro Norte", bloqueada.TrabajadorId, bloqueada.TrabajadorNombre, Guid.NewGuid(), "Formación PRL")];
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, id)
            .Add(x => x.CentroNombre, "Centro Norte").Add(x => x.MostrarTotales, true).Add(x => x.FiltroEstado, estado));
        var nombres = cut.FindAll(".lista-trabajadores-acordeon .fila-relacion-nombre").Select(n => n.TextContent).ToList();
        nombres.Should().Equal(estado == "Vencido" ? ["Ana Vencida", "Tina Tolerada"] : nombre.Length == 0 ? [] : new[] { nombre });
        var franja = cut.FindComponent<FranjaEstado>().Instance;
        franja.TotalTodos.Should().Be(3);
        franja.Recuentos.Should().NotBeNull();
        var recuentos = franja.Recuentos!;
        recuentos.Values.Sum().Should().Be(4, "En tolerancia también cuenta en Vencido; los chips no suman el total");
        recuentos["Vencido"].Should().Be(2);
        recuentos["Bloqueado"].Should().Be(1);
        recuentos["EnTolerancia"].Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void El_modo_listado_conserva_Bloqueado_y_no_ofrece_la_accion_de_pedir_de_la_fila(bool modoListado)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var bloqueado = Asignado("Zoe Bloqueada", EstadoDocumento.Faltante, Documento("Formación PRL", EstadoDocumento.Faltante), Documento("Vigencia sin confirmar", EstadoDocumento.SinConfirmar));
        var otro = Asignado("Ana Pendiente", EstadoDocumento.Faltante, Documento("Aptitud médica", EstadoDocumento.Faltante));
        mediador.Asignaciones[id] = [bloqueado, otro];
        var bloqueos = new List<DocumentacionBloqueantePendienteDto>
        {
            new(id, "Centro Norte", bloqueado.TrabajadorId, bloqueado.TrabajadorNombre, Guid.NewGuid(), "Formación PRL")
        };
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, id).Add(x => x.CentroNombre, "Centro Norte")
            .Add(x => x.ModoListado, modoListado).Add(x => x.SeleccionMultiple, true).Add(x => x.Bloqueos, bloqueos));
        var pastillas = cut.FindAll(".fila-relacion-estado .badge").Select(b => b.TextContent.Trim());
        if (modoListado)
        {
            pastillas.Should().Contain("Bloqueado");
            cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Pedir");
            cut.FindAll(".fila-relacion-acciones button").Should().OnlyContain(b => b.ClassList.Contains("fila-trabajador-gestionar"),
                "en el listado la única acción de la fila es «Gestionar», que despliega; «Pedir» ya no existe como verbo propio");
            cut.FindAll("input, .acordeon-asignaciones-cabecera, .aviso-visita-sin-asignacion").Should().BeEmpty();
        }
        else
        {
            pastillas.Should().Contain("Bloqueado");
            cut.FindAll(".fila-relacion-acciones button").Should().ContainSingle(b => b.TextContent.Trim() == "Gestionar");
            cut.FindAll(".motivo-bloqueo-pedir").Should().HaveCount(2);
        }
        cut.Find(".motivo-bloqueo").TextContent.Should().Contain("Formación PRL");
    }

    [Fact]
    public void Solo_el_portal_del_canal_principal_es_primario()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte");
        mediador.Canales[id] = [CanalPlataforma("https://secundario.test") with { EsPrincipal = false }, CanalPlataforma("https://principal.test")];
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll(".centro-ficha-canales a").Count(a => a.TextContent.Contains("Abrir portal")).Should().Be(2));
        var portales = cut.FindAll(".centro-ficha-canales a").Where(a => a.TextContent.Contains("Abrir portal")).ToList();
        portales.Single(a => a.GetAttribute("href")!.Contains("principal.test")).ClassList.Should().Contain("boton-primario");
        portales.Single(a => a.GetAttribute("href")!.Contains("secundario.test")).ClassList.Should().Contain("boton-secundario");
        portales.Count(a => a.ClassList.Contains("boton-primario")).Should().Be(1);
    }

    [Theory]
    [InlineData("es-ES", "1 trabajador asignado")]
    [InlineData("ca-ES", "1 treballador assignat")]
    public void La_cabecera_usa_el_singular_para_un_Trabajador_asignado(string cultura, string esperado)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var id = Guid.NewGuid();
            var mediador = Registrar(new MediatorFalso());
            mediador.Detalles[id] = Detalle(id, "Centro Norte");
            mediador.Resumenes[id] = Resumen(id, "Centro Norte");
            mediador.Asignaciones[id] = [Asignado("Ana Alba", EstadoDocumento.Vigente)];
            var cut = Renderizar(id);
            cut.WaitForAssertion(() => cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain(esperado));
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void Las_columnas_de_la_fila_usan_variables_locales_del_componente()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        const string ruta = "src/CaeManager.Web/Features/Centros/Components/AcordeonAsignacionesCentro.razor.css";
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, ruta))) directorio = directorio.Parent;
        directorio.Should().NotBeNull();
        var css = File.ReadAllText(Path.Combine(directorio!.FullName, ruta));
        css.Should().Contain("--ancho-fraccion-trabajador: 76px;").And.Contain("--ancho-estado-trabajador: 128px;")
            .And.Contain("--ancho-acciones-trabajador: 100px;");
        css.Should().Contain("grid-template-columns: minmax(0, 1fr) var(--ancho-fraccion-trabajador) var(--ancho-estado-trabajador) var(--ancho-acciones-trabajador);");
        css.Should().NotContain("grid-template-columns: minmax(0, 1fr) 76px 128px 100px;");
    }
}
