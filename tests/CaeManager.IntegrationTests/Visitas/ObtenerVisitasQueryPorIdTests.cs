using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Filtro por id de la lista de Visitas: lo usa /visitas para refrescar una fila en sitio tras
/// guardar su edición. Dos propiedades, contra PostgreSQL real porque las dos son filtros SQL:
/// la fila pedida por id es la misma que da la carga de página, y el filtro va después del
/// alcance, así que solo estrecha.
/// </summary>
public class ObtenerVisitasQueryPorIdTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private Guid _centroNorteId;
    private Guid _visitaNorteId;
    private Guid _visitaSurId;
    private Guid _visitaPasadaId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15, horasAvisoVisita: 48, horasCriticasVisita: 24));
        var clienteEmpresarial = Empresa.CrearComoCliente("Cliente Visita Por Id S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Visita Por Id S.L.", "B87654323");
        contexto.Empresas.AddRange(clienteEmpresarial, empresa);
        var norte = new Centro(clienteEmpresarial.Id, empresa.Id, "Centro Norte Por Id");
        var sur = new Centro(clienteEmpresarial.Id, empresa.Id, "Centro Sur Por Id");
        contexto.Centros.AddRange(norte, sur);

        var visitaNorte = new Visita(norte.Id, _hoy.AddDays(1), _hoy.AddDays(2), notas: null);
        var visitaSur = new Visita(sur.Id, _hoy.AddDays(3), _hoy.AddDays(3), notas: null);
        var visitaPasada = new Visita(norte.Id, _hoy.AddDays(-9), _hoy.AddDays(-8), notas: null);
        contexto.Visitas.AddRange(visitaNorte, visitaSur, visitaPasada);
        await contexto.SaveChangesAsync();

        (_centroNorteId, _visitaNorteId, _visitaSurId, _visitaPasadaId) = (norte.Id, visitaNorte.Id, visitaSur.Id, visitaPasada.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private static ObtenerVisitasQuery PorId(Guid id) =>
        new(Busqueda: null, SoloActivas: false, NotificadoCliente: null, VisitaId: id);

    [Fact]
    public async Task La_fila_pedida_por_id_es_la_misma_que_da_la_carga_de_pagina()
    {
        await using var lectura = CrearContexto();
        var pagina = await Handler(lectura, new AlcanceDatosServiceFalso()).Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null), CancellationToken.None);
        pagina.Elementos.Select(v => v.Id).Should().Equal([_visitaNorteId, _visitaSurId], "punto de partida: las dos activas, por fecha");

        var porId = await Handler(lectura, new AlcanceDatosServiceFalso()).Handle(PorId(_visitaNorteId), CancellationToken.None);

        porId.TotalElementos.Should().Be(1);
        porId.Elementos.Should().ContainSingle().Which.Should().BeEquivalentTo(pagina.Elementos.Single(v => v.Id == _visitaNorteId));
    }

    /// <summary>La página la pide sin sus filtros: una Visita que ya no es activa también sale por su id.</summary>
    [Fact]
    public async Task Una_visita_que_ya_no_cumple_Solo_activas_sale_por_su_id()
    {
        await using var lectura = CrearContexto();

        var porId = await Handler(lectura, new AlcanceDatosServiceFalso()).Handle(PorId(_visitaPasadaId), CancellationToken.None);

        porId.Elementos.Should().ContainSingle().Which.FechaFin.Should().Be(_hoy.AddDays(-8));
    }

    /// <summary>El filtro por id va después del alcance: solo estrecha, nunca enseña una Visita de un Centro que el usuario no ve.</summary>
    [Fact]
    public async Task El_filtro_por_id_no_devuelve_una_visita_fuera_del_alcance()
    {
        await using var lectura = CrearContexto();
        var soloNorte = new AlcanceDatosServiceFalso(centroIds: [_centroNorteId]);

        var fuera = await Handler(lectura, soloNorte).Handle(PorId(_visitaSurId), CancellationToken.None);
        var dentro = await Handler(lectura, soloNorte).Handle(PorId(_visitaNorteId), CancellationToken.None);

        fuera.Elementos.Should().BeEmpty();
        fuera.TotalElementos.Should().Be(0);
        dentro.Elementos.Should().ContainSingle().Which.CentroNombre.Should().Be("Centro Norte Por Id");
    }

    private static ObtenerVisitasQueryHandler Handler(CaeManagerDbContext contexto, AlcanceDatosServiceFalso alcance) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto);

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
