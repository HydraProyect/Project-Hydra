using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Lo que <c>AuditoriaInterceptor</c> copia a <c>RegistrosAuditoria</c> de las tablas de Identity que
/// <c>cae_app_soporte</c> no puede leer. La retirada de <c>AspNetUserLogins.ProviderKey</c> (#1903) no protege
/// el identificador si el historial lo copia, porque Soporte TALVEG sí lee <c>RegistrosAuditoria</c>. Ejecuta
/// el constructor real de registros sobre un <c>ChangeTracker</c> sin conexión (mismo mecanismo que
/// <c>FichaCentroModeloTests</c>); la prueba contra PostgreSQL con RLS está en
/// <c>AuditoriaDeIdentidadRestanteTests</c>.
/// </summary>
public class AuditoriaDeIdentidadEnmascaraSecretosTests
{
    private const string Proveedor = "Microsoft";
    private const string ClaveDelProveedor = "oid-secreto-del-directorio";
    private const string ValorDelToken = "CLAVETOTPENCLARO";

    [Fact]
    public void Vincular_un_login_externo_no_copia_el_ProviderKey_pero_conserva_el_proveedor()
    {
        using var contexto = CrearContexto();
        contexto.Set<IdentityUserLogin<Guid>>().Add(
            new IdentityUserLogin<Guid> { LoginProvider = Proveedor, ProviderKey = ClaveDelProveedor, ProviderDisplayName = Proveedor, UserId = Guid.NewGuid() });

        var registro = Registros(contexto).Should().ContainSingle().Subject;

        registro.EntidadTipo.Should().Be(EntidadTipoAuditoria.LoginExterno);
        registro.Accion.Should().Be("Creado");
        registro.DatosDespues.Should().NotContain(ClaveDelProveedor).And.Contain("***");
        registro.DatosDespues.Should().Contain(nameof(IdentityUserLogin<Guid>.LoginProvider)).And.Contain(Proveedor);
    }

    [Fact]
    public void Desvincular_un_login_externo_no_copia_el_ProviderKey_pero_conserva_el_proveedor()
    {
        using var contexto = CrearContexto();
        var login = new IdentityUserLogin<Guid> { LoginProvider = Proveedor, ProviderKey = ClaveDelProveedor, ProviderDisplayName = Proveedor, UserId = Guid.NewGuid() };
        contexto.Attach(login);
        contexto.Remove(login);

        var registro = Registros(contexto).Should().ContainSingle().Subject;

        registro.Accion.Should().Be("Eliminado");
        registro.DatosAntes.Should().NotContain(ClaveDelProveedor).And.Contain("***");
        registro.DatosAntes.Should().Contain(Proveedor);
    }

    [Fact]
    public void Un_token_de_usuario_no_copia_su_Value_pero_conserva_su_nombre()
    {
        using var contexto = CrearContexto();
        contexto.Set<IdentityUserToken<Guid>>().Add(
            new IdentityUserToken<Guid> { UserId = Guid.NewGuid(), LoginProvider = "[AspNetUserStore]", Name = "AuthenticatorKey", Value = ValorDelToken });

        var registro = Registros(contexto).Should().ContainSingle().Subject;

        registro.EntidadTipo.Should().Be(EntidadTipoAuditoria.TokenDeUsuario);
        registro.DatosDespues.Should().NotContain(ValorDelToken).And.Contain("***");
        registro.DatosDespues.Should().Contain("AuthenticatorKey");
    }

    private static List<RegistroAuditoria> Registros(CaeManagerDbContext contexto)
    {
        var metodo = typeof(AuditoriaInterceptor).GetMethod("ConstruirRegistros", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<RegistroAuditoria>)metodo.Invoke(null, [contexto, ActorAuditoria.Normal(Guid.NewGuid())])!;
    }

    private static CaeManagerDbContext CrearContexto() => new(
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x").Options,
        new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
}
