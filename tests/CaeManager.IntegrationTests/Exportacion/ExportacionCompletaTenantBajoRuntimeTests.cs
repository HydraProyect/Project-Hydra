using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Exportacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Exportacion;

/// <summary>
/// Exportación completa de un Tenant propietario leída <b>como <c>cae_app_runtime</c></b>, sin usuario
/// (igual que el modo de CLI), con los interceptores de sellado y de sesión RLS de producción. Dos
/// Tenants propietarios con su Empresa, su Administrador y una credencial de portal cada uno: el zip
/// del primero trae lo suyo y nada del segundo, ni la contraseña de su propio portal. Y se niega si
/// la pide quien no es Administrador del Tenant, o si la identidad de la conexión se salta RLS. La
/// siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class ExportacionCompletaTenantBajoRuntimeTests : IAsyncLifetime
{
    private const string ContrasenaPortal = "contrasena-del-portal-que-no-sale";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _empresaA;
    private Guid _empresaB;
    private string _administradorA = null!;
    private string _administradorB = null!;
    private string _consultaA = null!;

    public async Task InitializeAsync()
    {
        _propietario = CrearPropietario();
        await _propietario.Database.MigrateAsync();

        var tenantA = new Tenant("Tenant propietario exportado");
        var tenantB = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantA, tenantB);
        await _propietario.SaveChangesAsync();
        (_tenantA, _tenantB) = (tenantA.Id, tenantB.Id);

        var administrador = await _propietario.Roles.Where(r => r.Name == Roles.Administrador).Select(r => r.Id).SingleAsync();
        var consulta = await _propietario.Roles.Where(r => r.Name == Roles.Consulta).Select(r => r.Id).SingleAsync();

        _empresaA = await SembrarAsync(_tenantA, "Montajes Springfield");
        _empresaB = await SembrarAsync(_tenantB, "Montajes Shelbyville");
        (_, _administradorA) = await CuentaAsync(_tenantA, administrador);
        (var administradorBId, _administradorB) = await CuentaAsync(_tenantB, administrador);
        (_, _consultaA) = await CuentaAsync(_tenantA, consulta);

        // Referencia cruzada: una fila de auditoría de A que nombra a la cuenta de B. La política de lectura
        // de cuentas deja ver a B bajo el contexto de A por esa referencia, así que lo que impide que B salga
        // en Usuario.json es el filtro explícito por Tenant de la exportación, no solo RLS.
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            _propietario.RegistrosAuditoria.Add(new RegistroAuditoria(
                "Empresa", _empresaA, "Modificado", null, null, administradorBId, TipoActorAuditoria.Persona));
            await _propietario.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_zip_trae_los_datos_del_Tenant_pedido_y_nada_del_ajeno_ni_secretos()
    {
        using var destino = new MemoryStream();
        await using (var runtime = CrearRuntime())
        {
            var resultado = await ExportacionCompletaTenant.ExportarAsync(
                runtime, new AlmacenVacio(), _tenantA, _administradorA, destino);

            resultado.Recuentos["Empresa"].Should().Be(1);
            resultado.Recuentos["Usuario"].Should().Be(2);
        }

        destino.Position = 0;
        using var zip = new ZipArchive(destino, ZipArchiveMode.Read);
        var todo = new StringBuilder();
        foreach (var entrada in zip.Entries)
        {
            using var lector = new StreamReader(entrada.Open());
            todo.Append(await lector.ReadToEndAsync());
        }

        var texto = todo.ToString();
        texto.Should().Contain(_empresaA.ToString(), "control positivo: sin él, la ausencia de B no probaría nada");
        texto.Should().Contain(_administradorA);
        texto.Should().NotContain(_empresaB.ToString());
        texto.Should().NotContain(_tenantB.ToString());
        texto.Should().NotContain(_administradorB);
        texto.Should().NotContain("Shelbyville");
        texto.Should().NotContain(ContrasenaPortal);
        texto.Should().NotContain("PasswordHash").And.NotContain("SecurityStamp");

        using var manifiesto = await JsonDocument.ParseAsync(zip.GetEntry(ArchivoExportacion.RutaManifiesto)!.Open());
        manifiesto.RootElement.GetProperty("tenantId").GetGuid().Should().Be(_tenantA);
        manifiesto.RootElement.GetProperty("sha256").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(zip.Entries.Select(e => e.FullName).Where(n => n != ArchivoExportacion.RutaManifiesto));

        using var empresas = await JsonDocument.ParseAsync(zip.GetEntry("datos/Empresa.json")!.Open());
        empresas.RootElement.EnumerateArray().Select(e => e.GetProperty("TenantId").GetGuid()).Should().Equal(_tenantA);

        await using var control = CrearPropietario();
        (await control.RegistrosAuditoria.IgnoreQueryFilters()
                .CountAsync(r => r.TenantId == _tenantA && r.EntidadId == _tenantA && r.Accion == RegistroAuditoria.AccionExportacion))
            .Should().Be(2, "la exportación queda en la auditoría del Tenant propietario al empezar y al terminar");
        manifiesto.RootElement.GetProperty("noIncluido").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task La_papelera_sale_marcada_sin_secretos_y_sin_filas_de_otro_Tenant()
    {
        var enPapeleraA = await SembrarAsync(_tenantA, "Empresa en la papelera de A", eliminada: true);
        var enPapeleraB = await SembrarAsync(_tenantB, "Empresa en la papelera de B", eliminada: true);

        using var destino = new MemoryStream();
        await using (var runtime = CrearRuntime())
        {
            var resultado = await ExportacionCompletaTenant.ExportarAsync(
                runtime, new AlmacenVacio(), _tenantA, _administradorA, destino);

            resultado.Recuentos["Empresa"].Should().Be(2);
            resultado.Recuentos[ExportacionCompletaTenant.RecuentoEnPapelera].Should().Be(1);
        }

        destino.Position = 0;
        using var zip = new ZipArchive(destino, ZipArchiveMode.Read);
        using var empresas = await JsonDocument.ParseAsync(zip.GetEntry("datos/Empresa.json")!.Open());
        empresas.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("Id").GetGuid(), e => e.GetProperty("EstaEliminado").GetBoolean())
            .Should().BeEquivalentTo(new Dictionary<Guid, bool> { [_empresaA] = false, [enPapeleraA] = true });

        var todo = new StringBuilder();
        foreach (var entrada in zip.Entries)
        {
            using var lector = new StreamReader(entrada.Open());
            todo.Append(await lector.ReadToEndAsync());
        }

        todo.ToString().Should().NotContain(enPapeleraB.ToString());
        todo.ToString().Should().NotContain(ContrasenaPortal, "la credencial de una Empresa en la papelera sigue sin salir");

        using var manifiesto = await JsonDocument.ParseAsync(zip.GetEntry(ArchivoExportacion.RutaManifiesto)!.Open());
        manifiesto.RootElement.GetProperty("noIncluido").EnumerateObject().Select(p => p.Name)
            .Should().NotContain("Filas con borrado lógico");
    }

    [Fact]
    public async Task Se_niega_si_la_pide_una_cuenta_que_no_es_Administrador_del_Tenant()
    {
        await using var runtime = CrearRuntime();

        var noAdministrador = () => ExportacionCompletaTenant.ExportarAsync(
            runtime, new AlmacenVacio(), _tenantA, _consultaA, new MemoryStream());
        var administradorDeOtroTenant = () => ExportacionCompletaTenant.ExportarAsync(
            runtime, new AlmacenVacio(), _tenantA, _administradorB, new MemoryStream());

        await noAdministrador.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Administrador activo*");
        await administradorDeOtroTenant.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Administrador activo*");
    }

    [Fact]
    public async Task Se_niega_con_una_identidad_que_se_salta_RLS()
    {
        await using var propietario = CrearPropietario();

        var exportar = () => ExportacionCompletaTenant.ExportarAsync(
            propietario, new AlmacenVacio(), _tenantA, _administradorA, new MemoryStream());

        await exportar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exenta de RLS*");
    }

    private async Task<Guid> SembrarAsync(Guid tenantId, string razonSocial, bool eliminada = false)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = new Empresa(razonSocial, "B12345674");
        _propietario.Empresas.Add(empresa);
        _propietario.CredencialesAccesoEmpresa.Add(new CredencialAccesoEmpresa(
            empresa.Id, "https://portal.example", "Empresa en el portal", "usuario-portal", ContrasenaPortal));
        if (eliminada)
            empresa.MarcarComoEliminado(Guid.NewGuid());
        await _propietario.SaveChangesAsync();
        return empresa.Id;
    }

    private async Task<(Guid Id, string Correo)> CuentaAsync(Guid tenantId, Guid rolId)
    {
        var id = Guid.NewGuid();
        var correo = $"{id:N}@exportacion.local";
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        _propietario.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = "Usuario",
            EmailConfirmed = true,
            PasswordHash = "hash-que-no-sale",
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        });
        _propietario.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
        await _propietario.SaveChangesAsync();
        return (id, correo);
    }

    private CaeManagerDbContext CrearPropietario()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    /// <summary>Como el modo de CLI: sin usuario ni sesión; el Tenant sale solo del ámbito explícito.</summary>
    private CaeManagerDbContext CrearRuntime()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), new CurrentUserServiceFalso(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class AlmacenVacio : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new FileNotFoundException(identificador);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
