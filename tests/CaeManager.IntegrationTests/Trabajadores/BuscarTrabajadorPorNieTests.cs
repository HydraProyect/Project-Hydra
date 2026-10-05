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
/// El buscador de /trabajadores identifica a un trabajador extranjero por su NIE (decisión del propietario
/// al pasar la lista a la fase 1: el orden por DNI se pierde, la búsqueda por documento no). El NIE se guarda
/// en el mismo campo que el DNI, normalizado en mayúsculas; aquí se fija contra Postgres en los dos caminos de
/// la consulta: el orden por estado documental (el de serie de la pantalla) y el orden por apellidos.
/// </summary>
public class BuscarTrabajadorPorNieTests : IAsyncLifetime
{
    private const string Nie = "X1005105M";
    private const string Dni = "60005002A";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var empresa = new Empresa("Montajes Skynet S.L.");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        contexto.Trabajadores.AddRange(
            Trabajador.DeEmpresa(empresa.Id, "Carla", "Molina Ríos", Nie),
            Trabajador.DeEmpresa(empresa.Id, "Paula", "Campos Lara", Dni));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("X1005105M", "EstadoDocumental")] // NIE completo, orden de serie
    [InlineData("x1005105m", "EstadoDocumental")] // en minúsculas
    [InlineData("1005105", "EstadoDocumental")]   // solo la parte numérica
    [InlineData("X1005105M", "Apellidos")]        // el otro camino de la consulta
    [InlineData("x1005105m", "Apellidos")]
    public async Task Buscar_por_el_NIE_encuentra_al_trabajador(string termino, string ordenarPor)
    {
        var encontrados = await BuscarAsync(termino, ordenarPor);

        encontrados.Should().Equal("Molina Ríos");
    }

    /// <summary>Control positivo: el mismo buscador encuentra por DNI al otro trabajador, no a los dos.</summary>
    [Fact]
    public async Task Buscar_por_un_DNI_encuentra_solo_a_su_trabajador()
    {
        var encontrados = await BuscarAsync(Dni, "EstadoDocumental");

        encontrados.Should().Equal("Campos Lara");
    }

    private async Task<IReadOnlyList<string>> BuscarAsync(string termino, string ordenarPor)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerTrabajadoresQueryHandler(
            contexto, contexto, contexto, contexto,
            new AlcanceDatosServiceFalso(), new CalculoEstadoDocumentalService(contexto, contexto));

        var resultado = await handler.Handle(
            new ObtenerTrabajadoresQuery(termino, OrdenarPor: ordenarPor, TamanoPagina: 50), CancellationToken.None);
        return resultado.Elementos.Select(t => t.Apellidos).ToList();
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
