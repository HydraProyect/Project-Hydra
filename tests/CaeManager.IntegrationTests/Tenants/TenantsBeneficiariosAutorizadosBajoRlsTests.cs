using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Web.Features.Tenants;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Lote 0 del contrato del selector de Tenant beneficiario (2026-09-26): un
/// único predicado (<see cref="TenantsBeneficiariosAutorizados"/>) decide qué
/// Tenants beneficiarios lista el selector, cuáles acepta
/// <c>/cuenta/cliente-activo</c>, cuáles sobreviven a la revalidación y qué rol
/// tiene el usuario en cada vuelta del fan-out (invariantes I1, I2 e I18).
///
/// <para>
/// Escenario: un Gestor CAE del Operador CAE externo (Tenant de origen, sin
/// cartera sobre sí mismo) y, por cada rama del predicado, un Tenant beneficiario
/// que la ejerce:
/// <list type="bullet">
/// <item><b>A</b>: Asignación de Operación externa vigente del Operador de origen
/// y Asignación de Cartera vigente — el único alcanzado por Operación. <b>Sin</b>
/// delegación heredada: es el caso que antes el selector no listaba.</item>
/// <item><b>OperacionCaducada</b>, <b>OperacionFutura</b>, <b>OperacionSuspendida</b>:
/// cartera vigente bajo una operación que no lo es por una sola condición.</item>
/// <item><b>CarteraCaducada</b>, <b>CarteraFutura</b>, <b>CarteraSuspendida</b>:
/// operación vigente, cartera que no lo es por una sola condición.</item>
/// <item><b>OtroOperador</b>: operación vigente de otro Operador CAE con cartera
/// del usuario (C5).</item>
/// <item><b>RaizMalFormada</b>: operación externa marcada raíz por SQL — fila
/// imposible por dominio; aísla la condición «no raíz».</item>
/// <item><b>Heredado</b> y <b>HeredadoCaducado</b>: vía heredada de delegación,
/// vigente y caducada.</item>
/// </list>
/// Las filas «imposibles por dominio» (futura con estado Vigente, raíz externa)
/// se fuerzan por SQL a propósito: son las que distinguen cada condición de las
/// demás y hacen que su mutación ponga un test en rojo.
/// </para>
/// </summary>
public class TenantsBeneficiariosAutorizadosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _protector = new EphemeralDataProtectionProvider();
    private readonly Guid _gestor = Guid.NewGuid();
    private readonly Guid _gestorUnico = Guid.NewGuid();
    private readonly Guid _gestorConOrigen = Guid.NewGuid();
    private readonly Guid _consultaUnico = Guid.NewGuid();
    private readonly Guid _administradorSinCartera = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private readonly List<IAsyncDisposable> _desechables = [];

    private Guid _origen;
    private Guid _otroOperador;
    private Guid _a;
    private Guid _operacionA;
    private Guid _operacionCaducada;
    private Guid _operacionFutura;
    private Guid _operacionSuspendida;
    private Guid _carteraCaducada;
    private Guid _carteraFutura;
    private Guid _carteraSuspendida;
    private Guid _otroOperadorTenant;
    private Guid _operacionOtroOperador;
    private Guid _raizMalFormada;
    private Guid _operacionRaizMalFormada;
    private Guid _heredado;
    private Guid _heredadoCaducado;
    private Guid _heredadoDesactivado;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        Guid NuevoTenant(string nombre)
        {
            var tenant = new Tenant(nombre);
            _propietario.Tenants.Add(tenant);
            return tenant.Id;
        }

        _origen = NuevoTenant("Operador CAE externo de prueba");
        _otroOperador = NuevoTenant("Otro Operador CAE externo");
        _a = NuevoTenant("Tenant beneficiario A");
        _operacionCaducada = NuevoTenant("Tenant con operación caducada");
        _operacionFutura = NuevoTenant("Tenant con operación futura");
        _operacionSuspendida = NuevoTenant("Tenant con operación suspendida");
        _carteraCaducada = NuevoTenant("Tenant con cartera caducada");
        _carteraFutura = NuevoTenant("Tenant con cartera futura");
        _carteraSuspendida = NuevoTenant("Tenant con cartera suspendida");
        _otroOperadorTenant = NuevoTenant("Tenant de otro Operador");
        _raizMalFormada = NuevoTenant("Tenant con raíz mal formada");
        _heredado = NuevoTenant("Tenant por vía heredada");
        _heredadoCaducado = NuevoTenant("Tenant por vía heredada caducada");
        _heredadoDesactivado = NuevoTenant("Tenant por vía heredada desactivada");
        await _propietario.SaveChangesAsync();

        foreach (var tenant in new[] { _origen, _a })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenant);
            _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            await _propietario.SaveChangesAsync();
        }

        // Un Cliente empresarial en A: sin él, cualquier cartera ve cero clientes
        // y el KPI diría «sin cartera» con o sin rol efectivo — el test del
        // Dashboard no distinguiría nada.
        using (AmbitoTenantExplicito.Establecer(_a))
        {
            _propietario.Empresas.Add(Empresa.CrearComoCliente("Cliente empresarial de A", "B10380186", false, null, null));
            await _propietario.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;
        var ayer = ahora.AddDays(-1);

        async Task<(AsignacionOperacion Operacion, AsignacionCartera Cartera)> OperacionConCartera(
            Guid propietario, Guid operador, Guid usuario,
            DateTime operacionDesde, DateTime? operacionHasta, DateTime carteraDesde, DateTime? carteraHasta)
        {
            using var ambito = AmbitoTenantExplicito.Establecer(propietario);
            var operacion = AsignacionOperacion.Externa(
                propietario, operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, operacionDesde, operacionHasta, ahora);
            var cartera = AsignacionCartera.Externa(
                operacion, usuario, Roles.GestorCae, AmbitoAsignacion.Universal, carteraDesde, carteraHasta, ahora);
            _propietario.AsignacionesOperacion.Add(operacion);
            _propietario.AsignacionesCartera.Add(cartera);
            await _propietario.SaveChangesAsync();
            return (operacion, cartera);
        }

        _operacionA = (await OperacionConCartera(_a, _origen, _gestor, ayer, null, ayer, null)).Operacion.Id;
        await OperacionConCartera(_operacionCaducada, _origen, _gestor, ahora.AddDays(-10), ayer, ahora.AddDays(-10), null);
        // Cartera ya vigente bajo una operación que aún no ha empezado: solo la
        // condición de inicio de la operación la excluye.
        var futura = await OperacionConCartera(_operacionFutura, _origen, _gestor, ahora.AddDays(1), null, ayer, null);
        var suspendida = await OperacionConCartera(_operacionSuspendida, _origen, _gestor, ayer, null, ayer, null);
        await OperacionConCartera(_carteraCaducada, _origen, _gestor, ahora.AddDays(-10), null, ahora.AddDays(-10), ayer);
        var carteraFutura = await OperacionConCartera(_carteraFutura, _origen, _gestor, ayer, null, ahora.AddDays(1), null);
        var carteraSuspendida = await OperacionConCartera(_carteraSuspendida, _origen, _gestor, ayer, null, ayer, null);
        _operacionOtroOperador = (await OperacionConCartera(_otroOperadorTenant, _otroOperador, _gestor, ayer, null, ayer, null)).Operacion.Id;
        _operacionRaizMalFormada = (await OperacionConCartera(_raizMalFormada, _origen, _gestor, ayer, null, ayer, null)).Operacion.Id;

        suspendida.Operacion.Suspender();
        carteraSuspendida.Cartera.Suspender();
        await _propietario.SaveChangesAsync();

        // Filas imposibles por dominio, forzadas para aislar una sola condición:
        // una operación y una cartera Vigentes con inicio futuro (el dominio las
        // crea Programadas), y una operación externa marcada raíz.
        await _propietario.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AsignacionesOperacion\" SET \"Estado\" = 'Vigente' WHERE \"Id\" = {futura.Operacion.Id}");
        await _propietario.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AsignacionesCartera\" SET \"Estado\" = 'Vigente' WHERE \"Id\" = {carteraFutura.Cartera.Id}");
        await _propietario.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AsignacionesOperacion\" SET \"EsRaiz\" = TRUE WHERE \"Id\" = {_operacionRaizMalFormada}");

        // Gestor CAE con un único Tenant externo (A) y su Tenant de origen sin
        // gestionar: el caso de la decisión 5. Y otro igual pero con cartera sobre
        // su propio Tenant de origen (operación raíz), que no debe tener defecto.
        using (AmbitoTenantExplicito.Establecer(_a))
        {
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                await _propietario.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionA),
                _gestorUnico, Roles.GestorCae, AmbitoAsignacion.Universal, ayer, null, ahora));
            // Decisión 7 quater: mismo Tenant único, pero la cartera es de rol Consulta
            // (el Operador delegado): no debe tener Tenant por defecto.
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                await _propietario.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionA),
                _consultaUnico, Roles.Consulta, AmbitoAsignacion.Universal, ayer, null, ahora));
            await _propietario.SaveChangesAsync();
        }
        // Además, una operación INTERNA no raíz (propietario = operador = origen)
        // con cartera: solo «propietario distinto del origen» la deja fuera de la
        // vía de Operación.
        using (AmbitoTenantExplicito.Establecer(_origen))
        {
            var raiz = AsignacionOperacion.Raiz(_origen, ServicioCae.Outbound, ayer, ahora);
            _propietario.AsignacionesOperacion.Add(raiz);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(
                raiz, _gestorConOrigen, AmbitoAsignacion.Universal, ayer, null, ahora, rol: Roles.GestorCae));
            var clienteDelOrigen = Empresa.CrearComoCliente("Cliente empresarial del origen", "B10380194", false, null, null);
            _propietario.Empresas.Add(clienteDelOrigen);
            await _propietario.SaveChangesAsync();

            var interna = AsignacionOperacion.Interna(
                _origen, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDelOrigen.Id), ayer, null, ahora);
            _propietario.AsignacionesOperacion.Add(interna);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Interna(
                interna, _gestorConOrigen, AmbitoAsignacion.Universal, ayer, null, ahora));
            await _propietario.SaveChangesAsync();
        }
        using (AmbitoTenantExplicito.Establecer(_a))
        {
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                await _propietario.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionA),
                _gestorConOrigen, Roles.GestorCae, AmbitoAsignacion.Universal, ayer, null, ahora));
            await _propietario.SaveChangesAsync();
        }

        // Vía heredada: una delegación vigente, una ventana de soporte caducada
        // (activa, con fin pasado) y una delegación desactivada (sin fin).
        var delegacion = new DelegacionTenant(_origen, _heredado);
        var caducada = DelegacionTenant.ParaSoporte(_origen, _heredadoCaducado);
        caducada.ActivarParaSoporte("prueba", ahora.AddMinutes(-5), ahora.AddHours(-1));
        var desactivada = new DelegacionTenant(_origen, _heredadoDesactivado);
        desactivada.Desactivar();
        _propietario.DelegacionesTenant.AddRange(delegacion, caducada, desactivada);
        _propietario.AsignacionesOperadorDelegadoConRevocadas.AddRange(
            new AsignacionOperadorDelegado(delegacion.Id, _gestor, Roles.GestorCae),
            new AsignacionOperadorDelegado(caducada.Id, _gestor, Roles.GestorCae),
            new AsignacionOperadorDelegado(desactivada.Id, _gestor, Roles.GestorCae),
            // Decisión 7 bis: un Administrador del Operador CAE externo SIN Asignación
            // de Cartera y con un único Tenant externo (por la vía heredada).
            new AsignacionOperadorDelegado(delegacion.Id, _administradorSinCartera, Roles.Administrador));
        await _propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var desechable in _desechables)
            await desechable.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ---------- Capa Application: el predicado, sin RLS ----------

    [Fact]
    public async Task Por_Operacion_solo_alcanza_el_Tenant_con_operacion_y_cartera_vigentes_del_Operador_de_origen()
    {
        var alcanzados = await TenantsBeneficiariosAutorizados
            .CarterasPorOperacion(_propietario, _gestor, _origen, DateTime.UtcNow)
            .Select(v => v.Operacion.PropietarioTenantId)
            .ToListAsync();

        alcanzados.Should().BeEquivalentTo([_a],
            "cada otro Tenant falla exactamente una condición del predicado (operación caducada, futura o " +
            "suspendida; cartera caducada, futura o suspendida; otro Operador CAE; raíz)");
    }

    [Fact]
    public async Task Por_Operacion_nunca_devuelve_el_propio_Tenant_de_origen()
    {
        var alcanzados = await TenantsBeneficiariosAutorizados
            .CarterasPorOperacion(_propietario, _gestorConOrigen, _origen, DateTime.UtcNow)
            .Select(v => v.Operacion.PropietarioTenantId)
            .ToListAsync();

        alcanzados.Should().BeEquivalentTo([_a],
            "las carteras sobre la raíz y sobre una operación interna del origen lo gestionan, pero el origen no se " +
            "alcanza «por Operación»");
        (await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(_propietario, _gestorConOrigen, _origen, DateTime.UtcNow, default))
            .Should().BeTrue();
        (await TenantsBeneficiariosAutorizados.OrigenGestionadoAsync(_propietario, _gestor, _origen, DateTime.UtcNow, default))
            .Should().BeFalse();
    }

    [Fact]
    public async Task La_via_heredada_solo_alcanza_la_delegacion_vigente()
    {
        var alcanzados = await TenantsBeneficiariosAutorizados
            .AsignacionesHeredadasVigentes(_propietario, _gestor, DateTime.UtcNow)
            .Select(v => v.Concesion.TenantClienteId)
            .ToListAsync();

        alcanzados.Should().BeEquivalentTo([_heredado],
            "ni la ventana de soporte caducada ni la delegación desactivada conceden nada");
    }

    // ---------- La lista del selector, bajo RLS ----------

    [Fact]
    public async Task La_lista_incluye_el_Tenant_alcanzado_por_Operacion_y_conserva_la_via_heredada()
    {
        var lista = await ListaAsync(_gestor);

        lista.Should().BeEquivalentTo(new[]
        {
            new ClienteAutorizadoDto(_origen, "Operador CAE externo de prueba", EsOrigen: true, EsGestionadoPorOperacion: false),
            new ClienteAutorizadoDto(_a, "Tenant beneficiario A", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true),
            new ClienteAutorizadoDto(_heredado, "Tenant por vía heredada", EsOrigen: false, EsGestionadoPorOperacion: false),
        });
    }

    [Fact]
    public async Task Con_cartera_sobre_su_Tenant_de_origen_el_origen_consta_como_gestionado()
    {
        var lista = await ListaAsync(_gestorConOrigen);

        lista.Single(c => c.EsOrigen).EsGestionadoPorOperacion.Should().BeTrue();
        lista.Where(c => !c.EsOrigen).Select(c => c.TenantId).Should().BeEquivalentTo([_a]);
    }

    // ---------- /cuenta/cliente-activo, bajo RLS ----------

    [Fact]
    public async Task El_POST_acepta_el_Tenant_alcanzado_por_Operacion_y_embebe_su_operacion()
    {
        var (resultado, httpContext) = await CambiarAsync(_gestor, _a);

        resultado.Should().BeOfType<RedirectHttpResult>();
        var (tenant, operacion, _) = ClienteActivoSeleccionado.LeerCargaUtil(
            _protector, CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie), _gestor);
        tenant.Should().Be(_a);
        operacion.Should().Be(_operacionA);
        RecientesSelectorTenant.Interpretar(Uri.UnescapeDataString(CookieEmitida(httpContext, RecientesSelectorTenant.NombreCookie)!), _gestor)
            .Should().Equal([_a]);
    }

    [Fact]
    public async Task El_POST_aceptado_invalida_los_pendientes_del_usuario_y_el_rechazado_no()
    {
        var contador = new ContadorPendientesSelectorTenant(TimeProvider.System);
        var alcance = new[] { new ClienteAutorizadoDto(_a, "A", false, true, EsCarteraGestorCae: true) };
        var calculos = 0;
        Task<CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado.MiTrabajoAgregadoDto> Calcular(CancellationToken _)
        {
            calculos++;
            return Task.FromResult(new CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado.MiTrabajoAgregadoDto([]));
        }

        await contador.ObtenerAsync(_gestor, _origen, alcance, Calcular, CancellationToken.None);
        await CambiarAsync(_gestor, _operacionCaducada, contador);
        await contador.ObtenerAsync(_gestor, _origen, alcance, Calcular, CancellationToken.None);
        calculos.Should().Be(1, "un cambio rechazado no toca la caché");

        await CambiarAsync(_gestor, _a, contador);
        await contador.ObtenerAsync(_gestor, _origen, alcance, Calcular, CancellationToken.None);
        calculos.Should().Be(2, "un cambio autorizado descarta lo calculado para ese usuario");
    }

    [Fact]
    public async Task El_POST_acepta_la_via_heredada_sin_operacion()
    {
        var (resultado, httpContext) = await CambiarAsync(_gestor, _heredado);

        resultado.Should().BeOfType<RedirectHttpResult>();
        var (tenant, operacion, _) = ClienteActivoSeleccionado.LeerCargaUtil(
            _protector, CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie), _gestor);
        tenant.Should().Be(_heredado);
        operacion.Should().BeNull();
    }

    [Fact]
    public async Task El_POST_rechaza_cada_Tenant_que_el_predicado_excluye()
    {
        foreach (var excluido in new[]
                 {
                     _operacionCaducada, _operacionFutura, _operacionSuspendida, _carteraCaducada, _carteraFutura,
                     _carteraSuspendida, _otroOperadorTenant, _raizMalFormada, _heredadoCaducado, _heredadoDesactivado,
                 })
        {
            var (resultado, httpContext) = await CambiarAsync(_gestor, excluido);

            resultado.Should().BeOfType<ForbidHttpResult>($"el Tenant {excluido} no está en el conjunto autorizado (I1)");
            CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie).Should().BeNull();
            CookieEmitida(httpContext, RecientesSelectorTenant.NombreCookie).Should().BeNull("un Tenant rechazado nunca es reciente");
        }
    }

    [Fact]
    public async Task El_POST_al_Tenant_de_origen_borra_la_seleccion_y_recuerda_que_fue_a_proposito()
    {
        var (resultado, httpContext) = await CambiarAsync(_gestorUnico, _origen);

        resultado.Should().BeOfType<RedirectHttpResult>();
        httpContext.Response.Headers.SetCookie.Should().Contain(c =>
            c!.StartsWith(ClienteActivoSeleccionado.NombreCookie + "=;"), "volver al origen borra la selección");
        CookieEmitida(httpContext, CookieDeContextoTenant.NombreOrigenElegido).Should().Be(_gestorUnico.ToString("N"));
    }

    // ---------- Revalidación (middleware y circuito), bajo RLS ----------

    [Fact]
    public async Task La_revalidacion_mantiene_la_seleccion_por_Operacion_vigente()
    {
        var seleccion = await RevalidarAsync(_gestor, _a, _operacionA);

        seleccion.TenantIdSeleccionado.Should().Be(_a);
    }

    [Fact]
    public async Task La_revalidacion_descarta_una_operacion_de_otro_Operador_aunque_RLS_la_deje_ver()
    {
        // C5: con el Tenant seleccionado como app.tenant_id, RLS deja leer la
        // operación (PropietarioTenantId = app.tenant_id). Tiene que ser el
        // predicado quien la rechace.
        await using (var runtime = CrearRuntime(_gestor, tenantDeLaPeticion: _otroOperadorTenant))
            (await runtime.AsignacionesOperacion.CountAsync(o => o.Id == _operacionOtroOperador))
                .Should().Be(1, "control del instrumento: RLS no la oculta");

        var seleccion = await RevalidarAsync(_gestor, _otroOperadorTenant, _operacionOtroOperador);

        seleccion.TenantIdSeleccionado.Should().BeNull("una operación de otro Operador CAE no autoriza (C5)");
    }

    [Fact]
    public async Task La_revalidacion_descarta_una_operacion_raiz()
    {
        var seleccion = await RevalidarAsync(_gestor, _raizMalFormada, _operacionRaizMalFormada);

        seleccion.TenantIdSeleccionado.Should().BeNull("la raíz no es un contexto seleccionable (C5)");
    }

    [Fact]
    public async Task La_revalidacion_descarta_la_seleccion_cuando_la_operacion_caduca()
    {
        (await RevalidarAsync(_gestor, _a, _operacionA)).TenantIdSeleccionado.Should().Be(_a);

        var operacion = await _propietario.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionA);
        operacion.Cerrar(MotivoCierreAsignacion.Revocada, DateTime.UtcNow);
        await _propietario.SaveChangesAsync();

        (await RevalidarAsync(_gestor, _a, _operacionA)).TenantIdSeleccionado.Should().BeNull();
        (await ListaAsync(_gestor)).Select(c => c.TenantId).Should().NotContain(_a,
            "la lista y la revalidación usan el mismo predicado (I2)");
    }

    [Fact]
    public async Task La_revalidacion_descarta_un_token_cuyo_Tenant_no_es_el_de_su_operacion()
    {
        // Control del instrumento: con el Tenant del token como app.tenant_id,
        // RLS sigue dejando ver la operación de A por la posición del Operador
        // CAE; el rechazo tiene que venir del predicado.
        await using (var runtime = CrearRuntime(_gestor, tenantDeLaPeticion: _heredado))
            (await runtime.AsignacionesOperacion.CountAsync(o => o.Id == _operacionA)).Should().Be(1);

        var seleccion = await RevalidarAsync(_gestor, _heredado, _operacionA);

        seleccion.TenantIdSeleccionado.Should().BeNull("la operación del token pertenece a otro Tenant");
    }

    // ---------- Dashboard «Todos» (decisión 6) y Tenant por defecto (decisión 5) ----------

    [Fact]
    public async Task El_agregado_de_Dashboard_incluye_el_Tenant_alcanzado_por_Operacion_con_su_cartera()
    {
        await using var servicios = ServiciosDeAplicacion(_gestorUnico);

        var kpis = await servicios.GetRequiredService<IMediator>().Send(new ObtenerKpisGlobalesQuery());

        kpis.TotalClientes.Should().Be(2, "origen + A, alcanzado solo por Operación");
        kpis.ClientesConMasRiesgo.Should().Contain(c => c.TenantId == _a && !c.SinCarteraAsignada,
            "en la vuelta de A el rol efectivo sale de su Asignación de Cartera: no es alcance cero");
    }

    [Fact]
    public async Task En_el_fan_out_el_rol_de_un_Tenant_alcanzado_solo_por_Operacion_es_el_de_su_cartera()
    {
        await using var runtime = CrearRuntime(_gestorUnico, tenantDeLaPeticion: _origen);
        var usuario = CrearCurrentUserService(runtime, _gestorUnico);

        using (AmbitoTenantExplicito.Establecer(_a))
            (await usuario.ObtenerRolEfectivoAsync()).Should().Be(Roles.GestorCae,
                "el mismo rol que tendría seleccionando A por /cuenta/cliente-activo");
        using (AmbitoTenantExplicito.Establecer(_otroOperadorTenant))
            (await usuario.ObtenerRolEfectivoAsync()).Should().BeNull("ese Tenant no está en su conjunto autorizado");
    }

    [Fact]
    public async Task Con_un_unico_Tenant_externo_y_el_origen_sin_gestionar_ese_Tenant_es_el_activo_por_defecto()
    {
        ClientesAutorizados.TenantPorDefecto(await ListaAsync(_gestorUnico))!.TenantId.Should().Be(_a);
        ClientesAutorizados.TenantPorDefecto(await ListaAsync(_gestorConOrigen))
            .Should().BeNull("su Tenant de origen está gestionado");
        ClientesAutorizados.TenantPorDefecto(await ListaAsync(_gestor))
            .Should().BeNull("alcanza además un Tenant por la vía heredada");
    }

    [Fact]
    public async Task Decision_7_quater_con_cartera_de_rol_Consulta_no_hay_Tenant_por_defecto_ni_middleware()
    {
        var lista = await ListaAsync(_consultaUnico);

        lista.Single(c => !c.EsOrigen).Should().Match<ClienteAutorizadoDto>(c =>
            c.TenantId == _a && c.EsGestionadoPorOperacion && !c.EsCarteraGestorCae,
            "el rol sale de la Asignación de Cartera vigente");
        ClientesAutorizados.TenantPorDefecto(lista).Should().BeNull("la cartera no es de rol Gestor CAE");

        var httpContext = PeticionDePagina(_consultaUnico, "/trabajadores", "");
        (await FijarPorDefectoAsync(httpContext, _consultaUnico)).Should().BeNull();
        CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie).Should().BeNull(
            "entra en su Tenant de origen");
    }

    [Fact]
    public async Task Sin_Asignacion_de_Cartera_un_unico_Tenant_externo_no_es_el_activo_por_defecto_decision_7_bis()
    {
        var lista = await ListaAsync(_administradorSinCartera);
        lista.Should().BeEquivalentTo(
        [
            new ClienteAutorizadoDto(_origen, "Operador CAE externo de prueba", EsOrigen: true, EsGestionadoPorOperacion: false),
            new ClienteAutorizadoDto(_heredado, "Tenant por vía heredada", EsOrigen: false, EsGestionadoPorOperacion: false),
        ], "alcanza un único Tenant externo, pero no por Asignación de Cartera");

        ClientesAutorizados.TenantPorDefecto(lista).Should().BeNull(
            "el defecto de la decisión 5 aplica solo a quien alcanza el Tenant por cartera vigente");

        var httpContext = PeticionDePagina(_administradorSinCartera, "/trabajadores", "");
        (await FijarPorDefectoAsync(httpContext, _administradorSinCartera)).Should().BeNull(
            "el Administrador sin cartera sigue entrando en su Tenant de origen");
        CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie).Should().BeNull();
    }

    [Fact]
    public async Task Decision_7_quater_cartera_Consulta_el_rol_efectivo_es_Consulta_sin_defecto()
    {
        // El rol efectivo (fan-out) sale de la misma función que la marca de la lista.
        var lista = await ListaAsync(_consultaUnico);
        lista.Single(c => !c.EsOrigen).EsCarteraGestorCae.Should().BeFalse("manda la cartera del usuario (Consulta)");
        ClientesAutorizados.TenantPorDefecto(lista).Should().BeNull();
        ClientesAutorizados.SelectorVisible(lista, ClientesAutorizados.Activo(lista, _a)).Should().BeTrue(
            "dentro del Tenant externo el selector es su control para volver");

        await using var runtime = CrearRuntime(_consultaUnico, tenantDeLaPeticion: _origen);
        var usuario = CrearCurrentUserService(runtime, _consultaUnico);
        using (AmbitoTenantExplicito.Establecer(_a))
            (await usuario.ObtenerRolEfectivoAsync()).Should().Be(Roles.Consulta);

        var httpContext = PeticionDePagina(_consultaUnico, "/trabajadores", "");
        (await FijarPorDefectoAsync(httpContext, _consultaUnico)).Should().BeNull();
    }

    [Fact]
    public async Task El_middleware_fija_el_Tenant_por_defecto_con_la_misma_cookie_que_el_POST_y_redirige()
    {
        var httpContext = PeticionDePagina(_gestorUnico, "/trabajadores", "?pagina=2");

        var destino = await FijarPorDefectoAsync(httpContext, _gestorUnico);

        destino.Should().Be("/trabajadores?pagina=2");
        var (tenant, operacion, _) = ClienteActivoSeleccionado.LeerCargaUtil(
            _protector, CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie), _gestorUnico);
        tenant.Should().Be(_a);
        operacion.Should().Be(_operacionA, "se revalida por la operación, igual que una selección explícita");
    }

    [Fact]
    public async Task Sin_Tenant_por_defecto_el_middleware_lo_recuerda_para_no_repetir_la_consulta()
    {
        var httpContext = PeticionDePagina(_gestor, "/", "");

        (await FijarPorDefectoAsync(httpContext, _gestor)).Should().BeNull();
        CookieEmitida(httpContext, CookieDeContextoTenant.NombreDefectoEvaluado).Should().Be(_gestor.ToString("N"));
        httpContext.Response.Headers.SetCookie.Should().Contain(c =>
            c!.StartsWith(CookieDeContextoTenant.NombreDefectoEvaluado + "=") && c.Contains("path=/;"),
            "la marca vale para toda la aplicación, no solo para el directorio de la primera página");
        CookieEmitida(httpContext, ClienteActivoSeleccionado.NombreCookie).Should().BeNull();

        var siguiente = PeticionDePagina(_gestor, "/", "");
        siguiente.Request.Headers.Cookie = $"{CookieDeContextoTenant.NombreDefectoEvaluado}={_gestor:N}";
        (await FijarPorDefectoAsync(siguiente, _gestor)).Should().BeNull();
        siguiente.Response.Headers.SetCookie.Should().BeEmpty("con la marca no se vuelve a evaluar");
    }

    [Fact]
    public async Task En_el_fan_out_una_operacion_que_autoriza_manda_aunque_su_rol_no_sea_delegable_y_haya_via_heredada()
    {
        // D1 de la revisión puente: seleccionado, el POST embebe la operación y el
        // rol sale solo de ella (null si no es delegable). El fan-out no puede
        // caer a la vía heredada y dar más.
        var ahora = DateTime.UtcNow;
        var usuario = Guid.NewGuid();
        using (AmbitoTenantExplicito.Establecer(_a))
        {
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                await _propietario.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionA),
                usuario, Roles.Administrador, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
            await _propietario.SaveChangesAsync();
        }
        var delegacion = new DelegacionTenant(_origen, _a);
        _propietario.DelegacionesTenant.Add(delegacion);
        _propietario.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, usuario, Roles.GestorCae));
        await _propietario.SaveChangesAsync();

        await using var runtime = CrearRuntime(usuario, tenantDeLaPeticion: _origen);
        var servicio = CrearCurrentUserService(runtime, usuario);
        using (AmbitoTenantExplicito.Establecer(_a))
            (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull();

        // Control: la vía heredada sola sí da GestorCae (sin cartera, otro usuario).
        var soloHeredado = Guid.NewGuid();
        _propietario.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, soloHeredado, Roles.GestorCae));
        await _propietario.SaveChangesAsync();
        await using var runtimeHeredado = CrearRuntime(soloHeredado, tenantDeLaPeticion: _origen);
        using (AmbitoTenantExplicito.Establecer(_a))
            (await CrearCurrentUserService(runtimeHeredado, soloHeredado).ObtenerRolEfectivoAsync())
                .Should().Be(Roles.GestorCae);
    }

    [Fact]
    public async Task El_middleware_no_fija_nada_si_el_usuario_volvio_a_su_origen_a_proposito()
    {
        var httpContext = PeticionDePagina(_gestorUnico, "/", "");
        httpContext.Request.Headers.Cookie = $"{CookieDeContextoTenant.NombreOrigenElegido}={_gestorUnico:N}";

        (await FijarPorDefectoAsync(httpContext, _gestorUnico)).Should().BeNull();
        httpContext.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public void El_middleware_solo_se_evalua_en_una_navegacion_de_pagina_sin_seleccion()
    {
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(PeticionDePagina(_gestorUnico, "/", "")).Should().BeTrue();

        var conSeleccion = PeticionDePagina(_gestorUnico, "/", "");
        conSeleccion.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}=x";
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(conSeleccion).Should().BeFalse();

        var post = PeticionDePagina(_gestorUnico, "/", "");
        post.Request.Method = HttpMethods.Post;
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(post).Should().BeFalse();

        var blazor = PeticionDePagina(_gestorUnico, "/_blazor/negotiate", "");
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(blazor).Should().BeFalse();

        var cuenta = PeticionDePagina(_gestorUnico, "/cuenta/iniciar-sesion", "");
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(cuenta).Should().BeFalse();

        var anonimo = new DefaultHttpContext();
        anonimo.Request.Headers.Accept = "text/html";
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(anonimo).Should().BeFalse();
    }

    // ---------- Arnés ----------

    private async Task<IReadOnlyList<ClienteAutorizadoDto>> ListaAsync(Guid usuario)
    {
        await using var runtime = CrearRuntime(usuario, tenantDeLaPeticion: _origen);
        return await new ObtenerClientesAutorizadosQueryHandler(
                runtime, runtime, new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen))
            .Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
    }

    private async Task<(IResult, DefaultHttpContext)> CambiarAsync(Guid usuario, Guid tenantId, IContadorPendientesSelectorTenant? contador = null)
    {
        await using var runtime = CrearRuntime(usuario, tenantDeLaPeticion: _origen);
        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(usuario) };
        var resultado = await ClienteActivoEndpoints.CambiarAsync(
            tenantId, "/", httpContext, runtime, new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen),
            runtime, _protector, CancellationToken.None, contador);
        return (resultado, httpContext);
    }

    /// <summary>
    /// Ejecuta el middleware real con un token emitido por la clase de
    /// producción; la conexión va con el Tenant seleccionado como
    /// <c>app.tenant_id</c>, como en la petición real.
    /// </summary>
    private async Task<ClienteActivoSeleccionado> RevalidarAsync(Guid usuario, Guid tenant, Guid asignacionOperacionId)
    {
        var httpContext = PeticionDePagina(usuario, "/", "");
        httpContext.Request.Headers.Cookie =
            $"{ClienteActivoSeleccionado.NombreCookie}={ClienteActivoSeleccionado.Proteger(_protector, usuario, tenant, asignacionOperacionId)}";
        var seleccion = new ClienteActivoSeleccionado(new HttpContextAccessorFalso(httpContext), _protector);

        await using var runtime = CrearRuntime(usuario, tenantDeLaPeticion: tenant);
        await new RevalidacionClienteActivoMiddleware(_ => Task.CompletedTask).InvokeAsync(
            httpContext, seleccion, new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen),
            runtime, runtime, new SesionPrivilegiadaAusente(),
            NullLogger<RevalidacionClienteActivoMiddleware>.Instance);
        return seleccion;
    }

    private async Task<string?> FijarPorDefectoAsync(DefaultHttpContext httpContext, Guid usuario)
    {
        await using var servicios = ServiciosDeAplicacion(usuario);
        return await TenantBeneficiarioPorDefectoMiddleware.FijarTenantPorDefectoAsync(
            httpContext, servicios.GetRequiredService<IMediator>(),
            new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen),
            servicios.GetRequiredService<IOperacionesQueryContext>(), _protector);
    }

    private DefaultHttpContext PeticionDePagina(Guid usuario, string ruta, string query)
    {
        var httpContext = new DefaultHttpContext { User = UsuarioAutenticado(usuario) };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Path = ruta;
        httpContext.Request.QueryString = new QueryString(string.IsNullOrEmpty(query) ? null : query);
        httpContext.Request.Headers.Accept = "text/html,application/xhtml+xml";
        return httpContext;
    }

    /// <summary>
    /// Contenedor de aplicación con el <see cref="CurrentUserService"/> y el
    /// <see cref="AlcanceDatosService"/> de producción sobre una conexión
    /// <c>cae_app_runtime</c> bajo RLS: lo que tiene que demostrarse es que el
    /// rol efectivo de cada vuelta del fan-out sale de la cartera real.
    /// </summary>
    private ServiceProvider ServiciosDeAplicacion(Guid usuario)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_origen);
        var runtime = CrearRuntime(usuario, tenantDeLaPeticion);
        _desechables.Add(runtime);

        var usuarioReal = CrearCurrentUserService(runtime, usuario);
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(runtime);
        servicios.AddSingleton<ITenantsQueryContext>(runtime);
        servicios.AddSingleton<IOperacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(runtime);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(runtime);
        servicios.AddSingleton<ICurrentUserService>(usuarioReal);
        servicios.AddSingleton<IAlcanceDatosService>(
            new AlcanceDatosService(runtime, usuarioReal, tenantDeLaPeticion, new SesionPrivilegiadaAusente()));
        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearRuntime(Guid usuario, Guid tenantDeLaPeticion) =>
        CrearRuntime(usuario, new TenantActualDeLaPeticion(tenantDeLaPeticion));

    private CaeManagerDbContext CrearRuntime(Guid usuario, ITenantActual tenantDeLaPeticion)
    {
        var usuarioInterceptor = new CurrentUserServiceFalso(usuario, tenantOrigenId: _origen);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(
                    tenantDeLaPeticion, new SinClienteActivo(), usuarioInterceptor, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    private CurrentUserService CrearCurrentUserService(CaeManagerDbContext contexto, Guid usuario)
    {
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, usuario.ToString()),
                new Claim(ClaimTypes.Role, Roles.GestorCae),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _origen.ToString())
            ],
            "prueba");

        var servicios = new ServiceCollection();
        servicios.AddSingleton<ITenantsQueryContext>(contexto);
        servicios.AddSingleton<IOperacionesQueryContext>(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorNulo(),
            new SinClienteActivo(),
            servicios.AddTechoDeRolSinEncargoSembrado().BuildServiceProvider());
    }

    private static string? CookieEmitida(HttpContext contexto, string nombre)
    {
        var cabecera = contexto.Response.Headers.SetCookie
            .FirstOrDefault(c => c is not null && c.StartsWith(nombre + "=") && !c.StartsWith(nombre + "=;"));
        return cabecera?.Split(';')[0][(nombre.Length + 1)..];
    }

    private static ClaimsPrincipal UsuarioAutenticado(Guid usuarioId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "prueba"));

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class HttpContextAccessorNulo : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }

    private sealed class HttpContextAccessorFalso(HttpContext contexto) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => contexto;
            set => throw new NotSupportedException();
        }
    }
}
