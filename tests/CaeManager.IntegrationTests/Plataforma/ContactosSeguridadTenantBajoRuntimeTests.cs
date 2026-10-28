using CaeManager.Application.Common;
using CaeManager.Application.ContactosSeguridad;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using ContactosSeguridadPlataforma = CaeManager.Infrastructure.Plataforma.ContactosSeguridadPlataforma;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Contacto de seguridad del Tenant beneficiario (aviso de violaciones de seguridad, incremento 3a) contra
/// PostgreSQL real y con la identidad de conexión de producción, <c>cae_app_runtime</c>. El propietario solo
/// siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: un Tenant no lee el contacto de otro; solo el Administrador del Tenant propone (política
/// restrictiva de INSERT); el Tenant no puede validarse a sí mismo ni por la tabla (GRANT por columna y
/// política restrictiva de UPDATE) ni por la función; un Actor de Plataforma TALVEG sin la concesión
/// AdminPlataforma global vigente no lee la cola, no valida y no resuelve destinatarios; con ella, valida,
/// sustituye al vigente y deja auditoría, salvo la propuesta que hizo su propia cuenta (cuatro ojos); y dos
/// propuestas del mismo Tenant que se cruzan dejan una sola. Las reglas de Application están en
/// <c>ContactoSeguridadTenantTests</c>; la forma de las políticas, en <c>CoberturaRlsDelModeloTests</c>.
/// </para>
/// </summary>
public class ContactosSeguridadTenantBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _adminPlataforma = Guid.NewGuid();
    private readonly Guid _adminPlataformaAdministradorDelTenant = Guid.NewGuid();
    private readonly Guid _actorPlataformaSinConcesion = Guid.NewGuid();
    private readonly Guid _actorPlataformaCaducado = Guid.NewGuid();
    private readonly Guid _administradorDeA = Guid.NewGuid();
    private readonly Guid _administradorDeB = Guid.NewGuid();
    private readonly Guid _tecnicoDeA = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant beneficiario A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant beneficiario B S.L."));
        var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
        SembrarCuenta(contexto, "admin-plataforma", _tenantPlataforma, _adminPlataforma, null);
        SembrarCuenta(contexto, "admin-plataforma-y-del-tenant", _tenantPlataforma, _adminPlataformaAdministradorDelTenant,
            roles[Roles.Administrador]);
        SembrarCuenta(contexto, "sin-concesion", _tenantPlataforma, _actorPlataformaSinConcesion, null);
        SembrarCuenta(contexto, "caducado", _tenantPlataforma, _actorPlataformaCaducado, null);
        SembrarCuenta(contexto, "admin-a", _tenantA, _administradorDeA, roles[Roles.Administrador]);
        SembrarCuenta(contexto, "admin-b", _tenantB, _administradorDeB, roles[Roles.Administrador]);
        SembrarCuenta(contexto, "tecnico-a", _tenantA, _tecnicoDeA, roles[Roles.Consulta]);
        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            _adminPlataforma, vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: null));
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            _adminPlataformaAdministradorDelTenant, vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: null));
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            _actorPlataformaCaducado, vigenciaDesde: ahora.AddDays(-10), vigenciaHasta: ahora.AddDays(-1)));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── El Tenant beneficiario ───────────────────────────────────────────────

    [Fact]
    public async Task El_Administrador_propone_y_otro_Tenant_no_lo_ve()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");

        (await LeerComoAsync(_tenantA, _administradorDeA)).Should().ContainSingle().Which.Should().Be(contacto);
        (await LeerComoAsync(_tenantB, _administradorDeB)).Should().BeEmpty(
            "la RLS de aislamiento no deja leer el contacto de seguridad de otro Tenant");
    }

    [Fact]
    public async Task Quien_no_es_Administrador_del_Tenant_no_puede_proponer_ni_por_la_tabla()
    {
        await using var contexto = CrearContexto(_tenantA, actor: _tecnicoDeA, tenantOrigen: _tenantA);
        contexto.ContactosSeguridadTenant.Add(
            ContactoSeguridadTenant.Proponer("Intruso", "intruso@ejemplo.test", null, _tecnicoDeA, DateTime.UtcNow));

        var guardar = () => contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ContarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task El_Tenant_no_puede_validarse_su_propia_propuesta_por_la_tabla()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        var validarPorEstado = () => contexto.Database.ExecuteSqlAsync(
            $@"UPDATE ""ContactosSeguridadTenant"" SET ""Estado"" = 'Validado' WHERE ""Id"" = {contacto}");
        var validarConResolucion = () => contexto.Database.ExecuteSqlAsync(
            $@"UPDATE ""ContactosSeguridadTenant"" SET ""ResueltoEnUtc"" = now() WHERE ""Id"" = {contacto}");

        (await validarPorEstado.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "la política restrictiva de UPDATE solo admite pasar una propuesta a Sustituida");
        (await validarConResolucion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "el GRANT de UPDATE es solo de Estado y SustitucionEnUtc");
        (await EstadoAsync(contacto)).Should().Be("Propuesto");
    }

    [Fact]
    public async Task Proponer_dos_veces_sin_resolver_sustituye_la_primera_propuesta_en_el_mismo_guardado()
    {
        // El índice único parcial admite una sola Propuesta por Tenant: si EF insertara la nueva antes de
        // pasar la anterior a Sustituida, el guardado fallaría con 23505.
        var primera = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        var segunda = await ProponerAsync(_tenantA, _administradorDeA, "Jordi");

        (await EstadoAsync(primera)).Should().Be("Sustituido");
        (await EstadoAsync(segunda)).Should().Be("Propuesto");
    }

    [Fact]
    public async Task El_Administrador_de_otro_Tenant_no_propone_aunque_el_contexto_apunte_al_Tenant()
    {
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeB, tenantOrigen: _tenantB);
        contexto.ContactosSeguridadTenant.Add(
            ContactoSeguridadTenant.Proponer("Intruso", "intruso@ejemplo.test", null, _administradorDeB, DateTime.UtcNow));

        var guardar = () => contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "ser Administrador de un Tenant no lo hace Administrador del Tenant del contexto");
        (await ContarAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("Validado", false)]
    [InlineData("Propuesto", true)]
    public async Task El_alta_directa_por_la_tabla_solo_nace_Propuesta_y_a_nombre_de_quien_firma(string estado, bool aNombreDeOtro)
    {
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);
        var proponente = aNombreDeOtro ? _tecnicoDeA : _administradorDeA;
        var resolutor = estado == "Validado" ? _administradorDeA : (Guid?)null;
        var resueltoEn = estado == "Validado" ? DateTime.UtcNow : (DateTime?)null;

        var insertar = () => contexto.Database.ExecuteSqlAsync($@"
INSERT INTO ""ContactosSeguridadTenant""
  (""Id"", ""Nombre"", ""Correo"", ""Estado"", ""PropuestoPorUsuarioId"", ""PropuestoEnUtc"", ""ResueltoPorUsuarioId"", ""ResueltoEnUtc"", ""TenantId"")
VALUES ({Guid.NewGuid()}, 'Intruso', 'intruso@ejemplo.test', {estado}, {proponente}, now(), {resolutor}, {resueltoEn}, {_tenantA})");

        (await insertar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await ContarAsync()).Should().Be(0);
    }

    [Fact]
    public async Task El_Tenant_no_sustituye_por_la_tabla_un_contacto_ya_validado()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using (var plataforma = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
            (await LlamarResolverAsync(plataforma, contacto, validar: true, motivo: null)).Should().Be("validado");
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        var filas = await contexto.Database.ExecuteSqlAsync($@"
UPDATE ""ContactosSeguridadTenant"" SET ""Estado"" = 'Sustituido', ""SustitucionEnUtc"" = now() WHERE ""Id"" = {contacto}");

        filas.Should().Be(0, "el USING de la política de sustitución solo deja ver Propuestas para actualizar");
        (await EstadoAsync(contacto)).Should().Be("Validado");
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_no_valida_por_la_funcion()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA);

        (await LlamarResolverAsync(contexto, contacto, validar: true, motivo: null)).Should().Be("no_autorizado");
        (await EstadoAsync(contacto)).Should().Be("Propuesto");
    }

    // ── El Actor de Plataforma TALVEG ─────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_concesion_global_vigente_no_se_lee_la_cola_ni_se_valida(bool caducada)
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        var actor = caducada ? _actorPlataformaCaducado : _actorPlataformaSinConcesion;
        await using var contexto = CrearContexto(_tenantPlataforma, actor: actor);

        (await new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos()).ObtenerPropuestosAsync(CancellationToken.None))
            .Should().BeEmpty("sin la concesión la política de lectura de plataforma no abre nada");
        (await LlamarResolverAsync(contexto, contacto, validar: true, motivo: null)).Should().Be("no_autorizado");
        (await EstadoAsync(contacto)).Should().Be("Propuesto");

        var destinatarios = () => contexto.Database
            .SqlQuery<Guid>($@"SELECT ""TenantId"" AS ""Value"" FROM app_destinatarios_aviso_seguridad({new[] { _tenantA }})")
            .ToListAsync();
        (await destinatarios.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Con_concesion_global_valida_sustituye_al_vigente_y_deja_auditoria_sin_datos_personales()
    {
        var primero = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
        {
            var plataforma = new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos());
            (await plataforma.ObtenerPropuestosAsync(CancellationToken.None)).Should().ContainSingle()
                .Which.NombreOrganizacion.Should().Be("Tenant beneficiario A S.L.");
            (await plataforma.ResolverAsync(primero, motivoRechazo: null, CancellationToken.None)).EsExitoso.Should().BeTrue();
        }

        var segundo = await ProponerAsync(_tenantA, _administradorDeA, "Jordi");
        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
        {
            (await new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos())
                .ResolverAsync(segundo, motivoRechazo: null, CancellationToken.None)).EsExitoso.Should().BeTrue();
        }

        (await EstadoAsync(primero)).Should().Be("Sustituido");
        (await EstadoAsync(segundo)).Should().Be("Validado");
        var auditoria = await EscalarAsync<string>(@"
SELECT string_agg(""DatosDespues"", '|') FROM ""RegistrosAuditoria""
WHERE ""EntidadTipo"" = 'ContactoSeguridadTenant' AND ""UsuarioId"" = @u;", _adminPlataforma);
        auditoria.Should().Contain("Validado").And.Contain("Sustituido");
        auditoria.Should().NotContain("Marta").And.NotContain("Jordi").And.NotContain("@ejemplo.test",
            "la auditoría de la resolución no copia los datos personales del contacto");
    }

    [Fact]
    public async Task Rechazar_exige_motivo_y_una_propuesta_resuelta_no_se_vuelve_a_resolver()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);

        (await LlamarResolverAsync(contexto, contacto, validar: false, motivo: "   ")).Should().Be("motivo_no_valido");
        (await LlamarResolverAsync(contexto, contacto, validar: false, motivo: "No pertenece a la organización"))
            .Should().Be("rechazado");
        (await LlamarResolverAsync(contexto, contacto, validar: true, motivo: null)).Should().Be("no_propuesto");
        (await EstadoAsync(contacto)).Should().Be("Rechazado");
    }

    [Fact]
    public async Task Los_destinatarios_son_el_contacto_validado_o_los_Administradores()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);
        var plataforma = new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos());
        (await plataforma.ResolverAsync(contacto, motivoRechazo: null, CancellationToken.None)).EsExitoso.Should().BeTrue();

        var resultado = await plataforma.ResolverDestinatariosAsync([_tenantA, _tenantB], CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var deA = resultado.Valor.Single(d => d.TenantId == _tenantA);
        deA.SinContactoValidado.Should().BeFalse();
        deA.Destinatarios.Should().ContainSingle().Which.Nombre.Should().Be("Marta");
        var deB = resultado.Valor.Single(d => d.TenantId == _tenantB);
        deB.SinContactoValidado.Should().BeTrue();
        deB.Destinatarios.Should().ContainSingle().Which.Correo.Should().Be("admin-b@contacto-seguridad.test",
            "sin contacto validado el aviso va a los Administradores del Tenant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_propuesta_pendiente_o_rechazada_no_recibe_avisos(bool rechazada)
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);
        if (rechazada)
            (await LlamarResolverAsync(contexto, contacto, validar: false, motivo: "No pertenece a la organización"))
                .Should().Be("rechazado");

        var resultado = await new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos())
            .ResolverDestinatariosAsync([_tenantA], CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var deA = resultado.Valor.Single(d => d.TenantId == _tenantA);
        deA.SinContactoValidado.Should().BeTrue();
        deA.Destinatarios.Should().ContainSingle().Which.Correo.Should().Be("admin-a@contacto-seguridad.test",
            "solo un contacto validado recibe avisos; el técnico de A no es Administrador");
    }

    [Fact]
    public async Task Con_concesion_global_pero_dentro_de_otro_Tenant_no_se_valida()
    {
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantA, actor: _adminPlataforma, tenantOrigen: _tenantPlataforma);

        // Según firme el interceptor ese contexto, la función se para en la firma o en la comprobación de que
        // el actor trabaja desde su propio Tenant de plataforma; en los dos casos, sin escribir.
        (await LlamarResolverAsync(contexto, contacto, validar: true, motivo: null))
            .Should().BeOneOf("no_autorizado", "contexto_no_valido");
        (await EstadoAsync(contacto)).Should().Be("Propuesto");
    }

    [Fact]
    public async Task La_cuenta_que_propuso_no_resuelve_por_la_funcion_y_otro_Actor_de_Plataforma_si()
    {
        // Un Actor de Plataforma TALVEG que además es Administrador del Tenant de plataforma propone el contacto
        // de ese Tenant: tiene la concesión global, trabaja desde su Tenant de plataforma, y aun así no se lo
        // resuelve él mismo.
        var contacto = await ProponerAsync(_tenantPlataforma, _adminPlataformaAdministradorDelTenant, "Marta");
        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataformaAdministradorDelTenant))
        {
            (await LlamarResolverAsync(contexto, contacto, validar: true, motivo: null)).Should().Be("misma_cuenta");
            (await LlamarResolverAsync(contexto, contacto, validar: false, motivo: "No es quien debe")).Should().Be("misma_cuenta");

            var plataforma = new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos());
            (await plataforma.ObtenerCuentaProponenteAsync(contacto, CancellationToken.None))
                .Should().Be(_adminPlataformaAdministradorDelTenant);
            (await plataforma.ResolverAsync(contacto, motivoRechazo: null, CancellationToken.None))
                .Error.Should().Be(ErroresContactoSeguridad.MismaCuentaProponente);
        }
        (await EstadoAsync(contacto)).Should().Be("Propuesto", "la función se para antes de escribir");

        await using (var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma))
            (await LlamarResolverAsync(contexto, contacto, validar: true, motivo: null)).Should().Be("validado");
        (await EstadoAsync(contacto)).Should().Be("Validado");
    }

    [Fact]
    public async Task El_Actor_de_Plataforma_lee_la_cuenta_proponente_de_otro_Tenant()
    {
        // La comprobación temprana de cuatro ojos lee la fila de otro Tenant desde el Tenant de plataforma: sin la
        // política de lectura del Actor de Plataforma devolvería null y nunca daría MismaCuentaProponente.
        var contacto = await ProponerAsync(_tenantA, _administradorDeA, "Marta");
        await using var contexto = CrearContexto(_tenantPlataforma, actor: _adminPlataforma);

        (await new ContactosSeguridadPlataforma(contexto, new PuertaAccesoDatos())
                .ObtenerCuentaProponenteAsync(contacto, CancellationToken.None))
            .Should().Be(_administradorDeA);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dos_propuestas_que_se_cruzan_dejan_una_y_la_otra_no_guarda_ni_deja_nada_pendiente(bool conPendientePrevia)
    {
        // Las dos leen el mismo estado antes de que la otra guarde. Sin pendiente previa, el índice único parcial
        // deja pasar la primera (23505 para la segunda); con ella, la segunda encuentra ya Sustituida la pendiente
        // que también quería sustituir. En los dos casos la segunda recibe false, no una excepción.
        var previa = conPendientePrevia ? await ProponerAsync(_tenantA, _administradorDeA, "Antigua") : (Guid?)null;
        await using var primero = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA, conAuditoria: true);
        await using var segundo = CrearContexto(_tenantA, actor: _administradorDeA, tenantOrigen: _tenantA, conAuditoria: true);
        var repositorioPrimero = new ContactoSeguridadTenantRepository(primero);
        var repositorioSegundo = new ContactoSeguridadTenantRepository(segundo);
        var ahora = DateTime.UtcNow;
        foreach (var (repositorio, nombre) in new[] { (repositorioPrimero, "Marta"), (repositorioSegundo, "Jordi") })
        {
            foreach (var pendiente in (await repositorio.ObtenerPendienteYVigenteAsync(CancellationToken.None))
                         .Where(c => c.Estado == EstadoContactoSeguridad.Propuesto))
                pendiente.SustituirPropuestaPendiente(ahora);
            repositorio.Agregar(ContactoSeguridadTenant.Proponer(
                nombre, $"{nombre.ToLowerInvariant()}@ejemplo.test", null, _administradorDeA, ahora));
        }

        (await repositorioPrimero.GuardarPropuestaAsync(CancellationToken.None)).Should().BeTrue();
        (await repositorioSegundo.GuardarPropuestaAsync(CancellationToken.None)).Should().BeFalse();

        segundo.ChangeTracker.Entries().Should().NotContain(
            e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted,
            "ni la propuesta perdedora ni su auditoría pueden colarse en el siguiente guardado del circuito");
        (await EscalarAsync<long>(@"
SELECT count(*) FROM ""ContactosSeguridadTenant"" WHERE ""TenantId"" = @u AND ""Estado"" = 'Propuesto';", _tenantA))
            .Should().Be(1);
        if (previa is { } id)
            (await EstadoAsync(id)).Should().Be("Sustituido");
    }

    // ── Preparación y lectura ─────────────────────────────────────────────────

    private async Task<Guid> ProponerAsync(Guid tenant, Guid administrador, string nombre)
    {
        await using var contexto = CrearContexto(tenant, actor: administrador, tenantOrigen: tenant);
        var repositorio = new ContactoSeguridadTenantRepository(contexto);
        var ahora = DateTime.UtcNow;
        foreach (var pendiente in (await repositorio.ObtenerPendienteYVigenteAsync(CancellationToken.None))
                     .Where(c => c.Estado == EstadoContactoSeguridad.Propuesto))
            pendiente.SustituirPropuestaPendiente(ahora);
        var contacto = ContactoSeguridadTenant.Proponer(nombre, $"{nombre.ToLowerInvariant()}@ejemplo.test", null, administrador, ahora);
        repositorio.Agregar(contacto);
        await contexto.SaveChangesAsync();
        return contacto.Id;
    }

    private async Task<List<Guid>> LeerComoAsync(Guid tenant, Guid actor)
    {
        await using var contexto = CrearContexto(tenant, actor: actor, tenantOrigen: tenant);
        return await contexto.ContactosSeguridadTenant.AsNoTracking().Select(c => c.Id).ToListAsync();
    }

    // La misma llamada que ContactosSeguridadPlataforma.ResolverAsync, para leer el código exacto.
    private static Task<string> LlamarResolverAsync(CaeManagerDbContext contexto, Guid contacto, bool validar, string? motivo) =>
        contexto.Database
            .SqlQuery<string>($"SELECT app_resolver_contacto_seguridad({contacto}, {validar}, {motivo}) AS \"Value\"")
            .SingleAsync();

    private Task<string> EstadoAsync(Guid contacto) =>
        EscalarAsync<string>(@"SELECT ""Estado"" FROM ""ContactosSeguridadTenant"" WHERE ""Id"" = @u;", contacto);

    private async Task<long> ContarAsync() =>
        await EscalarAsync<long>(@"SELECT count(*) FROM ""ContactosSeguridadTenant"" WHERE @u IS NOT NULL;", Guid.Empty);

    private async Task<T> EscalarAsync<T>(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private static void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid tenant, Guid id, Guid? rolId)
    {
        var correo = $"{nombre}@contacto-seguridad.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = tenant,
            TwoFactorEnabled = true,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
        if (rolId is { } rol)
            contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rol });
    }

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción. Solo la siembra pasa la
    /// cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, string? cadena = null, Guid? actor = null, Guid? tenantOrigen = null, bool conAuditoria = false)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var actorEfectivo = actor ?? _adminPlataforma;
        var usuario = new CurrentUserServiceFalso(
            actorEfectivo, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, tieneDobleFactorActivo: true);
        var interceptores = new List<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>
        {
            new TenantSelladoInterceptor(tenantActual),
            new TenantRlsConnectionInterceptor(
                tenantActual, new SinSeleccion(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls),
        };
        // Solo donde el test mira lo que el interceptor de auditoría deja en el rastreo tras un fallo.
        if (conAuditoria)
            interceptores.Insert(0, new AuditoriaInterceptor(new ActorFijo(actorEfectivo)));
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(interceptores)
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Tenant(nombre);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class ActorFijo(Guid actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(actor));

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(actor);
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
