using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>La matriz de la demo a dirección no puede perder un estado sin que un
/// test se ponga rojo.</b>
///
/// <para>
/// Todo se mide con el mismo servicio que pinta la pantalla
/// (<see cref="CalculoEstadoCentroService"/>) y con el mismo alcance que ve
/// cada usuario (<see cref="AlcanceDatosService"/>), sobre la siembra real
/// bajo RLS efectiva (<see cref="ArnesDeArranqueRuntime"/>) y en el orden real
/// del arranque —<see cref="DelegacionDemoSeeder"/>, escenarios, backfill—; las
/// esperas están escritas aquí a mano, no derivadas del sembrador, porque un
/// test que las calcula con la misma tabla que el sembrador confirma
/// cualquier cosa.
/// </para>
///
/// <para>
/// El resto de la siembra (los Tenants de la demo anterior) convive con la
/// matriz; solo se miran las filas de los Clientes empresariales del
/// catálogo, por razón social.
/// </para>
/// </summary>
public sealed class EscenariosDireccionDemoFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = ConfigurarEscenarios(Arnes, escenariosDireccion: true);

        using (var ambito = Arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);

            await EscenariosDireccionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        }

        // El backfill corre DESPUÉS en el arranque real (Program.cs): tiene que
        // dejar la matriz como está, no cerrar ni duplicar lo que ya sembró.
        await using var bootstrap = Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        await AsignacionesOperativasBackfillSeeder.SeedAsync(bootstrap, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();

    internal static IConfiguration ConfigurarEscenarios(ArnesDeArranqueRuntime arnes, bool escenariosDireccion) =>
        new ConfigurationBuilder()
            .AddConfiguration(arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EscenariosDireccionDemoSeeder.ClaveConfiguracion] = escenariosDireccion ? "true" : "false"
            })
            .Build();
}

public class EscenariosDireccionDemoTests(EscenariosDireccionDemoFixture fixture) : IClassFixture<EscenariosDireccionDemoFixture>
{
    private static readonly (string Tenant, ClienteEscenarioDemo Cliente)[] Clientes =
        CatalogoEscenariosDireccionDemo.Ramas
            .SelectMany(r => r.Clientes.Select(c => (r.NombreTenant, c)))
            .ToArray();

    /// <summary>
    /// Lo que cada escenario tiene que dar en sus tres centros, ordenados por
    /// código. Escrito a mano y aparte del sembrador (ver el comentario de clase).
    /// </summary>
    private static readonly Dictionary<EscenarioClienteDemo, EstadoCentro[]> EstadosEsperados = new()
    {
        [EscenarioClienteDemo.Completo] = [EstadoCentro.Vigente, EstadoCentro.Vigente, EstadoCentro.Vigente],
        [EscenarioClienteDemo.CasiCompleto] = [EstadoCentro.Vigente, EstadoCentro.Vigente, EstadoCentro.Proximo],
        [EscenarioClienteDemo.ConAccesoConDocumentacionPendiente] = [EstadoCentro.Vencido, EstadoCentro.Urgente, EstadoCentro.Faltante],
        [EscenarioClienteDemo.ConAccesoSinDocumentacionPendiente] = [EstadoCentro.Vigente, EstadoCentro.Vigente, EstadoCentro.Vigente],
        [EscenarioClienteDemo.AccesoBloqueado] = [EstadoCentro.Bloqueado, EstadoCentro.Vencido, EstadoCentro.Vigente],
        [EscenarioClienteDemo.AccesoPendienteDeConfirmacion] = [EstadoCentro.Vigente, EstadoCentro.Vigente, EstadoCentro.Vigente],
    };

    // ── La matriz, entera y medida ──────────────────────────────────────────

