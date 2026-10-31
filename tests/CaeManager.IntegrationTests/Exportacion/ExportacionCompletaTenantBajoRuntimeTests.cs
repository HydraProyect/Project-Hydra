using System.Text;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Exportacion;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Exportacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Exportacion;

/// <summary>
/// Exportación completa de un Tenant propietario leída <b>como <c>cae_app_runtime</c></b>, sin usuario
/// (igual que el modo de CLI), con los interceptores de sellado y de sesión RLS de producción. Dos
/// Tenants propietarios con su Empresa, su Administrador y una credencial de portal cada uno: el zip
/// cifrado del primero trae lo suyo y nada del segundo, ni la contraseña de su propio portal. Y se niega
/// si la pide quien no es un Actor de Plataforma TALVEG con concesión AdminPlataforma vigente sobre ese Tenant, o si la
/// identidad de la conexión se salta RLS. La siembra va como propietario de la base, porque no es lo que
/// se mide.
/// </summary>
public class ExportacionCompletaTenantBajoRuntimeTests : IAsyncLifetime
{
    private const string ContrasenaPortal = "contrasena-del-portal-que-no-sale";
    private const string ContrasenaZip = "contrasena-del-zip-que-no-se-registra";

    /// <summary>El índice único de CIF por Tenant incluye las Empresas en la papelera.</summary>
    private const string CifEnPapelera = "A58818501";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _empresaA;
    private Guid _empresaB;
    private string _administradorA = null!;
    private string _administradorB = null!;
    private string _consultaA = null!;
    private Guid _tenantPlataforma;
    private Guid _actorId;
    private string _actor = null!;
    private string _actorSinConcesion = null!;
    private string _actorDeOtroTenant = null!;
    private string _actorCaducado = null!;
    private string _actorDesactivado = null!;
    private string _actorRevocado = null!;
    private string _actorBloqueoTemporal = null!;
    private string _actorGlobal = null!;
    private string _consultaConConcesion = null!;

