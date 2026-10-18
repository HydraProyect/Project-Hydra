using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// El filtro y la agrupación «Por Cliente» de <see cref="ObtenerEmpresasQueryHandler"/> contra PostgreSQL: lo que
/// los tests unitarios (LINQ en memoria) no pueden decir es que las subconsultas correlacionadas —el filtro, el
/// orden por el primer Cliente y el Cliente principal de la página— se traducen a SQL, ni en el camino con estado
/// documental ni en el de sin él. El alcance (qué Empresas y qué Clientes ve cada rol) lo ata
/// <c>ObtenerEmpresasPorClienteQueryTests</c>.
/// </summary>
public class ObtenerEmpresasPorClienteEnSqlTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _aragon;
    private Guid _basauri;
    private Guid _castro;
    private Guid _umbrella;
    private Guid _cyberdyne;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var aragon = new Empresa("Aragon Montajes S.L.");
        var basauri = new Empresa("Basauri Obras S.L.");
        var castro = new Empresa("Castro Servicios S.L.");
        var umbrella = Empresa.CrearComoCliente("Umbrella Iberica S.A.", null, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var cyberdyne = Empresa.CrearComoCliente("Cyberdyne Iberica S.A.", null, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        contexto.Empresas.AddRange(aragon, basauri, castro, umbrella, cyberdyne);
        await contexto.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(aragon.Id, umbrella.Id, ahora));
        contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(aragon.Id, cyberdyne.Id, ahora));
        contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(basauri.Id, cyberdyne.Id, ahora));
        await contexto.SaveChangesAsync();

        (_aragon, _basauri, _castro, _umbrella, _cyberdyne) = (aragon.Id, basauri.Id, castro.Id, umbrella.Id, cyberdyne.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_filtro_por_Cliente_se_resuelve_en_SQL_con_y_sin_estado_documental(bool conRecuentosPorEstado)
    {
        await using var contexto = CrearContexto();

        var resultado = await CrearHandler(contexto).Handle(
            new ObtenerEmpresasQuery(null, ConRecuentosPorEstado: conRecuentosPorEstado, ClienteEmpresarialId: _umbrella),
            CancellationToken.None);

        resultado.Elementos.Select(e => e.Id).Should().Equal(_aragon);
        resultado.TotalElementos.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Agrupar_por_Cliente_ordena_en_SQL_y_trae_el_Cliente_principal_de_la_pagina(bool conRecuentosPorEstado)
    {
        await using var contexto = CrearContexto();

        var resultado = await CrearHandler(contexto).Handle(
            new ObtenerEmpresasQuery(
                null, OrdenarPor: nameof(EmpresaListaDto.ClientePrincipal), ConRecuentosPorEstado: conRecuentosPorEstado,
                ConClientePrincipal: true),
            CancellationToken.None);

        resultado.Elementos.Select(e => (e.Id, e.ClientePrincipal)).Should().Equal(
            (_aragon, "Cyberdyne Iberica S.A."),
            (_basauri, "Cyberdyne Iberica S.A."),
            (_castro, (string?)null));
    }

    private static ObtenerEmpresasQueryHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(contexto, new AlcanceDatosServiceFalso(),
            new CalculoEstadoDocumentalService(contexto, contexto, contexto),
            contexto, contexto, contexto, contexto,
            new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto));

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual));

        return new CaeManagerDbContext(builder.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
