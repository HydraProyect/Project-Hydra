using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Exportacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Qué columnas y qué tablas entran en la exportación completa de un Tenant propietario, medido
/// sobre el modelo real de <see cref="CaeManagerDbContext"/> (sin conexión). Lo que no entra es tan
/// importante como lo que entra: ninguna columna cifrada en reposo, ningún hash de clave de API y
/// ninguna tabla sin Tenant (catálogos globales, asignaciones entre Tenants, plano de plataforma).
/// </summary>
public class ExportacionCompletaTenantTests
{
    private static readonly IModel Modelo = CrearContextoSinConexion().Model;

    [Fact]
    public void Ninguna_columna_cifrada_en_reposo_se_exporta()
    {
        var cifradas = Modelo.GetEntityTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.GetValueConverter() is { } c && c.ModelClrType == typeof(string) && c.ProviderClrType == typeof(string))
            .ToList();

        cifradas.Should().NotBeEmpty("guarda del instrumento: el modelo cifra credenciales con conversores de texto a texto");
        cifradas.Should().OnlyContain(p => ExportacionCompletaTenant.EsSecreta(p));
    }

    [Theory]
    [InlineData(typeof(CredencialAccesoEmpresa), "Contrasena")]
    [InlineData(typeof(CredencialAccesoEmpresa), "Usuario")]
    [InlineData(typeof(CredencialAccesoSubcontrata), "Contrasena")]
    [InlineData(typeof(AccesoPlataformaCaeEmpresa), "Contrasena")]
    [InlineData(typeof(CanalGestionDocumental), "Contrasena")]
    [InlineData(typeof(CredencialIntegracion), "RefreshToken")]
    [InlineData(typeof(CredencialIntegracion), "AccessToken")]
    [InlineData(typeof(CredencialConexionPlataformaCae), "SecretoApi")]
    [InlineData(typeof(SuscripcionWebhook), "ClientState")]
    [InlineData(typeof(LineaWhatsApp), "TokenAcceso")]
    [InlineData(typeof(EventoWebhook), "PayloadCrudo")]
    [InlineData(typeof(ClaveApi), "HashClave")]
    public void Los_secretos_conocidos_no_se_exportan(Type entidad, string propiedad)
    {
        var columna = Modelo.FindEntityType(entidad)!.FindProperty(propiedad);

        columna.Should().NotBeNull($"{entidad.Name}.{propiedad} tiene que seguir existiendo para que esta prueba mida algo");
        ExportacionCompletaTenant.EsSecreta(columna!).Should().BeTrue();
    }

    [Fact]
    public void Solo_se_exportan_tablas_con_Tenant_y_estan_las_del_dominio_CAE()
    {
        var tipos = ExportacionCompletaTenant.TiposExportables(Modelo).Select(t => t.ClrType).ToList();

        tipos.Should().OnlyContain(t => typeof(EntidadConTenant).IsAssignableFrom(t));
        tipos.Select(t => t.Name).Should().Contain(
            ["Empresa", "Trabajador", "Vehiculo", "Centro", "Documento", "Proyecto", "Visita", "Incidencia",
             "Conversacion", "Mensaje", "RegistroAuditoria"]);
        tipos.Select(t => t.Name).Should().NotContain(
            ["Tenant", "AsignacionOperacion", "AsignacionCartera", "ConcesionPrivilegio", "SesionPrivilegiada", "DelegacionTenant",
             "TareaAsistente", "TurnoTareaAsistente", "PasoTareaAsistente"]);
        tipos.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Congelada a propósito: una columna nueva en la cuenta (un secreto, un token) no entra en la
    /// exportación sin que alguien decida aquí que puede salir.
    /// </summary>
    [Fact]
    public void Las_columnas_exportadas_de_la_cuenta_son_exactamente_las_de_perfil()
    {
        var columnas = ExportacionCompletaTenant.ColumnasExportablesDeUsuario(
            Modelo.FindEntityType(typeof(CaeManager.Infrastructure.Identity.ApplicationUser))!).Select(p => p.Name);

        columnas.Should().BeEquivalentTo(
            "Id", "TenantId", "UserName", "Email", "EmailConfirmed", "PhoneNumber", "PhoneNumberConfirmed",
            "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled", "AccessFailedCount", "NombreCompleto", "Tema", "Idioma",
            "Avatar", "CoordinadorUsuarioId", "ClienteId", "DebeCambiarContrasena", "FechaCreacion", "UltimaActividadUtc",
            "PermisoConsultarAccesoDocumentosSensibles");
    }

    [Theory]
    [InlineData("almacen/2026/abc.pdf", ".pdf")]
    [InlineData("almacen/abc.PNG", ".png")]
    [InlineData("almacen/abc", "")]
    [InlineData("almacen/abc.p/../x", "")]
    [InlineData("almacen/abc.extensionlarga", "")]
    public void La_extension_del_fichero_exportado_sale_solo_de_una_clave_segura(string clave, string esperada) =>
        ExportacionCompletaTenant.ExtensionSegura(clave).Should().Be(esperada);

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