    [Fact]
    public void El_catalogo_cubre_los_seis_estados_de_acceso_al_menos_una_vez()
    {
        Clientes.Select(c => c.Cliente.Escenario).Distinct().Should().BeEquivalentTo(
            Enum.GetValues<EscenarioClienteDemo>(),
            "la matriz que pidió la dirección son seis estados, y cada uno tiene que estar sembrado");
    }

    [Fact]
    public async Task Cada_cliente_del_catalogo_esta_sembrado_con_tres_centros_y_ocho_trabajadores()
    {
        foreach (var (tenant, cliente) in Clientes)
        {
            var datos = await CargarAsync(tenant, cliente);

            datos.Centros.Should().HaveCount(3, $"MEDIDO ({cliente.RazonSocial}): tres centros por Cliente empresarial");
            datos.TrabajadoresAsignados.Should().Be(8, $"MEDIDO ({cliente.RazonSocial}): ocho trabajadores repartidos en los centros");
        }
    }

    [Fact]
    public async Task El_estado_de_cada_centro_es_el_que_pide_el_escenario_medido_con_el_servicio_real()
    {
        foreach (var (tenant, cliente) in Clientes)
        {
            var datos = await CargarAsync(tenant, cliente);

            datos.EstadosDeCentro.Should().Equal(EstadosEsperados[cliente.Escenario],
                $"MEDIDO ({cliente.RazonSocial}, {cliente.Escenario}): {string.Join(", ", datos.EstadosDeCentro)}");
        }
    }

    [Fact]
    public async Task La_acreditacion_ante_la_plataforma_del_cliente_sale_del_escenario()
    {
        foreach (var (tenant, cliente) in Clientes)
        {
            var estados = (await CargarAsync(tenant, cliente)).EstadosDeAcreditacion;

            estados.Should().NotBeEmpty($"MEDIDO ({cliente.RazonSocial}): hay acreditaciones sembradas");
            switch (cliente.Escenario)
            {
                case EscenarioClienteDemo.Completo:
                case EscenarioClienteDemo.CasiCompleto:
                case EscenarioClienteDemo.ConAccesoSinDocumentacionPendiente:
                    estados.Should().OnlyContain(e => e == EstadoAcreditacion.Aceptada, cliente.RazonSocial);
                    break;
                case EscenarioClienteDemo.AccesoPendienteDeConfirmacion:
                    estados.Should().OnlyContain(e => e == EstadoAcreditacion.Subida, cliente.RazonSocial);
                    break;
                case EscenarioClienteDemo.ConAccesoConDocumentacionPendiente:
                    estados.Should().OnlyContain(e => e == EstadoAcreditacion.PendienteDeSubir, cliente.RazonSocial);
                    break;
                case EscenarioClienteDemo.AccesoBloqueado:
                    estados.Should().Contain(EstadoAcreditacion.Rechazada, cliente.RazonSocial);
                    estados.Should().Contain(EstadoAcreditacion.PendienteDeSubir, cliente.RazonSocial);
                    break;
            }
        }
    }

    /// <summary>
    /// «Con acceso sin documentación pendiente» y «completo» comparten estado de
    /// centros y de acreditaciones; lo que los separa es la ficha (interpretación
    /// declarada en <see cref="EscenarioClienteDemo"/>). Si esa diferencia se
    /// pierde, los dos escenarios pasan a ser el mismo y la matriz enseña cinco.
    /// </summary>
    [Fact]
    public async Task La_ficha_completa_distingue_al_cliente_completo_del_de_acceso_sin_pendiente()
    {
        foreach (var (tenant, cliente) in Clientes)
        {
            var datos = await CargarAsync(tenant, cliente);
            var fichaCompleta = datos.Centros.All(c => c.Contacto is not null && c.ContratoVigenteHasta is not null);
            var fichaVacia = datos.Centros.All(c => c.Contacto is null && c.ContratoVigenteHasta is null);

            if (cliente.Escenario == EscenarioClienteDemo.ConAccesoSinDocumentacionPendiente)
                fichaVacia.Should().BeTrue($"MEDIDO ({cliente.RazonSocial}): ficha a medias");
            else
                fichaCompleta.Should().BeTrue($"MEDIDO ({cliente.RazonSocial}): ficha completa");
        }
    }

