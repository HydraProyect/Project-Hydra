using AngleSharp.Dom;
using Bunit;
using System.Globalization;
using CaeManager.Application.Proyectos.Queries.ObtenerEmpresasProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVehiculosProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVisitasProyecto;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Proyecto360PaginaTests
{
    private static IElement ContadorDe(IRenderedComponent<ProyectoDetalle> cut, string pestana) =>
        cut.Find($"[role=tab][id$='-tab-{pestana}'] .pestanas-contador");

    [Theory]
    [InlineData(EstadoDocumento.Vencido, true)]
    [InlineData(EstadoDocumento.Faltante, true)]
    [InlineData(EstadoDocumento.EnTolerancia, false)]
    [InlineData(EstadoDocumento.Urgente, false)]
    [InlineData(EstadoDocumento.Proximo, false)]
    [InlineData(EstadoDocumento.SinConfirmar, false)]
    [InlineData(EstadoDocumento.Vigente, false)]
    [InlineData(EstadoDocumento.SinCaducidad, false)]
    public void Tecnicos_cuenta_los_bloqueados_en_rojo_o_los_activos_en_gris(EstadoDocumento estado, bool enAlerta)
    {
        _mediador.EstadoDelTecnico = estado;
        var cut = Renderizar(AbiertoId);

        var contador = cut.FindComponent<Pestanas>().Instance.Definiciones.Single(p => p.Id == "tecnicos").Contador!;
        contador.Valor.Should().Be(1, "los técnicos de baja no cuentan");
        contador.EnAlerta.Should().Be(enAlerta);
        contador.Glosa.Should().Be(enAlerta ? "técnicos bloqueados" : "técnicos activos");
        ContadorDe(cut, "tecnicos").ClassList.Contains("pestanas-contador-alerta").Should().Be(enAlerta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tecnicos_sin_estado_conocido_no_inventa_bloqueos(bool fallaLectura)
    {
        _mediador.TecnicoSinAsignarAlCentro = !fallaLectura;
        _mediador.FallarDocumentacionPorCentro = fallaLectura;
        var cut = Renderizar(AbiertoId);

        ContadorDe(cut, "tecnicos").ClassList.Should().NotContain("pestanas-contador-alerta");
        ContadorDe(cut, "tecnicos").TextContent.Should().Contain("1 técnicos activos");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empresas_cuenta_solo_las_rojas_cuando_las_hay_y_Vehiculos_y_Visitas_cuentan_el_total(bool hayRojas)
    {
        _mediador.Empresas[AbiertoId] =
        [
            new(Guid.NewGuid(), "Empresa A", 1, true, false) { EstadoDocumental = hayRojas ? EstadoDocumento.Vencido : EstadoDocumento.Vigente },
            new(Guid.NewGuid(), "Empresa B", 1, true, false) { EstadoDocumental = hayRojas ? EstadoDocumento.Faltante : EstadoDocumento.EnTolerancia },
            new(Guid.NewGuid(), "Empresa C", 1, false, true),
        ];
        _mediador.Vehiculos[AbiertoId] = [new(Guid.NewGuid(), "Vehículo A", "Modelo", "1234 ABC", Guid.NewGuid(), "Empresa A") { EstadoDocumental = EstadoDocumento.Vencido }];
        _mediador.Visitas[AbiertoId] = [new(Guid.NewGuid(), new(2026, 10, 1), new(2026, 10, 2), 2)];
        var cut = Renderizar(AbiertoId);

        var contador = cut.FindComponent<Pestanas>().Instance.Definiciones.Single(p => p.Id == "empresas").Contador!;
        contador.Valor.Should().Be(hayRojas ? 2 : 3);
        contador.EnAlerta.Should().Be(hayRojas);
        ContadorDe(cut, "empresas").ClassList.Contains("pestanas-contador-alerta").Should().Be(hayRojas);
        foreach (var pestana in new[] { "vehiculos", "visitas" })
        {
            ContadorDe(cut, pestana).TextContent.Should().StartWith("1");
            ContadorDe(cut, pestana).ClassList.Should().NotContain("pestanas-contador-alerta");
        }
        cut.FindAll("[role=tab][id$='-tab-ficha'] .pestanas-contador, [role=tab][id$='-tab-historial'] .pestanas-contador").Should().BeEmpty();
    }

    [Fact]
    public void Las_listas_vacias_cuentan_cero_y_las_lecturas_fallidas_no_inventan_cero()
    {
        _mediador.QueLanzan.Add(typeof(ObtenerVehiculosProyectoQuery));
        var cut = Renderizar(AbiertoId);

        ContadorDe(cut, "empresas").TextContent.Should().StartWith("0");
        ContadorDe(cut, "visitas").TextContent.Should().StartWith("0");
        cut.FindAll("[role=tab][id$='-tab-vehiculos'] .pestanas-contador").Should().BeEmpty();
    }

    [Theory]
    [InlineData("es-ES", "empresas", "Empresas con técnicos en el proyecto · documentación de empresa y subcontratas")]
    [InlineData("es-ES", "vehiculos", "Vehículos con acceso a Centro de prueba para este proyecto")]
    [InlineData("es-ES", "documentos", "Documentos propios del proyecto, del peor estado al mejor")]
    [InlineData("es-ES", "visitas", "Visitas del proyecto, la más reciente primero")]
    [InlineData("ca-ES", "empresas", "Empreses amb tècnics al projecte · documentació d’empresa i subcontractes")]
    [InlineData("ca-ES", "vehiculos", "Vehicles amb accés a Centro de prueba per a aquest projecte")]
    [InlineData("ca-ES", "documentos", "Documents propis del projecte, del pitjor estat al millor")]
    [InlineData("ca-ES", "visitas", "Visites del projecte, la més recent primer")]
    public void Las_introducciones_de_la_maqueta_se_localizan_y_Vehiculos_usa_el_Centro_del_Proyecto(string cultura, string pestana, string texto)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            _mediador.Detalles[AbiertoId] = Abierto with { CentroNombre = "Centro de prueba" };
            var cut = Renderizar(AbiertoId, $"?pestana={pestana}");

            cut.Find("[role=tabpanel] p.texto-descriptivo").TextContent.Should().Be(texto);
        }
        finally { CultureInfo.CurrentUICulture = anterior; }
    }

    [Theory]
    [InlineData("es-ES", "empresas", "Sin empresas en el proyecto", "Aquí aparecerán la Empresa y las subcontratas con técnicos en el proyecto.")]
    [InlineData("es-ES", "vehiculos", "Sin vehículos asignados", "Aquí aparecerán los vehículos de las Empresas con técnicos en el proyecto.")]
    [InlineData("es-ES", "visitas", "Sin visitas en este periodo", "Aquí aparecerán las visitas del Centro dentro de las fechas del proyecto.")]
    [InlineData("ca-ES", "empresas", "Sense empreses al projecte", "Aquí apareixeran l’Empresa i les subcontractes amb tècnics al projecte.")]
    [InlineData("ca-ES", "vehiculos", "Sense vehicles assignats", "Aquí apareixeran els vehicles de les Empreses amb tècnics al projecte.")]
    [InlineData("ca-ES", "visitas", "Sense visites en aquest període", "Aquí apareixeran les visites del Centre dins de les dates del projecte.")]
    public void Las_listas_vacias_usan_EstadoVacio_con_titulo_y_descripcion_localizados(string cultura, string pestana, string titulo, string descripcion)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var cut = Renderizar(AbiertoId, $"?pestana={pestana}");

            var vacio = cut.FindComponent<EstadoVacio>();
            vacio.Instance.Titulo.Should().Be(titulo);
            vacio.Instance.Descripcion.Should().Be(descripcion);
            vacio.Find("h3").TextContent.Should().Be(titulo);
            vacio.Find("p").TextContent.Should().Be(descripcion);
            cut.FindAll(".lista-relaciones-tarjeta").Should().BeEmpty();
        }
        finally { CultureInfo.CurrentUICulture = anterior; }
    }

    [Theory]
    [InlineData("es-ES", "Editar proyecto", "Cliente y Centro no se pueden cambiar después de crear el proyecto.")]
    [InlineData("ca-ES", "Editar projecte", "El Client i el Centre no es poden canviar després de crear el projecte.")]
    public async Task Editar_explica_al_pie_de_los_campos_que_el_Cliente_empresarial_y_el_Centro_son_inmutables(string cultura, string accion, string aviso)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            var cut = Renderizar(AbiertoId);
            await ElegirDelMenuAsync(cut, accion);

            var formulario = cut.Find(".formulario-drawer-contenido");
            formulario.LastElementChild!.TextContent.Should().Be(aviso);
            cut.FindAll(".drawer-pie button").Should().HaveCount(2);
        }
        finally { CultureInfo.CurrentUICulture = anterior; }
    }

    [Theory]
    [InlineData("empresas")]
    [InlineData("vehiculos")]
    public void Empresas_y_Vehiculos_ordenan_en_memoria_por_el_peor_estado_y_dejan_los_desconocidos_al_final(string pestana)
    {
        EstadoDocumento?[] estados = [EstadoDocumento.Vigente, EstadoDocumento.SinConfirmar, EstadoDocumento.Faltante,
            null, EstadoDocumento.EnTolerancia, EstadoDocumento.Vencido, EstadoDocumento.Urgente,
            EstadoDocumento.SinCaducidad, EstadoDocumento.Proximo, EstadoDocumento.Vencido];
        _mediador.Empresas[AbiertoId] = estados.Select((estado, i) => new EmpresaProyectoDto(Guid.NewGuid(), $"Empresa {i}", 1, true, false) { EstadoDocumental = estado }).ToList();
        _mediador.Vehiculos[AbiertoId] = estados.Select((estado, i) => new VehiculoProyectoDto(Guid.NewGuid(), $"Vehículo {i}", "Modelo", "1234 ABC", Guid.NewGuid(), "Empresa A") { EstadoDocumental = estado }).ToList();
        var cut = Renderizar(AbiertoId, $"?pestana={pestana}");

        var nombre = pestana == "empresas" ? "Empresa" : "Vehículo";
        cut.FindAll(".fila-relacion-nombre").Select(n => n.TextContent.Trim()).Should().Equal(
            $"{nombre} 5", $"{nombre} 9", $"{nombre} 2", $"{nombre} 4", $"{nombre} 6", $"{nombre} 8", $"{nombre} 1", $"{nombre} 0", $"{nombre} 7", $"{nombre} 3");
        _mediador.Empresas[AbiertoId].Select(e => e.EstadoDocumental).Should().Equal(estados);
        _mediador.Vehiculos[AbiertoId].Select(v => v.EstadoDocumental).Should().Equal(estados);
        _mediador.Enviados.OfType<ObtenerEmpresasProyectoQuery>().Should().ContainSingle();
        _mediador.Enviados.OfType<ObtenerVehiculosProyectoQuery>().Should().ContainSingle();
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("del peor estado al mejor");
    }

    [Fact]
    public void Visitas_muestra_la_mas_reciente_primero_sin_inventar_estado_ni_trabajadores_listos()
    {
        _mediador.Visitas[AbiertoId] =
        [
            new(Guid.NewGuid(), new(2026, 9, 1), new(2026, 9, 2), 3),
            new(Guid.NewGuid(), new(2026, 10, 4), new(2026, 10, 5), 2),
            new(Guid.NewGuid(), new(2026, 8, 1), new(2026, 8, 2), 1),
            new(Guid.NewGuid(), new(2026, 10, 4), new(2026, 10, 6), 4),
        ];
        var cut = Renderizar(AbiertoId, "?pestana=visitas");

        cut.FindAll(".fila-relacion-nombre").Select(n => n.TextContent.Trim()).Should().Equal(
            "04/10/2026 – 06/10/2026", "04/10/2026 – 05/10/2026", "01/09/2026 – 02/09/2026", "01/08/2026 – 02/08/2026");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("la más reciente primero");
        cut.FindAll(".lista-relaciones-tarjeta .badge").Should().BeEmpty();
        cut.Find(".lista-relaciones-tarjeta").TextContent.Should().NotContain("listos");
        _mediador.Enviados.OfType<ObtenerVisitasProyectoQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Tecnicos_con_estados_distintos_cuenta_solo_los_bloqueados_activos()
    {
        var trabajadorListo = Guid.NewGuid();
        _mediador.EstadoDelTecnico = EstadoDocumento.Vencido;
        _mediador.TecnicosAdicionales.Add(new TecnicoProyectoDto(Guid.NewGuid(), trabajadorListo, "Técnico listo", new(2026, 10, 1), null, true));
        _mediador.EstadosPorTrabajador[trabajadorListo] = EstadoDocumento.Vigente;
        var cut = Renderizar(AbiertoId);

        cut.FindAll(".fila-relacion-nombre").Should().HaveCount(3, "hay dos técnicos activos y uno de baja");
        var contador = cut.FindComponent<Pestanas>().Instance.Definiciones.Single(p => p.Id == "tecnicos").Contador!;
        contador.Should().Be(new ContadorPestana(1, "técnicos bloqueados", EnAlerta: true));
    }

    [Fact]
    public void El_prefijo_de_alerta_se_limita_a_los_contadores_rojos_de_Proyecto360()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull();
        var css = File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Proyectos", "Pages", "ProyectoDetalle.razor.css"));

        css.Should().MatchRegex(@"\.proyecto360\s+::deep\s+\.pestanas-contador-alerta::before\s*\{\s*content:\s*""!"";");
    }
}
