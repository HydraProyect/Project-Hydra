using CaeManager.Application.Alertas;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// El listado de Trabajadores filtra por Delegación y por Oficio del catálogo de su Empresa, ordena para «Agrupar» por
/// ellos y trae sus nombres, con las dos formas del handler (la normal y la que calcula el estado documental en SQL).
/// El alcance de Asignación de Cartera sigue recortando: el filtro solo estrecha.
/// </summary>
public class ObtenerTrabajadoresPorDelegacionEmpresaYOficioTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _empresaA, _empresaB, _delegacionMadridA, _delegacionBilbaoA, _delegacionMadridB, _oficioSoldadorA, _oficioAndamieroA;
    private Guid _madridSoldador, _bilbaoAndamiero, _sinCatalogo, _madridDeB, _deSubcontrata;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var empresaA = new Empresa("Alfa Montajes S.L.");
        var empresaB = new Empresa("Beta Obras S.L.");
        var subcontrata = new Empresa("Gamma Subcontrata S.L.");
        contexto.Empresas.AddRange(empresaA, empresaB, subcontrata);
        await contexto.SaveChangesAsync();

        var madridA = new DelegacionEmpresa(empresaA.Id, "Madrid");
        var bilbaoA = new DelegacionEmpresa(empresaA.Id, "Bilbao");
        var madridB = new DelegacionEmpresa(empresaB.Id, "Madrid");
        var soldadorA = new OficioEmpresa(empresaA.Id, "Soldador");
        var andamieroA = new OficioEmpresa(empresaA.Id, "Andamiero");
        contexto.DelegacionesEmpresa.AddRange(madridA, bilbaoA, madridB);
        contexto.OficiosEmpresa.AddRange(soldadorA, andamieroA);

        var t1 = Trabajador.DeEmpresa(empresaA.Id, "Ana", "Uno", "00000001R");
        var t2 = Trabajador.DeEmpresa(empresaA.Id, "Beto", "Dos", "00000002W");
        var t3 = Trabajador.DeEmpresa(empresaA.Id, "Carla", "Tres", "00000003A");
        var t4 = Trabajador.DeEmpresa(empresaB.Id, "Dani", "Cuatro", "00000004G");
        var t5 = Trabajador.DeSubcontrata(subcontrata.Id, "Eva", "Cinco", "00000005M");
        contexto.Trabajadores.AddRange(t1, t2, t3, t4, t5);
        await contexto.SaveChangesAsync();

        t1.FijarDelegacionEmpresaYOficio(madridA, soldadorA);
        t2.FijarDelegacionEmpresaYOficio(bilbaoA, andamieroA);
        t4.FijarDelegacionEmpresaYOficio(madridB, null);
        await contexto.SaveChangesAsync();

        (_empresaA, _empresaB) = (empresaA.Id, empresaB.Id);
        (_delegacionMadridA, _delegacionBilbaoA, _delegacionMadridB) = (madridA.Id, bilbaoA.Id, madridB.Id);
        (_oficioSoldadorA, _oficioAndamieroA) = (soldadorA.Id, andamieroA.Id);
        (_madridSoldador, _bilbaoAndamiero, _sinCatalogo, _madridDeB, _deSubcontrata) = (t1.Id, t2.Id, t3.Id, t4.Id, t5.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task<ResultadoPaginado<TrabajadorListaDto>> EjecutarAsync(
        ObtenerTrabajadoresQuery consulta, IReadOnlyList<Guid>? trabajadoresVisibles = null)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerTrabajadoresQueryHandler(
            contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(trabajadorIds: trabajadoresVisibles),
            new CalculoEstadoDocumentalService(contexto, contexto, contexto), contexto, contexto,
            new DocumentosFaltantesService(contexto, contexto, contexto));
        return await handler.Handle(consulta, CancellationToken.None);
    }

    /// <summary>La misma pregunta por los dos caminos del handler: el normal y el que calcula el estado en SQL.</summary>
    public static TheoryData<bool> Caminos => [false, true];

    [Theory]
    [MemberData(nameof(Caminos))]
    public async Task Filtrar_por_DelegacionEmpresa_deja_solo_a_los_de_ese_catalogo_con_sus_nombres(bool conRecuentos)
    {
        var resultado = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, DelegacionEmpresaId: _delegacionMadridA, TamanoPagina: 50, ConRecuentosPorEstado: conRecuentos));

        resultado.TotalElementos.Should().Be(1, "el total ya es el del filtro, sin segunda consulta de recuento");
        var fila = resultado.Elementos.Should().ContainSingle().Which;
        fila.Id.Should().Be(_madridSoldador);
        (fila.DelegacionEmpresaId, fila.DelegacionEmpresaNombre).Should().Be((_delegacionMadridA, "Madrid"));
        (fila.OficioEmpresaId, fila.OficioNombre).Should().Be((_oficioSoldadorA, "Soldador"));
    }

    [Theory]
    [MemberData(nameof(Caminos))]
    public async Task Filtrar_por_Oficio_y_por_DelegacionEmpresa_se_componen(bool conRecuentos)
    {
        var soloOficio = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, OficioEmpresaId: _oficioAndamieroA, TamanoPagina: 50, ConRecuentosPorEstado: conRecuentos));
        soloOficio.Elementos.Select(t => t.Id).Should().Equal(_bilbaoAndamiero);

        var contradictorios = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, DelegacionEmpresaId: _delegacionBilbaoA, OficioEmpresaId: _oficioSoldadorA, TamanoPagina: 50, ConRecuentosPorEstado: conRecuentos));
        contradictorios.TotalElementos.Should().Be(0, "nadie es Soldador en Bilbao");

        var conEmpresa = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, EmpresaId: _empresaA, DelegacionEmpresaId: _delegacionBilbaoA, OficioEmpresaId: _oficioAndamieroA,
            TamanoPagina: 50, ConRecuentosPorEstado: conRecuentos));
        conEmpresa.Elementos.Select(t => t.Id).Should().Equal(_bilbaoAndamiero);
    }

    [Fact]
    public async Task Una_DelegacionEmpresa_de_otra_Empresa_no_devuelve_a_nadie_de_esta()
    {
        var resultado = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, EmpresaId: _empresaA, DelegacionEmpresaId: _delegacionMadridB, TamanoPagina: 50));

        resultado.Elementos.Should().BeEmpty("«Madrid» de Beta no es un catálogo de Alfa, aunque se llamen igual");
        var deBeta = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, EmpresaId: _empresaB, DelegacionEmpresaId: _delegacionMadridB, TamanoPagina: 50));
        deBeta.Elementos.Select(t => t.Id).Should().Equal(_madridDeB);
    }

    [Theory]
    [MemberData(nameof(Caminos))]
    public async Task El_filtro_solo_estrecha_el_alcance_de_cartera_sigue_recortando(bool conRecuentos)
    {
        // Quien solo ve a Beto y a Carla no ve a Ana aunque filtre por la Delegación de Ana.
        var visibles = new[] { _bilbaoAndamiero, _sinCatalogo };

        var madrid = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, DelegacionEmpresaId: _delegacionMadridA, TamanoPagina: 50, ConRecuentosPorEstado: conRecuentos), visibles);
        madrid.TotalElementos.Should().Be(0);
        madrid.Elementos.Should().BeEmpty();

        var bilbao = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, DelegacionEmpresaId: _delegacionBilbaoA, TamanoPagina: 50, ConRecuentosPorEstado: conRecuentos), visibles);
        bilbao.Elementos.Select(t => t.Id).Should().Equal(_bilbaoAndamiero);

        var agrupado = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, TamanoPagina: 50, OrdenarPor: nameof(TrabajadorListaDto.DelegacionEmpresaNombre), ConRecuentosPorEstado: conRecuentos), visibles);
        agrupado.Elementos.Select(t => t.Id).Should().Equal(_bilbaoAndamiero, _sinCatalogo);
    }

    [Theory]
    [MemberData(nameof(Caminos))]
    public async Task Ordenar_por_DelegacionEmpresaNombre_agrupa_por_nombre_y_Empresa_y_deja_al_final_a_los_sin_catalogo(bool conRecuentos)
    {
        var resultado = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, TamanoPagina: 50, OrdenarPor: nameof(TrabajadorListaDto.DelegacionEmpresaNombre), ConRecuentosPorEstado: conRecuentos));

        // Bilbao (Alfa), Madrid (Alfa), Madrid (Beta): el mismo nombre en dos Empresas son dos grupos, uno detrás de otro.
        resultado.Elementos.Take(3).Select(t => t.Id).Should().Equal(_bilbaoAndamiero, _madridSoldador, _madridDeB);
        resultado.Elementos.Skip(3).Select(t => t.Id).Should().BeEquivalentTo([_sinCatalogo, _deSubcontrata]);
        resultado.Elementos.Skip(3).Should().OnlyContain(t => t.DelegacionEmpresaNombre == null && t.DelegacionEmpresaId == null,
            "un Trabajador de Subcontrata cae en «Sin delegación»");
    }

    [Theory]
    [MemberData(nameof(Caminos))]
    public async Task Ordenar_por_OficioNombre_agrupa_por_Oficio_y_deja_al_final_a_los_sin_Oficio(bool conRecuentos)
    {
        var resultado = await EjecutarAsync(new ObtenerTrabajadoresQuery(
            null, TamanoPagina: 50, OrdenarPor: nameof(TrabajadorListaDto.OficioNombre), ConRecuentosPorEstado: conRecuentos));

        resultado.Elementos.Take(2).Select(t => t.Id).Should().Equal(_bilbaoAndamiero, _madridSoldador);
        resultado.Elementos.Skip(2).Select(t => t.Id).Should().BeEquivalentTo([_sinCatalogo, _madridDeB, _deSubcontrata]);
    }

    [Theory]
    [MemberData(nameof(Caminos))]
    public async Task Agrupado_y_paginado_no_repite_ni_pierde_filas_entre_paginas(bool conRecuentos)
    {
        var vistos = new List<Guid>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var resultado = await EjecutarAsync(new ObtenerTrabajadoresQuery(
                null, Pagina: pagina, TamanoPagina: 2, OrdenarPor: nameof(TrabajadorListaDto.DelegacionEmpresaNombre), ConRecuentosPorEstado: conRecuentos));
            resultado.TotalElementos.Should().Be(5);
            vistos.AddRange(resultado.Elementos.Select(t => t.Id));
        }

        vistos.Should().OnlyHaveUniqueItems().And.HaveCount(5);
        vistos.Take(3).Should().Equal(_bilbaoAndamiero, _madridSoldador, _madridDeB);
    }

    [Fact]
    public async Task Sin_filtro_ni_agrupacion_la_lista_es_la_de_siempre_y_trae_los_nombres()
    {
        var resultado = await EjecutarAsync(new ObtenerTrabajadoresQuery(null, TamanoPagina: 50));

        resultado.TotalElementos.Should().Be(5, "los Trabajadores sin catálogo no se pierden por el join");
        resultado.Elementos.Single(t => t.Id == _bilbaoAndamiero).OficioNombre.Should().Be("Andamiero");
        resultado.Elementos.Single(t => t.Id == _sinCatalogo).OficioNombre.Should().BeNull();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
