using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// DEC-53/DEC-62, hallazgo de revisión de Codex: un test contra un fake en
/// memoria (<c>EmpresasQueryContextFalso</c>) no puede distinguir "la
/// proyección SQL nunca incluye la columna Contrasena" de "se materializa la
/// fila entera y luego se descarta el campo" — las dos producen el mismo
/// DTO de salida. Por eso este test ejecuta el <b>handler real</b> contra
/// Postgres real y captura el SQL que EF Core genera de verdad (vía
/// <c>LogTo</c>), en vez de reconstruir la consulta a mano: si alguien
/// cambia el handler para materializar la entidad completa antes de
/// proyectar —el mismo defecto que <see cref="CredencialAccesoEmpresaSinContrasenaDto"/>
/// existe para evitar—, este test lo detecta.
/// </summary>
public class ObtenerCredencialAccesoEmpresaSinContrasenaQuerySqlTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly IDataProtectionProvider _dataProtectionProvider = new EphemeralDataProtectionProvider();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_handler_real_genera_SQL_sin_la_columna_Contrasena()
    {
        var empresa = new Empresa("Empresa con credencial S.L.");
        var credencial = new CredencialAccesoEmpresa(
            empresa.Id, "https://portal.example", "campo", "usuario", "secreta-de-verdad", "notas");

        await using (var contextoEscritura = CrearContexto())
        {
            contextoEscritura.Empresas.Add(empresa);
            contextoEscritura.CredencialesAccesoEmpresa.Add(credencial);
            await contextoEscritura.SaveChangesAsync();
        }

        var sqlCapturado = new List<string>();
        await using var contextoLectura = CrearContexto(sql => sqlCapturado.Add(sql));

        var handler = new ObtenerCredencialAccesoEmpresaSinContrasenaQueryHandler(
            contextoLectura, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(
            new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(empresa.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.Usuario.Should().Be("usuario");

        var sentenciasSelect = sqlCapturado.Where(s => s.Contains("SELECT", StringComparison.OrdinalIgnoreCase)).ToList();
        sentenciasSelect.Should().NotBeEmpty("control positivo: el handler sí debe haber ejecutado alguna consulta");
        sentenciasSelect.Should().OnlyContain(
            s => !s.Contains("\"Contrasena\"", StringComparison.OrdinalIgnoreCase),
            "la proyección no debe pedir la columna cifrada — ni siquiera para descartarla después");
    }

    private CaeManagerDbContext CrearContexto(Action<string>? capturarSql = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor());

        if (capturarSql is not null)
            builder = builder.LogTo(capturarSql, Microsoft.Extensions.Logging.LogLevel.Information);

        return new CaeManagerDbContext(builder.Options, _dataProtectionProvider, tenantActual);
    }
}