    public async Task InitializeAsync()
    {
        _propietario = CrearPropietario();
        await _propietario.Database.MigrateAsync();

        var tenantA = new Tenant("Tenant propietario exportado");
        var tenantB = new Tenant("Tenant propietario ajeno");
        var tenantPlataforma = new Tenant("TALVEG", esPlataforma: true);
        _propietario.Tenants.AddRange(tenantA, tenantB, tenantPlataforma);
        await _propietario.SaveChangesAsync();
        (_tenantA, _tenantB, _tenantPlataforma) = (tenantA.Id, tenantB.Id, tenantPlataforma.Id);

        _propietario.ReclamacionesBuzonIntegracion.AddRange(
            new ReclamacionBuzonIntegracion("exportado@example.com", _tenantA, Guid.NewGuid(), DateTime.UtcNow),
            new ReclamacionBuzonIntegracion("ajeno@example.com", _tenantB, Guid.NewGuid(), DateTime.UtcNow));
        await _propietario.SaveChangesAsync();

        var administrador = await _propietario.Roles.Where(r => r.Name == Roles.Administrador).Select(r => r.Id).SingleAsync();
        var consulta = await _propietario.Roles.Where(r => r.Name == Roles.Consulta).Select(r => r.Id).SingleAsync();

        _empresaA = await SembrarAsync(_tenantA, "Montajes Springfield");
        _empresaB = await SembrarAsync(_tenantB, "Montajes Shelbyville");
        (_, _administradorA) = await CuentaAsync(_tenantA, administrador);
        (var administradorBId, _administradorB) = await CuentaAsync(_tenantB, administrador);
        (_, _consultaA) = await CuentaAsync(_tenantA, consulta);

        var ahora = DateTime.UtcNow;
        (_actorId, _actor) = await CuentaAsync(_tenantPlataforma, administrador);
        (_, _actorSinConcesion) = await CuentaAsync(_tenantPlataforma, administrador);
        (var deOtroTenantId, _actorDeOtroTenant) = await CuentaAsync(_tenantPlataforma, administrador);
        (var caducadoId, _actorCaducado) = await CuentaAsync(_tenantPlataforma, administrador);
        (var desactivadoId, _actorDesactivado) = await CuentaAsync(_tenantPlataforma, administrador);
        (var revocadoId, _actorRevocado) = await CuentaAsync(_tenantPlataforma, administrador);
        (var temporalId, _actorBloqueoTemporal) = await CuentaAsync(_tenantPlataforma, administrador);
        (var globalId, _actorGlobal) = await CuentaAsync(_tenantPlataforma, administrador);
        (var consultaConConcesionId, _consultaConConcesion) = await CuentaAsync(_tenantA, consulta);
        var revocada = ConcesionPrivilegio.SobreTenants(revocadoId, CapacidadPrivilegio.AdminPlataforma, [_tenantA], ahora.AddMinutes(-10), null);
        revocada.Revocar(ahora);
        _propietario.ConcesionesPrivilegio.AddRange(
            revocada,
            ConcesionPrivilegio.SobreTenants(temporalId, CapacidadPrivilegio.AdminPlataforma, [_tenantA], ahora.AddMinutes(-10), null),
            ConcesionPrivilegio.Global(globalId, ahora.AddMinutes(-10), null),
            // Dato anómalo: una cuenta de un Tenant propietario con la concesión. Solo EsPlataforma la para.
            ConcesionPrivilegio.SobreTenants(consultaConConcesionId, CapacidadPrivilegio.AdminPlataforma, [_tenantA], ahora.AddMinutes(-10), null),
            ConcesionPrivilegio.SobreTenants(_actorId, CapacidadPrivilegio.AdminPlataforma, [_tenantA], ahora.AddMinutes(-10), null),
            ConcesionPrivilegio.SobreTenants(deOtroTenantId, CapacidadPrivilegio.AdminPlataforma, [_tenantB], ahora.AddMinutes(-10), null),
            ConcesionPrivilegio.SobreTenants(caducadoId, CapacidadPrivilegio.AdminPlataforma, [_tenantA], ahora.AddHours(-2), ahora.AddHours(-1)),
            ConcesionPrivilegio.SobreTenants(desactivadoId, CapacidadPrivilegio.AdminPlataforma, [_tenantA], ahora.AddMinutes(-10), null));
        await _propietario.SaveChangesAsync();
        await _propietario.Users.IgnoreQueryFilters().Where(u => u.Id == desactivadoId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LockoutEnd, ApplicationUser.FinDeBloqueoDeCuentaDesactivada));