    [Fact]
    public async Task Hay_extranjeros_con_identificacion_valida_y_vigencias_al_dia_a_punto_de_vencer_y_vencida()
    {
        var estadosDeAutorizacion = new List<EstadoDocumento>();

        foreach (var (tenant, cliente) in Clientes)
        {
            var datos = await CargarAsync(tenant, cliente);

            datos.Extranjeros.Should().HaveCountGreaterOrEqualTo(3,
                $"MEDIDO ({cliente.RazonSocial}): tres extranjeros por Cliente empresarial (fuera de la UE, de la UE y de la subcontrata)");
            datos.Extranjeros.Should().OnlyContain(
                e => ValidadorIdentificacion.Analizar(e.Dni).EsValido && ValidadorIdentificacion.Analizar(e.Dni).Tipo == TipoIdentificacion.Nie,
                $"MEDIDO ({cliente.RazonSocial}): NIE con letra de control válida");

            estadosDeAutorizacion.AddRange(datos.Extranjeros.SelectMany(e => e.EstadosDeAutorizacion));
        }

        estadosDeAutorizacion.Should().Contain(
            [EstadoDocumento.Vigente, EstadoDocumento.Proximo, EstadoDocumento.Vencido],
            $"MEDIDO ({string.Join(", ", estadosDeAutorizacion.Distinct())}): la autorización de residencia de los extranjeros " +
            "tiene que aparecer al día, a punto de vencer y vencida en algún Cliente empresarial");
    }

    [Fact]
    public async Task Hay_trabajadores_con_documentacion_completa_a_punto_de_vencer_urgente_vencida_y_faltante()
    {
        var estados = new HashSet<EstadoDocumento>();
        foreach (var (tenant, cliente) in Clientes)
            estados.UnionWith((await CargarAsync(tenant, cliente)).EstadosDeAptitudYFaltantes);

        estados.Should().Contain(
            [EstadoDocumento.Vigente, EstadoDocumento.Proximo, EstadoDocumento.Urgente, EstadoDocumento.Vencido, EstadoDocumento.Faltante],
            $"MEDIDO ({string.Join(", ", estados)}): la plantilla enseña los cinco estados de documentación");
    }

    // ── Las carteras: quién ve qué ──────────────────────────────────────────

    [Fact]
    public async Task Cada_cliente_tiene_una_unica_cartera_vigente_y_es_de_su_gestor()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var usuarios = await bootstrap.Users
            .Where(u => u.Email == EscenariosDireccionDemoSeeder.EmailGestorPrimero || u.Email == EscenariosDireccionDemoSeeder.EmailGestorSegundo)
            .ToDictionaryAsync(u => u.Email!, u => u.Id);

