using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Architecture.Tests;

public class ConcesionPrivilegioSeMaterializaSinValidarTests
{
    /// <summary>
    /// <see cref="ConcesionPrivilegio.MaximoDiasDeVigencia"/> lo aplica el
    /// constructor con parámetros. Si EF Core materializara por él, una fila
    /// guardada antes del tope reventaría al leerse y dejaría sin acceso a quien
    /// ya lo tiene, y por eso se fija la elección del constructor.
    /// </summary>
    [Fact]
    public void EF_materializa_la_concesion_por_el_constructor_sin_parametros()
    {
        using var contexto = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql("Host=localhost;Database=solo-modelo;Username=x;Password=x").Options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });

        var tipo = (RuntimeEntityType)contexto.Model.FindEntityType(typeof(ConcesionPrivilegio))!;

        tipo.ConstructorBinding.Should().BeOfType<ConstructorBinding>()
            .Which.Constructor.GetParameters().Should().BeEmpty();
    }
}
