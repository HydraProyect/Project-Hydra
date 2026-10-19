using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using FluentAssertions;

namespace CaeManager.Application.Tests.Visitas;

public class ObtenerVisitasFiltrosDesignTests
{
    private sealed class VisitasContexto : IVisitasQueryContext
    {
        public List<Visita> Lista { get; } = [];
        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(Lista.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(Array.Empty<VisitaTrabajador>().AsQueryable());
    }

    [Fact]
    public async Task Los_filtros_y_la_cartera_definen_todos_los_totales_antes_de_paginar_y_agrupar()
    {
        var hoy = DiaDeNegocio.Hoy();
        var titular = new Empresa("Titular en cartera");
        var titularAjeno = new Empresa("Titular fuera de cartera");
        var empresa = new Empresa("Empresa, S.A.");
        var centro = new Centro(titular.Id, empresa.Id, "Centro Norte");
        var centroAjeno = new Centro(titularAjeno.Id, empresa.Id, "Centro ajeno");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.AddRange([centro, centroAjeno]);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange([titular, titularAjeno, empresa]);
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var visitas = new VisitasContexto();
        foreach (var dias in new[] { 0, 2, 6, 15 })
            visitas.Lista.Add(new Visita(centro.Id, hoy.AddDays(dias), hoy.AddDays(dias), null));
        visitas.Lista.Add(new Visita(centroAjeno.Id, hoy, hoy, null));
        var handler = new ObtenerVisitasQueryHandler(centros, configuracion, new DocumentosQueryContextFalso(), empresas,
            new TiposDocumentoQueryContextFalso(), visitas,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centro.Id]), new TrabajadoresQueryContextFalso());
        var consulta = new ObtenerVisitasQuery(null, true, null, TamanoPagina: 1, ConRecuentosPorEstado: true,
            ClienteFiltro: titular.RazonSocial, EmpresaFiltro: SeleccionFiltroVisitas.Escribir([empresa.RazonSocial, "Empresa inexistente"]),
            VentanaDias: 7, AgruparPorCliente: true);

        var resultado = await handler.Handle(consulta, default);

        resultado.Elementos.Should().ContainSingle();
        resultado.TotalElementos.Should().Be(3);
        resultado.RecuentosPorEstado![nameof(EstadoDocumentacionVisita.PorGestionar)].Should().Be(3);
        resultado.ResumenPorGrupo!.Should().ContainSingle();
        resultado.ResumenPorGrupo[titular.Id.ToString()].Total.Should().Be(3, "es el grupo entero, no la única fila de esta página");
        var fueraDeCartera = await handler.Handle(consulta with { ClienteFiltro = titularAjeno.RazonSocial }, default);
        fueraDeCartera.TotalElementos.Should().Be(0);
        fueraDeCartera.Elementos.Should().BeEmpty();
        fueraDeCartera.ResumenPorGrupo.Should().BeEmpty();
    }
}