        foreach (var (tenant, cliente) in Clientes)
        {
            var clienteId = await IdDeClienteAsync(bootstrap, tenant, cliente);
            var vigentes = await bootstrap.AsignacionesCartera
                .Where(c => c.AmbitoRelacionClienteId == clienteId && c.Estado == EstadoAsignacion.Vigente)
                .ToListAsync();

            var esperado = usuarios[cliente.Gestor == GestorDemo.Primero
                ? EscenariosDireccionDemoSeeder.EmailGestorPrimero
                : EscenariosDireccionDemoSeeder.EmailGestorSegundo];

            vigentes.Should().ContainSingle($"MEDIDO ({cliente.RazonSocial}): una sola cartera vigente")
                .Which.UsuarioId.Should().Be(esperado, $"MEDIDO ({cliente.RazonSocial}): la cartera es de su Gestor CAE");
        }
    }

    [Fact]
    public async Task Cada_gestor_ve_en_cada_tenant_exactamente_sus_clientes_y_el_coordinador_ve_los_de_ambos()
    {
        foreach (var rama in CatalogoEscenariosDireccionDemo.Ramas)
        {
            var esperadosPrimero = rama.Clientes.Where(c => c.Gestor == GestorDemo.Primero).Select(c => c.RazonSocial).Order().ToList();
            var esperadosSegundo = rama.Clientes.Where(c => c.Gestor == GestorDemo.Segundo).Select(c => c.RazonSocial).Order().ToList();
            var esperadosCoordinador = rama.Clientes.Select(c => c.RazonSocial).Order().ToList();

            (await ClientesVisiblesAsync(EscenariosDireccionDemoSeeder.EmailGestorPrimero, Roles.GestorCae, rama.NombreTenant))
                .Should().Equal(esperadosPrimero, $"MEDIDO ({rama.NombreTenant}): Gestor CAE primero");
            (await ClientesVisiblesAsync(EscenariosDireccionDemoSeeder.EmailGestorSegundo, Roles.GestorCae, rama.NombreTenant))
                .Should().Equal(esperadosSegundo, $"MEDIDO ({rama.NombreTenant}): Gestor CAE segundo");
            (await ClientesVisiblesAsync(EscenariosDireccionDemoSeeder.EmailCoordinador, Roles.CoordinadorCae, rama.NombreTenant))
                .Should().Equal(esperadosCoordinador, $"MEDIDO ({rama.NombreTenant}): Coordinador CAE, cartera de los dos Gestores CAE");
        }
    }

    [Fact]
    public void El_primer_gestor_lleva_seis_tenants_y_el_segundo_tres()
    {
        var porGestor = CatalogoEscenariosDireccionDemo.Ramas
            .SelectMany(r => r.Clientes.Select(c => (r.NombreTenant, c.Gestor)))
            .GroupBy(x => x.Gestor)
            .ToDictionary(g => g.Key, g => g.Select(x => x.NombreTenant).Distinct().Count());

        porGestor[GestorDemo.Primero].Should().Be(6, "una cartera en cada uno de los seis Tenants propietarios");
        porGestor[GestorDemo.Segundo].Should().Be(3, "un segundo Gestor CAE con carteras en tres, para que el Coordinador CAE tenga dos que ver");
    }

    [Fact]
    public async Task El_backfill_posterior_no_cierra_ni_duplica_las_carteras_de_la_matriz()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var clienteIds = new List<Guid>();
        foreach (var (tenant, cliente) in Clientes)
            clienteIds.Add(await IdDeClienteAsync(bootstrap, tenant, cliente));

        var porCliente = await bootstrap.AsignacionesCartera
            .Where(c => c.AmbitoRelacionClienteId != null && clienteIds.Contains(c.AmbitoRelacionClienteId.Value))
            .GroupBy(c => c.AmbitoRelacionClienteId)
            .Select(g => new { g.Key, Vigentes = g.Count(c => c.Estado == EstadoAsignacion.Vigente), Todas = g.Count() })
            .ToListAsync();

        porCliente.Should().HaveCount(Clientes.Length);
        porCliente.Should().OnlyContain(x => x.Vigentes == 1 && x.Todas == 1,
            "MEDIDO: tras el backfill (que corre en el fixture, como en Program.cs) cada Cliente empresarial " +
            "sigue con su única cartera vigente, sin cerradas ni repetidas");
    }

    // ── Vigencias relativas a hoy ───────────────────────────────────────────

    [Theory]
    [InlineData(VigenciaDemo.AlDia, EstadoDocumento.Vigente)]
    [InlineData(VigenciaDemo.APuntoDeVencer, EstadoDocumento.Proximo)]
    [InlineData(VigenciaDemo.Urgente, EstadoDocumento.Urgente)]
    [InlineData(VigenciaDemo.Vencida, EstadoDocumento.Vencido)]
    public void Cada_vigencia_de_demo_sigue_en_su_banda_una_semana_despues_de_sembrar(VigenciaDemo vigencia, EstadoDocumento esperado)
    {
        var hoy = new DateOnly(2026, 9, 20);
        var vence = hoy.AddDays(vigencia.DiasHastaVencimiento());

        foreach (var diasDespues in new[] { 0, 3, 7 })
        {
            CalculadoraEstadoDocumento.Calcular(
                vence, hoy.AddDays(diasDespues), ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias)
                .Should().Be(esperado,
                    $"{vigencia} sembrada hoy tiene que seguir siendo {esperado} {diasDespues} días después: " +
                    "la demo puede ser al día siguiente");
        }
    }

    [Fact]
    public void Todos_los_tenants_del_catalogo_estan_en_la_allowlist_de_retirada()
    {
        RetiradaTenantDemoService.NombresTenantsDeDemo.Should().Contain(
            CatalogoEscenariosDireccionDemo.Ramas.Select(r => r.NombreTenant),
            "un Tenant de demo que no está en la allowlist no se puede retirar: la reversión de la demo se quedaría coja");
    }

    // ── Medición ────────────────────────────────────────────────────────────

    private sealed record ExtranjeroMedido(string Dni, IReadOnlyList<EstadoDocumento> EstadosDeAutorizacion);

    private sealed record DatosMedidos(
        IReadOnlyList<Centro> Centros,
        IReadOnlyList<EstadoCentro> EstadosDeCentro,
        int TrabajadoresAsignados,
        IReadOnlyList<EstadoAcreditacion> EstadosDeAcreditacion,
        IReadOnlyList<ExtranjeroMedido> Extranjeros,
        IReadOnlyList<EstadoDocumento> EstadosDeAptitudYFaltantes);

    private async Task<Guid> IdDeClienteAsync(CaeManagerDbContext bootstrap, string tenant, ClienteEscenarioDemo cliente)
    {
        var tenantId = await bootstrap.Tenants.Where(t => t.Nombre == tenant).Select(t => t.Id).SingleAsync();
        return await bootstrap.Empresas.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.EsCritico != null && e.RazonSocial == cliente.RazonSocial)
            .Select(e => e.Id)
            .SingleAsync();
    }

    private async Task<DatosMedidos> CargarAsync(string tenant, ClienteEscenarioDemo cliente)
    {
        Guid tenantId;
        await using (var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
            tenantId = await bootstrap.Tenants.Where(t => t.Nombre == tenant).Select(t => t.Id).SingleAsync();

        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var clienteId = await contexto.Empresas
                .Where(e => e.EsCritico != null && e.RazonSocial == cliente.RazonSocial)
                .Select(e => e.Id).SingleAsync();

            var centros = await contexto.Centros.Where(c => c.ClienteId == clienteId).OrderBy(c => c.CodigoCentro).ToListAsync();
            var centroIds = centros.Select(c => c.Id).ToList();

            var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto);
            var resultado = await servicio.CalcularAsync(centroIds, CancellationToken.None);

            var trabajadorIds = await contexto.Asignaciones
                .Where(a => centroIds.Contains(a.CentroId) && a.FechaBaja == null)
                .Select(a => a.TrabajadorId).Distinct().ToListAsync();

            var acreditaciones = await contexto.AcreditacionesDocumentoPlataforma
                .Where(a => contexto.Documentos.Any(d => d.Id == a.DocumentoId && d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value)))
                .Select(a => a.Estado).ToListAsync();

            var parametros = await contexto.ParametrosSistema.SingleAsync();
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
            var tipos = await contexto.TiposDocumento.ToDictionaryAsync(t => t.Id, t => t.Nombre);

            var trabajadores = await contexto.Trabajadores.Where(t => trabajadorIds.Contains(t.Id)).ToListAsync();
            var documentos = await contexto.Documentos.Where(d => d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value)).ToListAsync();

            EstadoDocumento Estado(Documento d) =>
                CalculadoraEstadoDocumento.Calcular(d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);

            var extranjeros = trabajadores
                .Where(t => t.Dni is { Length: > 0 } dni && dni[0] == 'X')
                .Select(t => new ExtranjeroMedido(
                    t.Dni!,
                    documentos.Where(d => d.TrabajadorId == t.Id
                                          && tipos[d.TipoDocumentoId] is "Permiso de residencia" or "Certificado de Registro de Ciudadano de la UE")
                        .Select(Estado).ToList()))
                .ToList();

            // Los estados de la documentación de la plantilla: la aptitud médica
            // y la formación por su vigencia, y los huecos que el propio servicio
            // de centros marca como Faltante.
            var estadosPlantilla = documentos
                .Where(d => tipos[d.TipoDocumentoId] is "Certificado de aptitud médica" or "Formación Art. 19")
                .Select(Estado).ToList();
            if (resultado.Values.SelectMany(r => r.Causas).Any(c => c.Estado == EstadoDocumento.Faltante))
                estadosPlantilla.Add(EstadoDocumento.Faltante);

            return new DatosMedidos(
                centros,
                centros.Select(c => resultado[c.Id].Estado).ToList(),
                trabajadorIds.Count,
                acreditaciones,
                extranjeros,
                estadosPlantilla);
        }
    }

    /// <summary>
    /// Los Clientes empresariales que ve un usuario del Operador CAE dentro de
    /// un Tenant propietario, con el alcance real de la aplicación. Ordenados por
    /// razón social.
    /// </summary>
    private async Task<List<string>> ClientesVisiblesAsync(string email, string rol, string tenant)
    {
        Guid tenantPropietarioId, usuarioId, tenantOrigenId;
        await using (var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
        {
            tenantPropietarioId = await bootstrap.Tenants.Where(t => t.Nombre == tenant).Select(t => t.Id).SingleAsync();
            var usuario = await bootstrap.Users.SingleAsync(u => u.Email == email);
            (usuarioId, tenantOrigenId) = (usuario.Id, usuario.TenantId);
        }

        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            var alcance = new AlcanceDatosService(
                contexto,
                new UsuarioDeMedicion(usuarioId, rol, tenantOrigenId),
                new TenantActualAmbiental { TenantId = tenantPropietarioId },
                new SinSesionPrivilegiada());

            var ids = await alcance.ObtenerClienteIdsVisiblesAsync();
            ids.Should().NotBeNull($"{email} no es de alcance total: su lista de Clientes empresariales es acotada");

            var delCatalogo = CatalogoEscenariosDireccionDemo.Ramas
                .SelectMany(x => x.Clientes).Select(c => c.RazonSocial).ToList();

            return await contexto.Empresas
                .Where(e => ids!.Contains(e.Id) && e.EsCritico != null && delCatalogo.Contains(e.RazonSocial))
                .Select(e => e.RazonSocial)
                .OrderBy(r => r)
                .ToListAsync();
        }
    }

    private sealed class UsuarioDeMedicion(Guid usuarioId, string rol, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}

