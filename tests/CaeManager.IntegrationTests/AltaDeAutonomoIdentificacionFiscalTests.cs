using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// P1 — el alta de un autónomo, de extremo a extremo contra Postgres real.
/// Antes de este incremento la vía entera estaba cerrada para una persona
/// física: el validador de Application y <c>Empresa.EstablecerCif</c> exigían
/// un NIF de persona jurídica, así que un autónomo no podía existir como
/// Empresa sin inventarse un CIF.
///
/// Lo que aquí se prueba y no se puede probar en Domain ni en Application: que
/// el DNI y el NIE caben en la columna tal como está definida
/// (<c>HasMaxLength(9)</c>) y que el índice único <c>(TenantId, Cif)</c> los
/// trata igual que a un CIF — ambas propiedades viven en el esquema, no en el
/// agregado, y por eso este incremento no necesita migración.
/// </summary>
public class AltaDeAutonomoIdentificacionFiscalTests : IAsyncLifetime
{
    private const string DniDeLaAutonoma = "77189989B";
    private const string NieDelAutonomo = "X1234567L";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(DniDeLaAutonoma)]
    [InlineData(NieDelAutonomo)]
    public async Task Un_autonomo_se_da_de_alta_como_empresa_con_su_dni_o_su_nie(string identificacion)
    {
        Guid empresaId;
        await using (var contexto = CrearContexto())
        {
            var handler = new CrearEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto, contexto);

            var resultado = await handler.Handle(
                new CrearEmpresaCommand("Marta Ruiz Salas", identificacion, []), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue("un autónomo se identifica con su DNI o su NIE, no con un CIF");
            empresaId = resultado.Valor;
        }

        await using var verificacion = CrearContexto();
        var empresa = await verificacion.Empresas.SingleAsync(e => e.Id == empresaId);
        empresa.Cif.Should().Be(identificacion, "la columna admite los nueve caracteres de un DNI o un NIE sin migración");
    }

    /// <summary>
    /// El índice único <c>(TenantId, Cif)</c> no distingue el tipo de
    /// documento: dos Empresas con el mismo DNI dentro del mismo Tenant
    /// propietario siguen siendo un duplicado, igual que con un CIF. Se
    /// comprueba por la vía del comando, que es la que ve el usuario.
    /// </summary>
    [Fact]
    public async Task Dos_empresas_con_el_mismo_dni_en_el_mismo_tenant_son_un_duplicado()
    {
        await using (var contexto = CrearContexto())
        {
            var handler = new CrearEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto, contexto);

            var primera = await handler.Handle(
                new CrearEmpresaCommand("Marta Ruiz Salas", DniDeLaAutonoma, []), CancellationToken.None);

            primera.EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new CrearEmpresaCommandHandler(
                new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto, contexto);

            var segunda = await handler.Handle(
                new CrearEmpresaCommand("Marta Ruiz Salas Consultoría", DniDeLaAutonoma, []), CancellationToken.None);

            segunda.EsExitoso.Should().BeFalse("el mismo documento dentro del mismo Tenant propietario es la misma persona");
        }
    }

    /// <summary>
    /// El dígito de control se sigue exigiendo: relajar el tipo admitido no
    /// relaja la comprobación. La letra correcta de 77189989 es B, no A.
    /// </summary>
    [Fact]
    public async Task Un_dni_con_la_letra_de_control_incorrecta_no_da_de_alta_a_nadie()
    {
        await using var contexto = CrearContexto();
        var handler = new CrearEmpresaCommandHandler(
            new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto, contexto);

        var accion = async () => await handler.Handle(
            new CrearEmpresaCommand("Marta Ruiz Salas", "77189989A", []), CancellationToken.None);

        await accion.Should().ThrowAsync<ArgumentException>();
        (await contexto.Empresas.CountAsync()).Should().Be(0, "nada se escribió");
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
