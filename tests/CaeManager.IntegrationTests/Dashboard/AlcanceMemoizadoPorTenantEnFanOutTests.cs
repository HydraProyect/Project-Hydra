using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// REC-195 — regresión permanente, no reproducción puntual. Hasta PR #571
/// <see cref="AlcanceDatosService"/> memoizaba los seis alcances en un único
/// valor por instancia; el fan-out de <see cref="ObtenerDashboardEjecutivoQuery"/>
/// reutiliza la MISMA instancia scoped para varios Tenants beneficiarios,
/// cambiando solo <see cref="AmbitoTenantExplicito"/> en cada vuelta del
/// bucle, así que la cartera resuelta en la primera vuelta se servía sin
/// revisar a los siguientes. Con dos Tenants beneficiarios de cartera
/// distinta (uno acotado, otro universal) el segundo salía con CERO filas
/// —no con datos ajenos: el filtro global de tenant seguía puesto, lo
/// envenenado era la restricción de cartera (Asignación de Cartera), no el
/// aislamiento (<c>tenant isolation ≠ operational delegation</c>, ADR-011 § 1).
///
/// PR #571 resolvió la causa técnica indexando cada alcance por
/// <c>TenantId</c> (<see cref="AlcanceDatosService"/>, diccionarios en vez de
/// campos escalares). Este fichero exige, sobre el fan-out real y de punta a
/// punta, que la suma quede correcta y que el alcance no quede envenenado
/// tras el bucle — antes solo se había medido con el escenario de acceso
/// total de #571 (<c>FanOutMultiTenantFugaDeAlcanceTests</c>), no con el de
/// cero filas silenciosas que reprodujo esta ficha.
///
/// Banco: <see cref="ObtenerDashboardEjecutivoQuery"/> end-to-end (llega al
/// alcance por Cliente y por Centro/Trabajador a la vez), con el fan-out,
/// <c>AmbitoTenantExplicito</c> y <c>AlcanceDatosService</c> REALES y sin
/// tocar. La única pieza sustituida es
/// <see cref="ObtenerClientesAutorizadosQuery"/> — se reemplaza su handler
/// por uno que devuelve la lista de Tenants en el ORDEN exacto que cada test
/// pide, para poder invertir el orden de los mismos dos Tenants
/// beneficiarios sin que la ordenación real (origen primero, delegados por
/// nombre) lo confunda con un cambio de datos. Esa query no forma parte del
/// mecanismo bajo prueba — solo decide QUIÉN entra al bucle, no cómo se
/// memoiza el alcance dentro de él — así que sustituirla no reduce la
/// fidelidad de la reproducción.
/// </summary>
public class AlcanceMemoizadoPorTenantEnFanOutTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioGestor = Guid.NewGuid();
    private readonly Guid _tenantConsultora = Guid.NewGuid();
    private CaeManagerDbContext _dbContext = null!;

    private Guid _tenantAlfa;
    private Guid _tenantBeta;
    private Guid _clienteA1;
    private Guid _clienteB1;
    private Guid _clienteB2;

    /// <summary>Tenant Alfa: cartera ACOTADA a un solo Cliente (ClienteA1) de los dos que tiene → 1 Centro, 2 Trabajadores.</summary>
    private const string NombreAlfa = "Tenant Alfa (cartera acotada)";

    /// <summary>Tenant Beta: cartera UNIVERSAL (ve sus dos Clientes) → 2 Centros, 9 Trabajadores.</summary>
    private const string NombreBeta = "Tenant Beta (cartera universal)";

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualPorAmbitoExplicito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await _dbContext.Database.MigrateAsync();

        var tenantConsultora = new Tenant("Consultora Operadora");
        // El Tenant "Consultora" no necesita datos propios: nunca entra en la
        // lista fabricada de Tenants beneficiarios, solo presta su Id como
        // OperadorTenantId de las dos operaciones externas de abajo.
        var tenantAlfa = new Tenant(NombreAlfa);
        var tenantBeta = new Tenant(NombreBeta);

        // Tenant/DelegacionTenant/AsignacionOperacion/AsignacionCartera son
        // catálogo global (Entity, no EntidadConTenant — ver
        // AsignacionResponsabilidad.cs): no pasan por el filtro de tenant y no
        // necesitan AmbitoTenantExplicito para escribirse.
        _dbContext.Tenants.AddRange(tenantConsultora, tenantAlfa, tenantBeta);
        await _dbContext.SaveChangesAsync();

        _tenantAlfa = tenantAlfa.Id;
        _tenantBeta = tenantBeta.Id;

        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-1);

        // --- Tenant Alfa: 2 Clientes, cartera del gestor acotada a uno solo ---
        using (AmbitoTenantExplicito.Establecer(_tenantAlfa))
        {
            var clienteA1 = Empresa.CrearComoCliente("Cliente A1 (en cartera)", GenerarCifValido(1), false, null, null);
            var clienteA2 = Empresa.CrearComoCliente("Cliente A2 (fuera de cartera)", GenerarCifValido(2), false, null, null);
            var contrataA = new Empresa("Contrata Alfa S.L.", GenerarCifValido(3));
            _dbContext.Empresas.AddRange(clienteA1, clienteA2, contrataA);
            _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            await _dbContext.SaveChangesAsync();
            _clienteA1 = clienteA1.Id;

            var centroA1 = new Centro(clienteA1.Id, contrataA.Id, "Centro A1 (en cartera)");
            var centroA2 = new Centro(clienteA2.Id, contrataA.Id, "Centro A2 (fuera de cartera)");
            _dbContext.Centros.AddRange(centroA1, centroA2);
            await _dbContext.SaveChangesAsync();

            for (var i = 0; i < 2; i++)
            {
                var trabajador = Trabajador.DeEmpresa(contrataA.Id, $"TrabA1_{i}", "Apellido", GenerarDni(100 + i));
                _dbContext.Trabajadores.Add(trabajador);
                await _dbContext.SaveChangesAsync();
                _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centroA1.Id, DateOnly.FromDateTime(desde)));
            }

            for (var i = 0; i < 3; i++)
            {
                var trabajador = Trabajador.DeEmpresa(contrataA.Id, $"TrabA2_{i}", "Apellido", GenerarDni(200 + i));
                _dbContext.Trabajadores.Add(trabajador);
                await _dbContext.SaveChangesAsync();
                _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centroA2.Id, DateOnly.FromDateTime(desde)));
            }

            await _dbContext.SaveChangesAsync();
        }

        // --- Tenant Beta: 2 Clientes, cartera del MISMO gestor universal ---
        using (AmbitoTenantExplicito.Establecer(_tenantBeta))
        {
            var clienteB1 = Empresa.CrearComoCliente("Cliente B1", GenerarCifValido(4), false, null, null);
            var clienteB2 = Empresa.CrearComoCliente("Cliente B2", GenerarCifValido(5), false, null, null);
            var contrataB = new Empresa("Contrata Beta S.L.", GenerarCifValido(6));
            _dbContext.Empresas.AddRange(clienteB1, clienteB2, contrataB);
            _dbContext.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            await _dbContext.SaveChangesAsync();
            _clienteB1 = clienteB1.Id;
            _clienteB2 = clienteB2.Id;

            var centroB1 = new Centro(clienteB1.Id, contrataB.Id, "Centro B1");
            var centroB2 = new Centro(clienteB2.Id, contrataB.Id, "Centro B2");
            _dbContext.Centros.AddRange(centroB1, centroB2);
            await _dbContext.SaveChangesAsync();

            for (var i = 0; i < 4; i++)
            {
                var trabajador = Trabajador.DeEmpresa(contrataB.Id, $"TrabB1_{i}", "Apellido", GenerarDni(300 + i));
                _dbContext.Trabajadores.Add(trabajador);
                await _dbContext.SaveChangesAsync();
                _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centroB1.Id, DateOnly.FromDateTime(desde)));
            }

            for (var i = 0; i < 5; i++)
            {
                var trabajador = Trabajador.DeEmpresa(contrataB.Id, $"TrabB2_{i}", "Apellido", GenerarDni(400 + i));
                _dbContext.Trabajadores.Add(trabajador);
                await _dbContext.SaveChangesAsync();
                _dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centroB2.Id, DateOnly.FromDateTime(desde)));
            }

            await _dbContext.SaveChangesAsync();
        }

        // --- Asignaciones de Cartera del mismo Gestor CAE, una por Tenant ---
        // Alfa: operación externa (la Consultora opera Alfa), cartera ACOTADA
        // a ClienteA1 — el Gestor CAE solo debería ver ese Cliente en Alfa.
        var operacionAlfa = AsignacionOperacion.Externa(
            _tenantAlfa, _tenantConsultora, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var carteraAlfa = AsignacionCartera.Externa(
            operacionAlfa, _usuarioGestor, Roles.GestorCae, AmbitoAsignacion.DeRelacionCliente(_clienteA1), desde, null, ahora);

        // Beta: misma forma, cartera UNIVERSAL — el Gestor CAE ve los dos
        // Clientes de Beta.
        var operacionBeta = AsignacionOperacion.Externa(
            _tenantBeta, _tenantConsultora, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var carteraBeta = AsignacionCartera.Externa(
            operacionBeta, _usuarioGestor, Roles.GestorCae, AmbitoAsignacion.Universal, desde, null, ahora);

        _dbContext.AsignacionesOperacion.AddRange(operacionAlfa, operacionBeta);
        _dbContext.AsignacionesCartera.AddRange(carteraAlfa, carteraBeta);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await _dbContext.DisposeAsync();
    }

    /// <summary>
    /// Antes de #571: Beta (segunda vuelta) heredaba sin resolver la cartera
    /// acotada de Alfa y aportaba CERO al fusionado. Ahora cada Tenant
    /// beneficiario resuelve su propia Asignación de Cartera sin importar
    /// cuál se procesó antes: la suma tiene que ser la de los dos —3 Centros
    /// (1 de Alfa + 2 de Beta), 11 Trabajadores (2 de Alfa + 9 de Beta)—, no
    /// solo la del primero.
    /// </summary>
    [Fact]
    public async Task Orden_Alfa_luego_Beta_suma_las_dos_carteras_sin_envenenar_a_beta()
    {
        var (resultado, alcance, proveedor) = await EjecutarDashboardEjecutivoAsync(EnEsteOrden: [_tenantAlfa, _tenantBeta]);
        await using var _ = proveedor;

        resultado.TotalTenants.Should().Be(2, "el fan-out procesó los dos Tenants beneficiarios");
        resultado.Centros.Should().Be(3, "Alfa aporta su único Centro en cartera y Beta sus 2 propios, sin que la cartera de Alfa la envenene");
        resultado.TrabajadoresActivos.Should().Be(11, "Alfa aporta sus 2 trabajadores en cartera y Beta sus 9, sin heredar el cero del defecto de REC-195");

        await VerificarSinEnvenenamientoTrasElBucleAsync(alcance);
    }

    /// <summary>
    /// Mismo mecanismo, orden invertido: la regresión exige que la suma sea
    /// IDÉNTICA a la del orden anterior. Antes de #571 el resultado se
    /// invertía con el orden (esto era precisamente el "control positivo del
    /// orden" que demostraba el defecto); ahora el orden no puede cambiar el
    /// resultado fusionado, porque cada Tenant resuelve su propia cartera
    /// desde su propia entrada del diccionario indexado por TenantId.
    /// </summary>
    [Fact]
    public async Task Orden_Beta_luego_Alfa_da_la_misma_suma_el_orden_ya_no_importa()
    {
        var (resultado, alcance, proveedor) = await EjecutarDashboardEjecutivoAsync(EnEsteOrden: [_tenantBeta, _tenantAlfa]);
        await using var _ = proveedor;

        resultado.TotalTenants.Should().Be(2);
        resultado.Centros.Should().Be(3, "misma suma que en el orden Alfa→Beta: el orden ya no determina qué mitad de los datos queda envenenada");
        resultado.TrabajadoresActivos.Should().Be(11);

        await VerificarSinEnvenenamientoTrasElBucleAsync(alcance);
    }

    /// <summary>
    /// Control de línea base, sin fan-out: cada Tenant, procesado EN SOLITARIO
    /// (bucle de un solo elemento, primera y única vuelta — nunca puede
    /// heredar nada memoizado de otro), resuelve su propia cartera
    /// correctamente. Esto es lo que descarta que la suma de los dos tests de
    /// arriba sea un acierto de los datos sembrados en vez de una resolución
    /// real por tenant: si Beta no diera 2 Centros/9 Trabajadores en
    /// solitario, la suma de "Orden_Alfa_luego_Beta" no demostraría nada.
    /// </summary>
    [Fact]
    public async Task Linea_base_cada_tenant_en_solitario_resuelve_su_propia_cartera_sin_envenenar_nada()
    {
        var (soloAlfa, _, proveedorAlfa) = await EjecutarDashboardEjecutivoAsync(EnEsteOrden: [_tenantAlfa]);
        await using (proveedorAlfa)
        {
            soloAlfa.TotalTenants.Should().Be(1);
            soloAlfa.Centros.Should().Be(1, "en solitario, Alfa ve su único Centro en cartera (Centro A1)");
            soloAlfa.TrabajadoresActivos.Should().Be(2, "en solitario, Alfa ve sus 2 trabajadores de Centro A1, no los 5 de A1+A2");
        }

        var (soloBeta, _, proveedorBeta) = await EjecutarDashboardEjecutivoAsync(EnEsteOrden: [_tenantBeta]);
        await using (proveedorBeta)
        {
            soloBeta.TotalTenants.Should().Be(1);
            soloBeta.Centros.Should().Be(2, "en solitario, Beta ve sus 2 Centros (cartera universal)");
            soloBeta.TrabajadoresActivos.Should().Be(9, "en solitario, Beta ve sus 9 trabajadores (4+5)");
        }
    }

    /// <summary>
    /// El instrumento tiene que poder distinguir el escenario correcto del
    /// envenenado: invertir el orden de los mismos dos Tenants beneficiarios
    /// no puede cambiar la suma fusionada. Si cambiara, el mecanismo no
    /// estaría resolviendo cada tenant por su cuenta.
    /// </summary>
    [Fact]
    public async Task Control_instrumento_invertir_el_orden_no_cambia_la_suma_fusionada()
    {
        var (ordenAlfaBeta, _, proveedorAlfaBeta) = await EjecutarDashboardEjecutivoAsync(EnEsteOrden: [_tenantAlfa, _tenantBeta]);
        await using var _1 = proveedorAlfaBeta;
        var (ordenBetaAlfa, _, proveedorBetaAlfa) = await EjecutarDashboardEjecutivoAsync(EnEsteOrden: [_tenantBeta, _tenantAlfa]);
        await using var _2 = proveedorBetaAlfa;

        ordenAlfaBeta.Centros.Should().Be(ordenBetaAlfa.Centros,
            "el orden de los Tenants beneficiarios no puede cambiar cuál cartera se resuelve para cada uno");
        ordenAlfaBeta.TrabajadoresActivos.Should().Be(ordenBetaAlfa.TrabajadoresActivos);
        ordenAlfaBeta.Centros.Should().Be(3);
        ordenAlfaBeta.TrabajadoresActivos.Should().Be(11);
    }

    /// <summary>
    /// § "¿Sobrevive al bucle?" del handoff original (HO-195-01): tras el
    /// último <c>using</c> del fan-out (ambiente ya sin
    /// <c>AmbitoTenantExplicito</c>, como al volver al resto del circuito
    /// Blazor), la MISMA instancia scoped de <c>AlcanceDatosService</c> se
    /// interroga de nuevo por CADA uno de los dos Tenants: si el alcance de
    /// uno se sirviera del último visitado en el bucle en vez de resolver el
    /// suyo propio, esto lo detecta — es exactamente la propiedad que la
    /// mutación de este incremento revierte a propósito para comprobar que
    /// el test la ve.
    ///
    /// El llamador mantiene vivo el <c>ServiceProvider</c>/<c>scope</c> de
    /// <see cref="EjecutarDashboardEjecutivoAsync"/> mientras dura esta
    /// comprobación (revisión de Codex, 2026-09-17): <c>AlcanceDatosService</c>
    /// no implementa <c>IDisposable</c> hoy, así que interrogarlo tras
    /// disponer su scope no falla, pero no demuestra la propiedad DENTRO del
    /// ciclo de vida que el fan-out real usa — una futura pieza inyectada que
    /// sí se disponga rompería esto en silencio.
    /// </summary>
    private async Task VerificarSinEnvenenamientoTrasElBucleAsync(IAlcanceDatosService alcance)
    {
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull(
            "el using del fan-out ya se cerró: estamos fuera de todo AmbitoTenantExplicito, como el resto del circuito");

        IReadOnlyList<Guid>? clienteIdsAlfa;
        using (AmbitoTenantExplicito.Establecer(_tenantAlfa))
            clienteIdsAlfa = await alcance.ObtenerClienteIdsVisiblesAsync();

        clienteIdsAlfa.Should().BeEquivalentTo([_clienteA1],
            "Alfa sigue resolviendo su propia cartera acotada con la misma instancia scoped, sin importar qué Tenant se procesó después en el bucle");

        IReadOnlyList<Guid>? clienteIdsBeta;
        using (AmbitoTenantExplicito.Establecer(_tenantBeta))
            clienteIdsBeta = await alcance.ObtenerClienteIdsVisiblesAsync();

        clienteIdsBeta.Should().BeEquivalentTo([_clienteB1, _clienteB2],
            "Beta sigue resolviendo su propia cartera universal con la misma instancia scoped, sin importar el orden del fan-out");
    }

    /// <summary>
    /// Devuelve también el <c>ServiceProvider</c> (revisión de Codex,
    /// 2026-09-17): antes se disponía aquí mismo con <c>await using</c>/
    /// <c>using</c> antes de retornar, y el llamador interrogaba
    /// <c>Alcance</c> ya con el scope disuelto — funcionaba hoy porque
    /// <c>AlcanceDatosService</c> no implementa <c>IDisposable</c>, pero no
    /// demostraba la propiedad dentro del ciclo de vida real del fan-out. El
    /// llamador dispone el proveedor con <c>await using</c> tras terminar de
    /// interrogar <c>Alcance</c> — disponer el proveedor raíz dispone
    /// también el scope hijo que creó (comportamiento documentado del
    /// contenedor por defecto de Microsoft.Extensions.DependencyInjection).
    /// </summary>
    private async Task<(DashboardEjecutivoDto Resultado, IAlcanceDatosService Alcance, ServiceProvider Proveedor)> EjecutarDashboardEjecutivoAsync(
        IReadOnlyList<Guid> EnEsteOrden)
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var tenantActual = new TenantActualPorAmbitoExplicito();
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Subcontratas.ISubcontratasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Notificaciones.INotificacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Vehiculos.IVehiculosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Auditoria.IAuditoriaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Facturacion.IFacturacionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Proyectos.IProyectosQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Retencion.IRetencionQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Incidencias.IIncidenciasQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Telemetria.ITelemetriaQueryContext>(_dbContext);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(_dbContext);

        // El mecanismo bajo prueba, EXACTO al de producción: registro
        // AddScoped (InfrastructureServiceCollectionExtensions.cs:431) sobre
        // la implementación real — nada de AlcanceDatosServiceFalso.
        servicios.AddScoped<IAlcanceDatosService, AlcanceDatosService>();
        servicios.AddSingleton<ISesionPrivilegiadaActual>(new SesionPrivilegiadaAusente());

        // Rol fijo "GestorCae" para TODA la sesión — hallazgo secundario del
        // retorno original de HO-195-01: ICurrentUserService.ObtenerRolActualAsync
        // (real, Web/CurrentUserService.cs) consulta IClienteActivoSeleccionado,
        // no AmbitoTenantExplicito, así que el rol NUNCA varía con la vuelta
        // del fan-out. Fijarlo aquí reproduce fielmente esa realidad, no la
        // simplifica.
        servicios.AddSingleton<ICurrentUserService>(
            new CurrentUserServiceFalso(_usuarioGestor, Roles.GestorCae, _tenantConsultora));

        servicios.AddSingleton<IDirectorioUsuariosService>(new DirectorioUsuariosServiceVacio());

        // Única sustitución deliberada (ver doc-comment de la clase): el
        // orden de los Tenants beneficiarios lo fija el test, no
        // ObtenerClientesAutorizadosQueryHandler.
        var clientesEnOrden = EnEsteOrden
            .Select(id => new ClienteAutorizadoDto(id, id == _tenantAlfa ? NombreAlfa : NombreBeta, EsOrigen: false))
            .ToList();
        servicios.AddSingleton<IRequestHandler<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>>(
            new ObtenerClientesAutorizadosQueryHandlerFalso(clientesEnOrden));

        var proveedor = servicios.BuildServiceProvider();
        var scope = proveedor.CreateScope();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var resultado = await mediator.Send(new ObtenerDashboardEjecutivoQuery(PeriodoKpi.MesActual(DateTime.UtcNow)));

        // Misma instancia scoped que usó el fan-out: si su memoización
        // sobrevive envenenada, esto es lo que hay que interrogar después,
        // con el proveedor y su scope todavía vivos (el llamador los
        // dispone al terminar).
        var alcance = scope.ServiceProvider.GetRequiredService<IAlcanceDatosService>();

        return (resultado, alcance, proveedor);
    }

    private static string GenerarDni(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }

    /// <summary>CIF con dígito de control real (organización "B"), mismo algoritmo que AislamientoPorAgregadoTests/ReclamacionDocumentalTests.GenerarCifValido, indexado para no colisionar.</summary>
    private static string GenerarCifValido(int indice)
    {
        var digitos = (indice + 1).ToString().PadLeft(7, '0');
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    /// <summary>
    /// Mismo comportamiento que <c>Web/Services/TenantActual.cs</c> en el eje
    /// que este instrumento necesita: consulta <c>AmbitoTenantExplicito</c>
    /// EN VIVO en cada lectura (no lo memoiza), exactamente como hace la
    /// implementación real (TenantActual.cs) antes de mirar el claim de
    /// sesión. Sin sesión de Blazor en este arnés, así que fuera del
    /// AmbitoTenantExplicito no hay tenant — comportamiento correcto para lo
    /// que este test necesita medir.
    /// </summary>
    private sealed class TenantActualPorAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class ObtenerClientesAutorizadosQueryHandlerFalso(IReadOnlyList<ClienteAutorizadoDto> enEsteOrden)
        : IRequestHandler<ObtenerClientesAutorizadosQuery, IReadOnlyList<ClienteAutorizadoDto>>
    {
        public Task<IReadOnlyList<ClienteAutorizadoDto>> Handle(ObtenerClientesAutorizadosQuery request, CancellationToken cancellationToken) =>
            Task.FromResult(enEsteOrden);
    }

    /// <summary>Sin usuarios que resolver: KPI BPO no es lo que este instrumento mide.</summary>
    private sealed class DirectorioUsuariosServiceVacio : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);
    }
}
