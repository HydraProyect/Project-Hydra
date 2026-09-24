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

/// <summary>
/// El buscador global (Ctrl/Cmd+K) ni busca por DNI ni lo muestra (decisión
/// delegada en Codex por el propietario, 2026-09-24, que extiende P4 «base
/// general sin DNI» a Ctrl+K). Lo tiene todo usuario autenticado —también un
/// usuario Consulta de un Operador CAE externo, con alcance sobre el Tenant
/// beneficiario entero—, y buscar por fragmento de DNI con el DNI completo al
/// lado permitía enumerarlo tecleando. El subtítulo pasa a ser la razón social
/// de la organización empleadora.
///
/// Todo se mide con alcance total (<see cref="AlcanceDatosServiceFalso"/> sin
/// listas = Administrador/DireccionCae/Consulta), que es el caso más amplio: si
/// ahí no sale el DNI, con cartera tampoco.
/// </summary>
public class BuscarGlobalSinDniTests : IAsyncLifetime
{
    private const string DniDeEmpresa = "12345678Z";
    private const string DniDeSubcontrata = "87654321X";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorDeEmpresa;
    private Guid _trabajadorDeSubcontrata;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var empresa = new Empresa("Montajes Ebro S.L.");
        var subcontrata = Empresa.CrearComoSubcontrata("Andamios Delta S.L.", null, "Gestionada");
        contexto.Empresas.AddRange(empresa, subcontrata);
        await contexto.SaveChangesAsync();

        var deEmpresa = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez Ibáñez", DniDeEmpresa);
        var deSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Juana", "Pérez Soler", DniDeSubcontrata);
        contexto.Trabajadores.AddRange(deEmpresa, deSubcontrata);
        await contexto.SaveChangesAsync();

        _trabajadorDeEmpresa = deEmpresa.Id;
        _trabajadorDeSubcontrata = deSubcontrata.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(DniDeEmpresa)]
    [InlineData("12345678z")]
    [InlineData("5678")]
    [InlineData("78Z")]
    [InlineData("12")]
    public async Task Un_dni_completo_o_un_fragmento_no_encuentra_ningun_trabajador(string termino)
    {
        var resultado = await BuscarAsync(termino);

        resultado.Trabajadores.Should().BeEmpty(
            $"«{termino}» solo casa con el DNI, y Ctrl+K ya no busca por DNI");
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