        await _propietario.Users.IgnoreQueryFilters().Where(u => u.Id == temporalId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LockoutEnd, DateTimeOffset.UtcNow.AddDays(300)));

        // El correo del Actor también existe como cuenta de un Tenant propietario: solo cuenta la de la plataforma.
        await CuentaAsync(_tenantA, consulta, _actor);

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
                runtime, new AlmacenVacio(), _tenantA, _actor, ContrasenaZip, destino);

            resultado.Recuentos["Empresa"].Should().Be(1);
            resultado.Recuentos["Usuario"].Should().Be(4);
            resultado.Recuentos["ReclamacionBuzonIntegracion"].Should().Be(1);
        }

        destino.Position = 0;
        using var zip = new ZipFile(destino, leaveOpen: true) { Password = ContrasenaZip };
        var entradas = zip.Cast<ZipEntry>().ToList();
        entradas.Should().OnlyContain(e => e.IsCrypted && e.AESKeySize == 256);
        var todo = new StringBuilder();
        foreach (var entrada in entradas)
        {
            using var lector = new StreamReader(zip.GetInputStream(entrada));
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
        texto.Should().NotContain(ContrasenaZip);
        texto.Should().NotContain("ajeno@example.com");

        using var manifiesto = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry(ArchivoExportacion.RutaManifiesto)));
        manifiesto.RootElement.GetProperty("tenantId").GetGuid().Should().Be(_tenantA);
        manifiesto.RootElement.GetProperty("sha256").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(entradas.Select(e => e.Name).Where(n => n != ArchivoExportacion.RutaManifiesto));

        using var empresas = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry("datos/Empresa.json")));
        empresas.RootElement.EnumerateArray().Select(e => e.GetProperty("TenantId").GetGuid()).Should().Equal(_tenantA);

        using var reclamaciones = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry("datos/ReclamacionBuzonIntegracion.json")));
        reclamaciones.RootElement.GetArrayLength().Should().Be(1);
        reclamaciones.RootElement[0].GetProperty("TenantPropietarioId").GetGuid().Should().Be(_tenantA);
        reclamaciones.RootElement[0].GetProperty("BuzonEmail").GetString().Should().Be("exportado@example.com");

        await using var control = CrearPropietario();
        var auditoria = await control.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.TenantId == _tenantA && r.EntidadId == _tenantA && r.Accion == RegistroAuditoria.AccionExportacion)
            .ToListAsync();
        auditoria.Should().HaveCount(2, "la exportación queda en la auditoría del Tenant propietario al empezar y al terminar");
        auditoria.Should().OnlyContain(r => r.ActorRealUsuarioId == null && r.UsuarioId == null &&
                                            r.TipoActor == TipoActorAuditoria.Desconocido &&
                                            r.ViaAcceso == TipoViaAccesoAuditoria.Desconocida);
        auditoria.Should().OnlyContain(r => r.DatosDespues!.Contains($"\"actorRealDeclaradoUsuarioId\":\"{_actorId}\"") &&
                                            r.DatosDespues.Contains("\"usuarioSimuladoId\":null") &&
                                            !r.DatosDespues.Contains(ContrasenaZip));
        manifiesto.RootElement.GetProperty("noIncluido").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task La_papelera_sale_marcada_sin_secretos_y_sin_filas_de_otro_Tenant()
    {
        var enPapeleraA = await SembrarAsync(_tenantA, "Empresa en la papelera de A", CifEnPapelera, eliminada: true);
        var enPapeleraB = await SembrarAsync(_tenantB, "Empresa en la papelera de B", CifEnPapelera, eliminada: true);

        using var destino = new MemoryStream();
        await using (var runtime = CrearRuntime())
        {
            var resultado = await ExportacionCompletaTenant.ExportarAsync(
                runtime, new AlmacenVacio(), _tenantA, _actor, ContrasenaZip, destino);

            resultado.Recuentos["Empresa"].Should().Be(2);
            resultado.Recuentos[ExportacionCompletaTenant.RecuentoEnPapelera].Should().Be(1);
        }

        destino.Position = 0;
        using var zip = new ZipFile(destino, leaveOpen: true) { Password = ContrasenaZip };
        using var empresas = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry("datos/Empresa.json")));
        empresas.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("Id").GetGuid(), e => e.GetProperty("EstaEliminado").GetBoolean())
            .Should().BeEquivalentTo(new Dictionary<Guid, bool> { [_empresaA] = false, [enPapeleraA] = true });

        var todo = new StringBuilder();
        foreach (var entrada in zip.Cast<ZipEntry>())
        {
            using var lector = new StreamReader(zip.GetInputStream(entrada));
            todo.Append(await lector.ReadToEndAsync());
        }

        todo.ToString().Should().NotContain(enPapeleraB.ToString());
        todo.ToString().Should().NotContain(ContrasenaPortal, "la credencial de una Empresa en la papelera sigue sin salir");

        using var manifiesto = await JsonDocument.ParseAsync(zip.GetInputStream(zip.GetEntry(ArchivoExportacion.RutaManifiesto)));
        manifiesto.RootElement.GetProperty("noIncluido").EnumerateObject().Select(p => p.Name)
            .Should().NotContain("Filas con borrado lógico");
    }

    [Fact]
    public async Task Se_niega_si_no_la_pide_un_Actor_de_Plataforma_con_concesion_vigente_sobre_el_Tenant()
    {
        string[] rechazados =
        [
            _administradorA, _consultaA, _administradorB, _actorSinConcesion, _actorDeOtroTenant,
            _actorCaducado, _actorDesactivado, _actorRevocado, _consultaConConcesion, "nadie@exportacion.local",
        ];

        foreach (var correo in rechazados)
        {
            await using var runtime = CrearRuntime();
            var exportar = () => ExportacionCompletaTenant.ExportarAsync(
                runtime, new AlmacenVacio(), _tenantA, correo, ContrasenaZip, new MemoryStream());

            await exportar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Actor de Plataforma TALVEG activo*");
        }

        await using var control = CrearPropietario();
        (await control.RegistrosAuditoria.IgnoreQueryFilters()
                .CountAsync(r => r.TenantId == _tenantA && r.Accion == RegistroAuditoria.AccionExportacion))
            .Should().Be(0, "un rechazo no llega a abrir la exportación");
    }

    [Fact]
    public async Task La_acepta_una_concesion_global_y_un_bloqueo_temporal_no_desactiva()
    {
        foreach (var correo in new[] { _actorGlobal, _actorBloqueoTemporal })
        {
            await using var runtime = CrearRuntime();
            var resultado = await ExportacionCompletaTenant.ExportarAsync(
                runtime, new AlmacenVacio(), _tenantA, correo, ContrasenaZip, new MemoryStream());

            resultado.TenantId.Should().Be(_tenantA);
        }
    }

    [Fact]
    public async Task El_Tenant_propietario_no_gana_lectura_de_la_cuenta_del_Actor()
    {
        await using (var runtime = CrearRuntime())
            await ExportacionCompletaTenant.ExportarAsync(runtime, new AlmacenVacio(), _tenantA, _actor, ContrasenaZip, new MemoryStream());

        await using var lector = CrearRuntime();
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);

        (await lector.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == _tenantA))
            .Should().Be(4, "control positivo: sin él, no ver al Actor no probaría nada");
        (await lector.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == _tenantPlataforma && u.Id == _actorId)).Should().Be(0);
    }

    [Fact]
    public async Task La_funcion_de_la_base_solo_la_ejecuta_el_runtime()
    {
        await using var control = CrearPropietario();
        var conExecute = await control.Database.SqlQuery<string>($$"""
            SELECT r.rolname AS "Value" FROM pg_roles r
            WHERE r.rolname IN ('cae_app_runtime', 'cae_app_soporte', 'cae_app_aprovisionamiento')
              AND has_function_privilege(r.rolname, 'public.app_actor_plataforma_que_exporta(text, uuid)', 'EXECUTE')
            """).ToListAsync();

        conExecute.Should().Equal("cae_app_runtime");
    }

    [Fact]
    public async Task Se_niega_con_una_identidad_que_se_salta_RLS()
    {
        await using var propietario = CrearPropietario();

        var exportar = () => ExportacionCompletaTenant.ExportarAsync(
            propietario, new AlmacenVacio(), _tenantA, _actor, ContrasenaZip, new MemoryStream());

        await exportar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exenta de RLS*");
    }

    private async Task<Guid> SembrarAsync(Guid tenantId, string razonSocial, string cif = "B12345674", bool eliminada = false)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = new Empresa(razonSocial, cif);
        _propietario.Empresas.Add(empresa);
        _propietario.CredencialesAccesoEmpresa.Add(new CredencialAccesoEmpresa(
            empresa.Id, "https://portal.example", "Empresa en el portal", "usuario-portal", ContrasenaPortal));
        if (eliminada)
            empresa.MarcarComoEliminado(Guid.NewGuid());
        await _propietario.SaveChangesAsync();
        return empresa.Id;
    }

    private async Task<(Guid Id, string Correo)> CuentaAsync(Guid tenantId, Guid rolId, string? correoFijo = null)
    {
        var id = Guid.NewGuid();
        var correo = correoFijo ?? $"{id:N}@exportacion.local";
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        _propietario.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = id.ToString("N"),
            NormalizedUserName = id.ToString("N").ToUpperInvariant(),
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
