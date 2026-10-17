using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.BusquedaGlobal;

/// <summary>El documento completo encuentra dentro de cartera; los fragmentos no enumeran DNI.</summary>
public class BuscarGlobalSinDniTests : IAsyncLifetime
{
    private const string DniDeEmpresa = "12345678Z";
    private const string DniDeSubcontrata = "87654321X";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorDeEmpresa;
    private Guid _trabajadorDeSubcontrata;
    private Guid _empresa;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var empresa = new Empresa("Montajes Ebro S.L.", "B12345674");
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios Delta S.L.", null, "Gestionada");
        contexto.Empresas.AddRange(empresa, subcontrata);
        await contexto.SaveChangesAsync();

        var deEmpresa = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez Ibáñez", DniDeEmpresa);
        var deSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Juana", "Pérez Soler", DniDeSubcontrata);
        contexto.Trabajadores.AddRange(deEmpresa, deSubcontrata);
        await contexto.SaveChangesAsync();

        _trabajadorDeEmpresa = deEmpresa.Id;
        _trabajadorDeSubcontrata = deSubcontrata.Id;
        _empresa = empresa.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_cif_completo_encuentra_solo_dentro_de_cartera()
    {
        var resultado = await BuscarAsync("B12345674");
        resultado.Empresas.Should().ContainSingle().Which.Id.Should().Be(_empresa);

        await using var contexto = CrearContexto();
        var handler = new BuscarGlobalQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(empresaIds: [], clienteIds: [], subcontrataIds: []));
        var fuera = await handler.Handle(new BuscarGlobalQuery("B12345674"), CancellationToken.None);
        fuera.Empresas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DniDeEmpresa)]
    [InlineData("12345678z")]
    [InlineData("12.345.678-Z")]
    public async Task El_documento_completo_encuentra_al_trabajador_sin_exponer_el_dni(string termino)
    {
        var resultado = await BuscarAsync(termino);
        resultado.Trabajadores.Should().ContainSingle().Which.Id.Should().Be(_trabajadorDeEmpresa);
        resultado.Trabajadores.Single().Subtitulo.Should().Be("Montajes Ebro S.L.");
    }

    [Fact]
    public async Task El_documento_completo_no_encuentra_fuera_de_cartera()
    {
        await using var contexto = CrearContexto();
        var handler = new BuscarGlobalQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(trabajadorIds: []));
        var resultado = await handler.Handle(new BuscarGlobalQuery(DniDeEmpresa), CancellationToken.None);
        resultado.Trabajadores.Should().BeEmpty();
    }
    [Theory]
    [InlineData("5678")]
    [InlineData("78Z")]
    [InlineData("12")]
    public async Task Un_fragmento_no_encuentra_ningun_trabajador(string termino)
    {
        var resultado = await BuscarAsync(termino);

        resultado.Trabajadores.Should().BeEmpty(
            $"«{termino}» solo casa con el DNI, y Ctrl+K no busca por fragmentos de DNI");
    }

    [Fact]
    public async Task Se_sigue_encontrando_por_nombre_y_por_apellidos()
    {
        // Control positivo del test de arriba: los dos Trabajadores existen y
        // son visibles con este alcance; si no saliesen aquí, el vacío de
        // arriba no probaría nada.
        (await BuscarAsync("Juan")).Trabajadores.Select(t => t.Id)
            .Should().BeEquivalentTo([_trabajadorDeEmpresa, _trabajadorDeSubcontrata]);
        (await BuscarAsync("soler")).Trabajadores.Should().ContainSingle()
            .Which.Id.Should().Be(_trabajadorDeSubcontrata);
    }

    [Fact]
    public async Task El_subtitulo_es_la_organizacion_empleadora_y_nunca_el_dni()
    {
        var trabajadores = (await BuscarAsync("Juan")).Trabajadores;

        trabajadores.Should().HaveCount(2);
        trabajadores.Single(t => t.Id == _trabajadorDeEmpresa).Subtitulo.Should().Be("Montajes Ebro S.L.");
        trabajadores.Single(t => t.Id == _trabajadorDeSubcontrata).Subtitulo.Should().Be("Andamios Delta S.L.");

        foreach (var item in trabajadores)
        {
            // Todo el DTO, no solo el subtítulo: ni título ni URL pueden llevarlo.
            string[] campos = [item.Titulo, item.Subtitulo ?? string.Empty, item.UrlDestino];
            campos.Should().NotContain(c => c.Contains(DniDeEmpresa) || c.Contains(DniDeSubcontrata));
        }
    }

    private async Task<ResultadoBusquedaGlobalDto> BuscarAsync(string termino)
    {
        await using var contexto = CrearContexto();
        var handler = new BuscarGlobalQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso());

        return await handler.Handle(new BuscarGlobalQuery(termino), CancellationToken.None);
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
