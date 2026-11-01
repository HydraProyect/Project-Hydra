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

/// <summary>
/// C-02: la vigilancia de gestiones urgentes retira el aviso cuando cuenta cero, así que no puede
/// perder una Visita porque otra cambie entre dos lecturas. Las páginas son consultas separadas, sin
/// instantánea común; con Skip, cancelar una fila ya leída adelantaba a las siguientes y la última
/// de ellas quedaba fuera de la lectura de la página que tocaba.
/// </summary>
public class LeerTodasLasVisitasPorClaveTests
{
    private static readonly ObtenerVisitasQuery Candidatas = GestionesUrgentesDeVisita.ConsultaCandidatas() with { TamanoPagina = 2 };

    /// <summary>Cuenta las lecturas: el manejador pide <c>Visitas</c> una vez por cada página.</summary>
    private sealed class VisitasQueCambianEntrePaginas : IVisitasQueryContext
    {
        private int _lecturas;

        public List<Visita> Lista { get; } = [];
        public Action AntesDeLaSegundaPagina { get; set; } = () => { };

        public IQueryable<Visita> Visitas
        {
            get
            {
                if (++_lecturas == 2) AntesDeLaSegundaPagina();
                return new TestAsyncQueryable<Visita>(Lista.AsQueryable());
            }
        }

        public IQueryable<VisitaTrabajador> VisitasTrabajadores =>
            new TestAsyncQueryable<VisitaTrabajador>(Array.Empty<VisitaTrabajador>().AsQueryable());
    }

    [Fact]
    public async Task Cancelar_Visitas_ya_leidas_no_deja_fuera_a_las_que_faltaban_por_leer()
    {
        var (handler, visitas) = Escenario(numeroDeVisitas: 5);
        var enOrden = visitas.Lista.OrderBy(v => v.Id).ToList();
        visitas.AntesDeLaSegundaPagina = () =>
        {
            foreach (var visita in enOrden.Take(Candidatas.TamanoPagina))
                visita.Cancelar(DateTime.UtcNow, "Cancelada mientras se leía.");
        };

        var leidas = await handler.LeerTodasAsync(Candidatas, centroIdsVisibles: null, default);

        leidas.Where(GestionesUrgentesDeVisita.EsUrgente).Select(v => v.Id).Should()
            .Contain(enOrden.Skip(Candidatas.TamanoPagina).Select(v => v.Id),
                "contar de menos retiraría el aviso de una gestión urgente aún pendiente");
    }

    /// <summary>Cinco deja una página incompleta al final; cuatro, una vacía que cierra el recorrido.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Sin_cambios_cada_Visita_se_lee_una_sola_vez(int numeroDeVisitas)
    {
        var (handler, visitas) = Escenario(numeroDeVisitas);

        var leidas = await handler.LeerTodasAsync(Candidatas, centroIdsVisibles: null, default);

        leidas.Select(v => v.Id).Should().BeEquivalentTo(visitas.Lista.Select(v => v.Id));
    }

    [Fact]
    public async Task La_pagina_de_la_peticion_no_recorta_el_recorrido()
    {
        var (handler, visitas) = Escenario(numeroDeVisitas: 3);

        var leidas = await handler.LeerTodasAsync(Candidatas with { Pagina = 2 }, centroIdsVisibles: null, default);

        leidas.Should().HaveCount(visitas.Lista.Count);
    }

    /// <summary>Todas con las mismas fechas: urgentes, y en el mismo orden por fecha y por Id.</summary>
    private static (ObtenerVisitasQueryHandler Handler, VisitasQueCambianEntrePaginas Visitas) Escenario(int numeroDeVisitas)
    {
        var manana = DiaDeNegocio.Hoy().AddDays(1);
        var titular = new Empresa("Titular");
        var empresa = new Empresa("Empresa, S.A.");
        var centro = new Centro(titular.Id, empresa.Id, "Centro Norte");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange([titular, empresa]);
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var visitas = new VisitasQueCambianEntrePaginas();
        for (var i = 0; i < numeroDeVisitas; i++)
            visitas.Lista.Add(new Visita(centro.Id, manana, manana, null));

        var handler = new ObtenerVisitasQueryHandler(centros, configuracion, new DocumentosQueryContextFalso(), empresas,
            new TiposDocumentoQueryContextFalso(), visitas,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: []), new TrabajadoresQueryContextFalso());

        return (handler, visitas);
    }
}
