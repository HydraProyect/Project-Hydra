using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Paridad de <see cref="AlcanceDatosService.ObtenerEmpresaIdsVisiblesAsync"/>
/// y <see cref="AlcanceDatosService.ObtenerSubcontrataIdsVisiblesAsync"/> —
/// primer test obligatorio de F4 (ver
/// f4-diseno-fisico-relacionempresarial-2026-08-26.md § 6/8ter en el
/// repositorio de negocio).
///
/// <para>
/// <b>Su nombre cambió en el cierre de F4, y el motivo importa</b>: mientras
/// existieron las tres tablas puente, cada fixture se sembraba por duplicado
/// (legacy + arista) y la suite servía de prueba de PARIDAD — se ejecutó
/// contra la implementación anterior a F4 (que leía legacy) y contra la
/// posterior (que lee solo la arista), con las mismas aserciones y el mismo
/// resultado. Esa paridad ya está demostrada y no puede volver a medirse:
/// las tablas legacy se retiraron. Lo que esta suite sigue probando —y por
/// eso no se borra— es que el alcance de datos se resuelve correctamente
/// sobre <c>RelacionEmpresarial</c>, incluida la frontera entre tenants.
/// Llamarla "Paridad" después del DROP sería un nombre que miente sobre lo
/// que el instrumento puede observar.
/// </para>
/// </summary>
public class AlcanceDatosServiceSobreRelacionEmpresarialTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Escenario 1 (Empresa propia → Cliente) + escenario 3 (múltiples
    /// relaciones): un Cliente visible con DOS Empresas propias vinculadas.
    /// </summary>
    [Fact]
    public async Task Empresa_propia_a_Cliente_con_multiples_relaciones()
    {
        Guid cliente, empresaUno, empresaDos;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cli = Empresa.CrearComoCliente("Cliente Paridad Uno S.A.", "B12345674", false, null, null);
            var e1 = new Empresa("Empresa Paridad Uno S.L.", "B10380186");
            var e2 = new Empresa("Empresa Paridad Dos S.L.", "B10380194");
            contexto.Empresas.AddRange(cli, e1, e2);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaUno = e1.Id; empresaDos = e2.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(empresaUno, cliente, ahora, ahora),
                RelacionEmpresarial.Migrar(empresaDos, cliente, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        var visibles = await ResolverEmpresaIdsParaGestorConCarteraAsync(cliente);
        visibles.Should().NotBeNull().And.BeEquivalentTo([empresaUno, empresaDos]);
    }

    /// <summary>Escenario 2: Subcontrata → Cliente, vía la relación directa.</summary>
    [Fact]
    public async Task Subcontrata_a_Cliente_directa()
    {
        Guid cliente, subcontrata;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cli = Empresa.CrearComoCliente("Cliente Paridad Dos S.A.", "B10380202", false, null, null);
            var sub = Empresa.CrearComoSubcontrata("Subcontrata Paridad S.L.", "B10380210", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(cli, sub);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; subcontrata = sub.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Migrar(subcontrata, cliente, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        var visibles = await ResolverSubcontrataIdsParaGestorConCarteraAsync(cliente, empresaIdsVisibles: []);
        visibles.Should().NotBeNull().And.BeEquivalentTo([subcontrata]);
    }

    /// <summary>
    /// Escenario 5: una Empresa propia SIN NivelServicio (no es subcontrata)
    /// no debe colarse en ObtenerSubcontrataIdsVisiblesAsync aunque tenga una
    /// relación con el mismo Cliente.
    /// </summary>
    [Fact]
    public async Task Empresa_sin_NivelServicio_no_aparece_como_subcontrata_visible()
    {
        Guid cliente, empresaPropia;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cli = Empresa.CrearComoCliente("Cliente Paridad Tres S.A.", "B10380228", false, null, null);
            var empresa = new Empresa("Empresa Sin Nivel S.L.", "B10380236"); // NivelServicio null — no es subcontrata
            contexto.Empresas.AddRange(cli, empresa);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaPropia = empresa.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Migrar(empresaPropia, cliente, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        var usuarioId = await OtorgarCarteraAsync(cliente);
        var empresaIdsVisibles = await ResolverEmpresaIdsParaUsuarioAsync(usuarioId);
        var subcontrataIdsVisibles = await ResolverSubcontrataIdsParaUsuarioAsync(usuarioId);

        empresaIdsVisibles.Should().Contain(empresaPropia);
        subcontrataIdsVisibles.Should().NotContain(empresaPropia,
            "no tiene NivelServicio: es una Empresa propia con relación directa, no una subcontrata");
    }

    /// <summary>Escenario 4: los datos de otro tenant no se filtran al alcance de este.</summary>
    [Fact]
    public async Task Datos_de_otro_tenant_no_se_filtran_al_alcance()
    {
        var otroTenant = Guid.NewGuid();
        Guid cliente, empresaDeMiTenant;

        await using (var contexto = CrearContexto(_tenant))
        {
            var cli = Empresa.CrearComoCliente("Cliente Paridad Cuatro S.A.", "B10380244", false, null, null);
            var empresa = new Empresa("Empresa Mi Tenant S.L.", "B10380251");
            contexto.Empresas.AddRange(cli, empresa);
            await contexto.SaveChangesAsync();
            cliente = cli.Id; empresaDeMiTenant = empresa.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Migrar(empresaDeMiTenant, cliente, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using (var contextoOtroTenant = CrearContexto(otroTenant))
        {
            var cliOtro = Empresa.CrearComoCliente("Cliente De Otro Tenant S.A.", "B10380269", false, null, null);
            var empresaOtro = new Empresa("Empresa De Otro Tenant S.L.", "B10380277");
            contextoOtroTenant.Empresas.AddRange(cliOtro, empresaOtro);
            await contextoOtroTenant.SaveChangesAsync();
            var ahora = DateTime.UtcNow;
            contextoOtroTenant.RelacionesEmpresariales.Add(RelacionEmpresarial.Migrar(empresaOtro.Id, cliOtro.Id, ahora, ahora));
            await contextoOtroTenant.SaveChangesAsync();
        }

        var visibles = await ResolverEmpresaIdsParaGestorConCarteraAsync(cliente);
        visibles.Should().NotBeNull().And.BeEquivalentTo([empresaDeMiTenant],
            "el filtro global de tenant debe excluir la Empresa del otro tenant aunque comparta el mismo proceso de prueba");
    }

    /// <summary>Escenario 6, control negativo: sin cartera asignada, vacío — nunca null, nunca "todo".</summary>
    [Fact]
    public async Task Sin_cartera_asignada_EmpresaIds_y_SubcontrataIds_son_vacios_no_null()
    {
        await using var contexto = CrearContexto(_tenant);
        var usuarioSinCartera = Guid.NewGuid();
        var servicio = new AlcanceDatosService(
            contexto,
            new CurrentUserServiceFalso(usuarioSinCartera, "GestorCae", tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

        (await servicio.ObtenerEmpresaIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
        (await servicio.ObtenerSubcontrataIdsVisiblesAsync()).Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// El rol Cliente (usuario de portal) SÍ ve Empresas para leer su
    /// documentación —es lo que un portal CAE existe para enseñar— pero NO
    /// para operar sobre ellas. Sin esta separación, un contacto de una
    /// empresa cliente externa alcanzaba el historial de reclamaciones a las
    /// contratistas y los adjuntos de sus hilos de correo, porque la cartera
    /// de Empresas se DERIVA de la de Clientes (hallazgo de la revisión
    /// adversaria de REC-003).
    ///
    /// Va contra el servicio REAL a propósito: la regla vive en Infrastructure
    /// (necesita el rol), así que un test con AlcanceDatosServiceFalso no
    /// puede observarla — comprobado por mutación, el doble la deja pasar.
    /// </summary>
    [Fact]
    public async Task El_rol_Cliente_ve_Empresas_para_leer_pero_ninguna_para_gestionar()
    {
        Guid clienteId;
        await using (var contexto = CrearContexto(_tenant))
        {
            var cliente = Empresa.CrearComoCliente("Cliente Portal S.L.", "B10380186", false, null, null);
            var proveedora = new Empresa("Contratista del Portal S.L.", "B87654323");
            contexto.Empresas.AddRange(cliente, proveedora);
            await contexto.SaveChangesAsync();

            contexto.Centros.Add(new Centro(cliente.Id, proveedora.Id, "Centro del portal"));
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        var usuarioPortal = Guid.NewGuid();
        await using (var contexto = CrearContexto(_tenant))
        {
            var usuario = await contexto.Users.FindAsync(usuarioPortal);
            if (usuario is null)
            {
                contexto.Users.Add(new ApplicationUser
                {
                    Id = usuarioPortal,
                    UserName = $"portal-{usuarioPortal:N}@ejemplo.test",
                    Email = $"portal-{usuarioPortal:N}@ejemplo.test",
                    ClienteId = clienteId,
                    TenantId = _tenant
                });
                await contexto.SaveChangesAsync();
            }
        }

        await using var lectura = CrearContexto(_tenant);
        var servicio = new AlcanceDatosService(
            lectura, new CurrentUserServiceFalso(usuarioPortal, "Cliente", tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        (await servicio.ObtenerEmpresaIdsVisiblesAsync()).Should().NotBeNull().And.NotBeEmpty(
            "el portal enseña la documentación de las contratistas de su propio Cliente");

        (await servicio.ObtenerEmpresaIdsParaGestionAsync()).Should().NotBeNull().And.BeEmpty(
            "pero operar sobre ellas —reclamar, leer el hilo de correo— es del lado de la gestión CAE, no del portal");
    }

    /// <summary>
    /// Otorga cartera de GestorCae sobre <paramref name="clienteId"/> y
    /// resuelve ObtenerEmpresaIdsVisiblesAsync — mismo mecanismo de F1 que
    /// MemoizacionAlcanceDatosTests. Solo puede llamarse UNA vez por test:
    /// crea la operación raíz del tenant, que es única por (tenant,servicio).
    /// </summary>
    private async Task<IReadOnlyList<Guid>?> ResolverEmpresaIdsParaGestorConCarteraAsync(Guid clienteId)
    {
        var usuarioId = await OtorgarCarteraAsync(clienteId);
        return await ResolverEmpresaIdsParaUsuarioAsync(usuarioId);
    }

    private async Task<IReadOnlyList<Guid>?> ResolverSubcontrataIdsParaGestorConCarteraAsync(Guid clienteId, IReadOnlyList<Guid> empresaIdsVisibles)
    {
        _ = empresaIdsVisibles; // el servicio resuelve sus propios EmpresaIds internamente; se recibe por claridad del escenario
        var usuarioId = await OtorgarCarteraAsync(clienteId);
        return await ResolverSubcontrataIdsParaUsuarioAsync(usuarioId);
    }

    private async Task<IReadOnlyList<Guid>?> ResolverEmpresaIdsParaUsuarioAsync(Guid usuarioId)
    {
        await using var contexto = CrearContexto(_tenant);
        var servicio = new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(usuarioId, "GestorCae", tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        return await servicio.ObtenerEmpresaIdsVisiblesAsync();
    }

    private async Task<IReadOnlyList<Guid>?> ResolverSubcontrataIdsParaUsuarioAsync(Guid usuarioId)
    {
        await using var contexto = CrearContexto(_tenant);
        var servicio = new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(usuarioId, "GestorCae", tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());

        return await servicio.ObtenerSubcontrataIdsVisiblesAsync();
    }

    private async Task<Guid> OtorgarCarteraAsync(Guid clienteId)
    {
        await using var contexto = CrearContexto(_tenant);
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(clienteId), ahora, null, ahora));

        await contexto.SaveChangesAsync();
        return usuarioId;
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
