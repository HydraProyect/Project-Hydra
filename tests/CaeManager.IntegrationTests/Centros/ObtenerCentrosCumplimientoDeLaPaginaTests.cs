using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// En el camino con estado de <see cref="ObtenerCentrosQueryHandler"/> (el del listado: franja de estado, grupos,
/// filtro u orden por estado) el cumplimiento se calcula para todos los Centros solo cuando se ordena por él; si no,
/// solo para la página. Lo que se fija aquí es que el porcentaje de cada Centro es el mismo por los tres caminos —
/// sin estado, con estado calculándolo para la página y con orden por cumplimiento calculándolo para todos— y que el
/// orden por cumplimiento sigue siendo el de siempre (el más bajo primero y «sin requisitos» al final).
/// </summary>
public class ObtenerCentrosCumplimientoDeLaPaginaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _alDia, _sinDocumento, _aMedias, _sinRequisitos;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Cumplimiento S.L.", "B10000032", false, null, null);
        var empresa = new Empresa("Empresa Cumplimiento S.L.", "B20000030");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var tipo = new TipoDocumento(
            "EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador,
            requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);

        // Nombres en el orden por defecto (Cliente empresarial, nombre): A, B, C, D.
        var alDia = new Centro(cliente.Id, empresa.Id, "Centro A al día");
        var sinDocumento = new Centro(cliente.Id, empresa.Id, "Centro B sin documento");
        var aMedias = new Centro(cliente.Id, empresa.Id, "Centro C a medias");
        var sinRequisitos = new Centro(cliente.Id, empresa.Id, "Centro D sin requisitos");
        contexto.Centros.AddRange(alDia, sinDocumento, aMedias, sinRequisitos);

        var conDocumentoEnA = Trabajador.DeEmpresa(empresa.Id, "Ana", "Alba", "77189989B");
        var sinDocumentoEnB = Trabajador.DeEmpresa(empresa.Id, "Bea", "Baez", "12345678Z");
        var conDocumentoEnC = Trabajador.DeEmpresa(empresa.Id, "Carla", "Cano", "87654321X");
        var sinDocumentoEnC = Trabajador.DeEmpresa(empresa.Id, "Dora", "Diaz", "11111111H");
        var enD = Trabajador.DeEmpresa(empresa.Id, "Eva", "Egea", "22222222J");
        contexto.Trabajadores.AddRange(conDocumentoEnA, sinDocumentoEnB, conDocumentoEnC, sinDocumentoEnC, enD);
        await contexto.SaveChangesAsync();

        // El porcentaje solo cuenta inclusiones explícitas del Centro: D no incluye el tipo.
        contexto.TiposDocumentoCentros.AddRange(
            new TipoDocumentoCentro(tipo.Id, alDia.Id, incluido: true),
            new TipoDocumentoCentro(tipo.Id, sinDocumento.Id, incluido: true),
            new TipoDocumentoCentro(tipo.Id, aMedias.Id, incluido: true));
        var hoy = DiaDeNegocio.Hoy();
        contexto.Asignaciones.AddRange(
            new Asignacion(conDocumentoEnA.Id, alDia.Id, hoy),
            new Asignacion(sinDocumentoEnB.Id, sinDocumento.Id, hoy),
            new Asignacion(conDocumentoEnC.Id, aMedias.Id, hoy),
            new Asignacion(sinDocumentoEnC.Id, aMedias.Id, hoy),
            new Asignacion(enD.Id, sinRequisitos.Id, hoy));
        foreach (var trabajador in new[] { conDocumentoEnA, conDocumentoEnC })
            contexto.Documentos.Add(Documento.DeTrabajador(
                trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1))));
        await contexto.SaveChangesAsync();

        (_alDia, _sinDocumento, _aMedias, _sinRequisitos) = (alDia.Id, sinDocumento.Id, aMedias.Id, sinRequisitos.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_porcentaje_de_cada_Centro_es_el_mismo_por_los_tres_caminos()
    {
        var esperado = new Dictionary<Guid, int?>
        {
            [_alDia] = 100,
            [_sinDocumento] = 0,
            [_aMedias] = 50,
            [_sinRequisitos] = null
        };

        var sinEstado = await PorcentajesAsync(new ObtenerCentrosQuery(null, null, TamanoPagina: 50));
        var conEstadoPorPaginas = new Dictionary<Guid, int?>();
        for (var pagina = 1; pagina <= 2; pagina++)
            foreach (var (id, porcentaje) in await PorcentajesAsync(new ObtenerCentrosQuery(
                null, null, Pagina: pagina, TamanoPagina: 2, ConRecuentosPorEstado: true, ConResumenPorGrupo: true)))
                conEstadoPorPaginas.Add(id, porcentaje);
        var ordenPorCumplimiento = await PorcentajesAsync(new ObtenerCentrosQuery(
            null, null, OrdenarPor: nameof(CentroListaDto.CumplimientoPorcentaje), TamanoPagina: 50, ConRecuentosPorEstado: true));

        sinEstado.Should().BeEquivalentTo(esperado, "control: el camino sin estado calcula el porcentaje de la página");
        conEstadoPorPaginas.Should().BeEquivalentTo(esperado, "con estado, el porcentaje se calcula solo para cada página");
        ordenPorCumplimiento.Should().BeEquivalentTo(esperado, "ordenando por él, se calcula para todos antes de paginar");
    }

    [Fact]
    public async Task El_orden_por_cumplimiento_deja_el_mas_bajo_primero_y_sin_requisitos_al_final()
    {
        var ascendente = await IdsAsync(new ObtenerCentrosQuery(
            null, null, OrdenarPor: nameof(CentroListaDto.CumplimientoPorcentaje), TamanoPagina: 50));
        var descendente = await IdsAsync(new ObtenerCentrosQuery(
            null, null, OrdenarPor: nameof(CentroListaDto.CumplimientoPorcentaje), Descendente: true, TamanoPagina: 50));

        ascendente.Should().Equal(_sinDocumento, _aMedias, _alDia, _sinRequisitos);
        descendente.Should().Equal(_alDia, _aMedias, _sinDocumento, _sinRequisitos);
    }

    /// <summary>
    /// La propiedad que cambia el coste: con estado y sin ordenar por cumplimiento, el cálculo del cumplimiento
    /// recibe solo los Centros de la página; ordenando por él, todos los que pasan los filtros.
    /// </summary>
    [Fact]
    public async Task Con_estado_el_cumplimiento_se_pide_solo_para_la_pagina_salvo_al_ordenar_por_el()
    {
        var conEstado = new List<int>();
        await EjecutarAsync(new ObtenerCentrosQuery(null, null, TamanoPagina: 2, ConRecuentosPorEstado: true), conEstado);
        var ordenando = new List<int>();
        await EjecutarAsync(new ObtenerCentrosQuery(
            null, null, OrdenarPor: nameof(CentroListaDto.CumplimientoPorcentaje), TamanoPagina: 2, ConRecuentosPorEstado: true), ordenando);

        conEstado.Should().Equal([2], "una sola llamada, con los dos Centros de la página");
        ordenando.Should().Equal([4], "una sola llamada, con los cuatro Centros, antes de paginar");
    }

    private async Task<Dictionary<Guid, int?>> PorcentajesAsync(ObtenerCentrosQuery consulta) =>
        (await EjecutarAsync(consulta)).ToDictionary(c => c.Id, c => c.CumplimientoPorcentaje);

    private async Task<List<Guid>> IdsAsync(ObtenerCentrosQuery consulta) =>
        (await EjecutarAsync(consulta)).Select(c => c.Id).ToList();

    private async Task<IReadOnlyList<CentroListaDto>> EjecutarAsync(ObtenerCentrosQuery consulta, List<int>? centrosPorLlamadaDeCumplimiento = null)
    {
        await using var contexto = CrearContexto();
        var calculo = new CalculoContado(
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto),
            centrosPorLlamadaDeCumplimiento ?? []);
        var handler = new ObtenerCentrosQueryHandler(contexto, contexto, new AlcanceDatosServiceFalso(), calculo);
        return (await handler.Handle(consulta, CancellationToken.None)).Elementos;
    }

    /// <summary>El cálculo real, apuntando cuántos Centros recibe cada llamada a <c>CalcularCumplimientoAsync</c>.</summary>
    private sealed class CalculoContado(ICalculoEstadoCentroService real, List<int> centrosPorLlamada) : ICalculoEstadoCentroService
    {
        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => real.CalcularAsync(centroIds, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
        {
            centrosPorLlamada.Add(centroIds.Count);
            return real.CalcularCumplimientoAsync(centroIds, cancellationToken);
        }

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => real.ObtenerParesExigidosAsync(centroIds, cancellationToken);

        public Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => real.ObtenerParesDeEmpresaExigidosAsync(centroIds, cancellationToken);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