/// <summary>
/// Las propiedades del propio arranque, cada una sobre una base nueva: inerte
/// por defecto, rechazo en Producción e idempotencia.
/// </summary>
public class EscenariosDireccionDemoArranqueTests
{
    [Fact]
    public async Task Sin_el_flag_no_se_siembra_nada_aunque_DatosPrueba_este_activo()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var configuracion = EscenariosDireccionDemoFixture.ConfigurarEscenarios(arnes, escenariosDireccion: false);

        await SembrarEscenariosAsync(arnes, configuracion, EntornoDePrueba.Desarrollo);

        await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        (await bootstrap.Tenants.CountAsync(t => t.Nombre == CatalogoEscenariosDireccionDemo.NombreTenantDuff))
            .Should().Be(0, "MEDIDO: inerte por defecto — sin DatosPrueba:EscenariosDireccion no nace ni un Tenant");
        (await bootstrap.Users.CountAsync(u => u.Email == EscenariosDireccionDemoSeeder.EmailCoordinador))
            .Should().Be(0, "MEDIDO: ni el equipo del Operador CAE");
    }

    [Fact]
    public async Task En_Produccion_lanza_y_no_siembra_nada_aunque_las_dos_claves_esten_activas()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var configuracion = EscenariosDireccionDemoFixture.ConfigurarEscenarios(arnes, escenariosDireccion: true);

        var siembra = () => SembrarEscenariosAsync(arnes, configuracion, new EntornoDePrueba("Production"));

        await siembra.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Producción*", "MEDIDO: esta siembra comparte la contraseña de la demo local y no puede correr en Producción");

        await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        (await bootstrap.Tenants.CountAsync(t => t.Nombre == CatalogoEscenariosDireccionDemo.NombreTenantDuff))
            .Should().Be(0, "MEDIDO: el rechazo es previo a cualquier escritura");
    }

    [Fact]
    public async Task Sembrar_dos_veces_no_duplica_nada()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var configuracion = EscenariosDireccionDemoFixture.ConfigurarEscenarios(arnes, escenariosDireccion: true);

        await SembrarDemoAnteriorAsync(arnes, configuracion);
        await SembrarEscenariosAsync(arnes, configuracion, EntornoDePrueba.Desarrollo);
        var primera = await ContarAsync(arnes);

        await SembrarEscenariosAsync(arnes, configuracion, EntornoDePrueba.Desarrollo);
        var segunda = await ContarAsync(arnes);

        primera.Clientes.Should().Be(CatalogoEscenariosDireccionDemo.Ramas.Sum(r => r.Clientes.Count),
            "guarda del propio test: la primera pasada sí sembró el catálogo entero");
        segunda.Should().Be(primera, "MEDIDO: una segunda pasada no añade Clientes, trabajadores, documentos ni carteras");
    }

    private sealed record Conteo(int Clientes, int Trabajadores, int Documentos, int Acreditaciones, int Carteras, int Usuarios);

    private static async Task<Conteo> ContarAsync(ArnesDeArranqueRuntime arnes)
    {
        await using var bootstrap = arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var nombres = CatalogoEscenariosDireccionDemo.Ramas.SelectMany(r => r.Clientes.Select(c => c.RazonSocial)).ToList();
        var clienteIds = await bootstrap.Empresas.IgnoreQueryFilters()
            .Where(e => e.EsCritico != null && nombres.Contains(e.RazonSocial)).Select(e => e.Id).ToListAsync();

        return new Conteo(
            clienteIds.Count,
            await bootstrap.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.Dni!.StartsWith("X") || t.Dni!.StartsWith("6")),
            await bootstrap.Documentos.IgnoreQueryFilters().CountAsync(),
            await bootstrap.AcreditacionesDocumentoPlataforma.IgnoreQueryFilters().CountAsync(),
            await bootstrap.AsignacionesCartera.CountAsync(c => c.AmbitoRelacionClienteId != null && clienteIds.Contains(c.AmbitoRelacionClienteId.Value)),
            await bootstrap.Users.CountAsync(u => u.Email!.Contains(".arcospa@")));
    }

    private static async Task SembrarDemoAnteriorAsync(ArnesDeArranqueRuntime arnes, IConfiguration configuracion)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await DelegacionDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(),
            configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    private static async Task SembrarEscenariosAsync(
        ArnesDeArranqueRuntime arnes, IConfiguration configuracion, Microsoft.Extensions.Hosting.IHostEnvironment entorno)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await EscenariosDireccionDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(),
            sp.GetRequiredService<UserManager<ApplicationUser>>(),
            configuracion, entorno, NullLogger.Instance);
    }
}
