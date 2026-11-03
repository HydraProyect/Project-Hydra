using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Sin conexión, sobre el modelo real: las delegaciones y el perfil de origen de la cuenta salen de una sola
/// sentencia, que es lo que impide leer el perfil de una cuenta de Soporte TALVEG cuya delegación se revoca a la
/// vez, y el perfil queda condicionado a la propia fila de la delegación.
/// </summary>
public class AccesosDelDestinoTests
{
    [Fact]
    public void Las_delegaciones_y_el_perfil_de_origen_salen_de_una_sola_sentencia()
    {
        using var contexto = CrearContextoSinConexion();

        var sql = DirectorioUsuariosTenant.AccesosDelDestino(contexto, Guid.NewGuid(), Guid.NewGuid()).ToQueryString();

        sql.Should().Contain("\"AsignacionesOperadorDelegado\"").And.Contain("\"AspNetUserRoles\"");
        sql.Trim().TrimEnd(';').Should().NotContain(";", "dos sentencias volverían a abrir la ventana de la revocación");
        var unionDelPerfil = sql.IndexOf("\"AspNetUserRoles\"", StringComparison.Ordinal);
        sql[unionDelPerfil..].Should().Contain("\"Proposito\" <> 'Soporte'", "el perfil se proyecta solo en la fila que no es de Soporte");
    }

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}