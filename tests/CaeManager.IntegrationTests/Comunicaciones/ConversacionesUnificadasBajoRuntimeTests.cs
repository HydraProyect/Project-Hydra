using System.Data.Common;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// La vista unificada de Comunicaciones entre Tenants (D-6 b, R-3; diseño en
/// Project-Hydra-Negocio/tecnico/DISENO-D6B-COMUNICACIONES-MULTITENANT-2026-11-17.md, § 4.2, § 4.3 y § 7.2), con la
/// lectura autenticada como <c>cae_app_runtime</c>, el contexto RLS firmado y el <see cref="CurrentUserService"/> y el
/// <see cref="AlcanceDatosService"/> de producción, reutilizados en cada vuelta del fan-out.
///
/// <para>
/// Escenario: el usuario es Gestor CAE en el Tenant de origen de un Operador CAE externo (claim de sesión).
/// <list type="bullet">
/// <item><b>Dentro</b>: Asignación de Operación Outbound vigente y Asignación de Cartera vigente. Una conversación de un
/// Cliente empresarial y otra del buzón personal de otro Gestor CAE.</item>
/// <item><b>Caducada</b>: Asignación de Operación vigente y Asignación de Cartera ya vencida por reloj, con la delegación
/// heredada todavía presente (fila de <c>AsignacionOperadorDelegado</c>). La vista no usa la heredada (DN-2 (a)).</item>
/// <item><b>Fuera</b>: ningún vínculo con el Operador CAE.</item>
/// </list>
/// </para>
/// </summary>
public class ConversacionesUnificadasBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly ContadorLecturas _contador = new();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private CurrentUserService _usuarioReal = null!;
    private Guid _tenantOrigen;
    private Guid _tenantDentro;
    private Guid _tenantCaducada;
    private Guid _tenantFuera;
    private Guid _clienteDentro;
    private Guid _conversacionDentro;
    private Guid _conversacionBuzonAjeno;
    private Guid _conversacionCaducada;
    private Guid _conversacionFuera;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var origen = new Tenant("Operador CAE externo de prueba");
        var dentro = new Tenant("Tenant beneficiario dentro");
        var caducada = new Tenant("Tenant beneficiario con cartera vencida");
        var fuera = new Tenant("Tenant ajeno al Operador CAE");
        _propietario.Tenants.AddRange(origen, dentro, caducada, fuera);
        _tenantOrigen = origen.Id;
        _tenantDentro = dentro.Id;
        _tenantCaducada = caducada.Id;
        _tenantFuera = fuera.Id;
        var delegacionDentro = new DelegacionTenant(_tenantOrigen, _tenantDentro);
        var delegacionCaducada = new DelegacionTenant(_tenantOrigen, _tenantCaducada);
        _propietario.DelegacionesTenant.AddRange(delegacionDentro, delegacionCaducada);
        _propietario.AsignacionesOperadorDelegadoConRevocadas.AddRange(
            new AsignacionOperadorDelegado(delegacionDentro.Id, _usuario, Roles.GestorCae),
            new AsignacionOperadorDelegado(delegacionCaducada.Id, _usuario, Roles.GestorCae));
        await _propietario.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            var cliente = Empresa.CrearComoCliente("Cervezas Duff dentro", "B10380186", false, null, null);
            _propietario.Empresas.Add(cliente);
            var buzonAjeno = new ConexionIntegracion(
                "gestor.otro@ejemplo.local", "Buzón personal ajeno", gestorPropietarioId: Guid.NewGuid());
            _propietario.ConexionesIntegracion.Add(buzonAjeno);
            await _propietario.SaveChangesAsync();
            _clienteDentro = cliente.Id;

            var operacion = AsignacionOperacion.Externa(
                _tenantDentro, _tenantOrigen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora);
            OperacionExternaAmparada.Agregar(_propietario, operacion, delegacionDentro);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _usuario, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));

            _conversacionDentro = AgregarConversacion("Consulta del Cliente empresarial", _clienteDentro, null);
            _conversacionBuzonAjeno = AgregarConversacion("Hilo del buzón personal ajeno", _clienteDentro, buzonAjeno.Id);
            await _propietario.SaveChangesAsync();
        }

        using (AmbitoTenantExplicito.Establecer(_tenantCaducada))
        {
            var cliente = Empresa.CrearComoCliente("Cervezas Duff vencida", "B10380194", false, null, null);
            _propietario.Empresas.Add(cliente);
            await _propietario.SaveChangesAsync();

            var hace10Dias = ahora.AddDays(-10);
            var operacion = AsignacionOperacion.Externa(
                _tenantCaducada, _tenantOrigen, ServicioCae.Outbound, AmbitoAsignacion.Universal, hace10Dias, null, hace10Dias);
            OperacionExternaAmparada.Agregar(_propietario, operacion, delegacionCaducada);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _usuario, Roles.GestorCae, AmbitoAsignacion.Universal, hace10Dias, ahora.AddDays(-1), hace10Dias));

            _conversacionCaducada = AgregarConversacion("Consulta con cartera vencida", cliente.Id, null);
            await _propietario.SaveChangesAsync();
        }

        using (AmbitoTenantExplicito.Establecer(_tenantFuera))
        {
            _conversacionFuera = AgregarConversacion("Consulta de un Tenant ajeno", null, null);
            await _propietario.SaveChangesAsync();
        }

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        var usuarioInterceptor = new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenantOrigen);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuarioInterceptor, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                _contador)
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
        _usuarioReal = CrearCurrentUserService(_runtime);

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddLogging();
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(_runtime);
        servicios.AddSingleton<ITenantsQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IIntegracionesQueryContext>(_runtime);
        servicios.AddSingleton<ICurrentUserService>(_usuarioReal);
        // Una sola instancia para todo el fan-out, como el ámbito scoped real: la memoización entre vueltas es lo
        // que tiene que resolver bien.
        servicios.AddSingleton<IAlcanceDatosService>(
            new AlcanceDatosService(_runtime, _usuarioReal, tenantDeLaPeticion, new SesionPrivilegiadaAusente()));
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la lectura va de verdad bajo RLS.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.Conversaciones.IgnoreQueryFilters().CountAsync(c => c.Id == _conversacionDentro)).Should().Be(0,
            "fuera de un ámbito, app.tenant_id es el Tenant de origen y RLS oculta la conversación");

        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
            (await _runtime.Conversaciones.IgnoreQueryFilters().CountAsync(c => c.Id == _conversacionDentro)).Should().Be(1);
    }

    [Fact]
    public async Task El_Tenant_dentro_del_alcance_se_ve_con_el_TenantId_de_la_fila_y_sin_el_buzon_personal_ajeno()
    {
        var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery());

        var fila = pagina.Filas.Should().ContainSingle(f => f.Id == _conversacionDentro).Subject;
        fila.TenantId.Should().Be(_tenantDentro);
        fila.TenantNombre.Should().Be("Tenant beneficiario dentro");
        fila.ClienteEmpresarialId.Should().Be(_clienteDentro);
        pagina.Filas.Should().NotContain(f => f.Id == _conversacionBuzonAjeno,
            "el hilo de un buzón personal de otro Gestor CAE no sale ni en la vista unificada");
        pagina.NoConsultados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_Tenant_fuera_del_alcance_no_se_ve_ni_pidiendolo_por_su_TenantId()
    {
        var todas = await EnviarAsync(new ObtenerConversacionesUnificadasQuery());
        todas.Filas.Should().NotContain(f => f.Id == _conversacionFuera || f.TenantId == _tenantFuera);

        var pedida = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(TenantIds: [_tenantFuera]));
        pedida.Filas.Should().BeEmpty("la coordenada se interseca con el conjunto calculado en el servidor");
        pedida.NoConsultados.Should().BeEmpty("un Tenant de fuera no se distingue de uno inexistente");
    }

    [Fact]
    public async Task La_cartera_vencida_saca_al_Tenant_aunque_quede_la_delegacion_heredada()
    {
        var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(TenantIds: [_tenantCaducada]));

        pagina.Filas.Should().BeEmpty("sin Asignación de Cartera vigente el Gestor CAE no entra, y la vista no cae a la heredada");
        pagina.NoConsultados.Should().BeEmpty();
        (await EnviarAsync(new ObtenerConversacionesUnificadasQuery())).Filas
            .Should().NotContain(f => f.Id == _conversacionCaducada || f.TenantId == _tenantCaducada);
    }

    [Fact]
    public async Task Pagina_por_cursor_sin_repetir_ni_saltarse_filas()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            AgregarConversacion("Segunda consulta del Cliente empresarial", _clienteDentro, null);
            await _propietario.SaveChangesAsync();
        }

        var primera = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(TamanoPagina: 1));
        primera.Filas.Should().HaveCount(1);
        primera.Siguiente.Should().NotBeNull();

        var segunda = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(Cursor: primera.Siguiente, TamanoPagina: 1));
        segunda.Filas.Should().HaveCount(1);
        segunda.Filas[0].Id.Should().NotBe(primera.Filas[0].Id);
        segunda.Siguiente.Should().BeNull("solo hay dos conversaciones visibles");
    }

    /// <summary>
    /// A igualdad de fecha decide el <c>Id</c> (§ 4.3, M4): el cursor tiene que entrar por la rama
    /// <c>c.Id.CompareTo(id) &lt; 0</c>, que Npgsql traduce a una comparación de <c>uuid</c>, y la fusión en memoria tiene
    /// que ordenar igual que PostgreSQL. Con las tres conversaciones visibles en la misma fecha, página a página de una
    /// fila, el recorrido sale entero, sin repetir, y en el orden de <see cref="OrdenConversacionesUnificadas"/>.
    /// </summary>
    [Fact]
    public async Task A_igual_fecha_el_cursor_desempata_por_Id_como_uuid()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            AgregarConversacion("Empate A", _clienteDentro, null);
            AgregarConversacion("Empate B", _clienteDentro, null);
            await _propietario.SaveChangesAsync();
        }

        var fecha = OrdenConversacionesUnificadas.TruncarAMicrosegundos(DateTime.UtcNow.AddMinutes(-1));
        await _propietario.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Conversaciones\" SET \"FechaUltimoMensajeUtc\" = {fecha} WHERE \"TenantId\" = {_tenantDentro}");

        var recorridas = new List<ConversacionUnificadaDto>();
        CursorConversacionesUnificadas? cursor = null;
        for (var vuelta = 0; vuelta < 10; vuelta++)
        {
            var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(Cursor: cursor, TamanoPagina: 1));
            pagina.NoConsultados.Should().BeEmpty();
            recorridas.AddRange(pagina.Filas);
            cursor = pagina.Siguiente;
            if (cursor is null) break;
        }

        cursor.Should().BeNull("el recorrido termina");
        recorridas.Should().HaveCount(3, "las tres conversaciones visibles del Tenant, sin la del buzón personal ajeno");
        recorridas.Should().OnlyContain(f => f.FechaUltimoMensajeUtc == fecha, "control: el empate de fecha es real");
        recorridas.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        recorridas.Select(f => f.Id).Should().Equal(
            recorridas.Select(f => f.Id).OrderByDescending(id => id, Comparer<Guid>.Create(OrdenConversacionesUnificadas.CompararComoUuid)),
            "PostgreSQL y la fusión en memoria ordenan el uuid igual");
    }

    /// <summary>
    /// La fusión entre Tenants (§ 4.3, M4; A-13): dos Tenants beneficiarios con fechas intercaladas y un empate de fecha
    /// entre ellos. Página a página de una fila, el recorrido sale entero, sin repetir ni saltarse filas y en el orden
    /// global, con el <c>TenantId</c> de cada fila. Sin la ordenación en memoria de la fusión, el cursor tomaría una fila
    /// que no es la última del orden global y el recorrido saltaría filas del otro Tenant.
    /// </summary>
    [Fact]
    public async Task La_fusion_entre_dos_Tenants_intercala_por_fecha_y_desempata_por_Id()
    {
        var (otro, clienteOtro) = await CrearTenantDentroAsync("Segundo Tenant beneficiario dentro", "B10380202");
        Guid dentroCuarta, dentroEmpate, otroSegunda, otroTercera, otroEmpate;
        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            dentroCuarta = AgregarConversacion("Dentro, cuarta", _clienteDentro, null);
            dentroEmpate = AgregarConversacion("Dentro, empate", _clienteDentro, null);
            await _propietario.SaveChangesAsync();
        }
        using (AmbitoTenantExplicito.Establecer(otro))
        {
            otroSegunda = AgregarConversacion("Otro, segunda", clienteOtro, null);
            otroTercera = AgregarConversacion("Otro, tercera", clienteOtro, null);
            otroEmpate = AgregarConversacion("Otro, empate", clienteOtro, null);
            await _propietario.SaveChangesAsync();
        }

        var referencia = OrdenConversacionesUnificadas.TruncarAMicrosegundos(DateTime.UtcNow.AddMinutes(-10));
        var esperadas = new List<(Guid Id, Guid TenantId, DateTime Fecha)>
        {
            (_conversacionDentro, _tenantDentro, referencia.AddMinutes(-1)),
            (otroSegunda, otro, referencia.AddMinutes(-2)),
            (otroTercera, otro, referencia.AddMinutes(-3)),
            (dentroCuarta, _tenantDentro, referencia.AddMinutes(-4)),
            (dentroEmpate, _tenantDentro, referencia.AddMinutes(-5)),
            (otroEmpate, otro, referencia.AddMinutes(-5)),
        };
        foreach (var (id, _, fecha) in esperadas)
            await _propietario.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Conversaciones\" SET \"FechaUltimoMensajeUtc\" = {fecha} WHERE \"Id\" = {id}");
        esperadas.Sort((x, y) => OrdenConversacionesUnificadas.Comparar(x.Fecha, x.Id, y.Fecha, y.Id));

        var recorridas = new List<ConversacionUnificadaDto>();
        CursorConversacionesUnificadas? cursor = null;
        for (var vuelta = 0; vuelta < 20; vuelta++)
        {
            var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery(Cursor: cursor, TamanoPagina: 1));
            pagina.NoConsultados.Should().BeEmpty();
            recorridas.AddRange(pagina.Filas);
            cursor = pagina.Siguiente;
            if (cursor is null) break;
        }

        cursor.Should().BeNull("el recorrido termina");
        recorridas.Select(f => (f.Id, f.TenantId, f.FechaUltimoMensajeUtc)).Should().Equal(esperadas,
            "orden global entre los dos Tenants, sin repetir ni saltarse filas");
        recorridas.Select(f => f.TenantId).Distinct().Should().BeEquivalentTo(new[] { _tenantDentro, otro },
            "control: el recorrido mezcla de verdad los dos Tenants");
    }

    /// <summary>Otro Tenant beneficiario «dentro», como el del fixture: Operación Outbound y cartera vigentes.</summary>
    private async Task<(Guid TenantId, Guid ClienteId)> CrearTenantDentroAsync(string nombre, string cif)
    {
        var tenant = new Tenant(nombre);
        _propietario.Tenants.Add(tenant);
        var delegacion = new DelegacionTenant(_tenantOrigen, tenant.Id);
        _propietario.DelegacionesTenant.Add(delegacion);
        _propietario.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _usuario, Roles.GestorCae));
        await _propietario.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            var cliente = Empresa.CrearComoCliente($"Cliente de {nombre}", cif, false, null, null);
            _propietario.Empresas.Add(cliente);
            await _propietario.SaveChangesAsync();

            var operacion = AsignacionOperacion.Externa(
                tenant.Id, _tenantOrigen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora);
            OperacionExternaAmparada.Agregar(_propietario, operacion, delegacion);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _usuario, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
            await _propietario.SaveChangesAsync();
            return (tenant.Id, cliente.Id);
        }
    }

    [Fact]
    public async Task Las_sentencias_por_pagina_no_crecen_con_las_conversaciones()
    {
        _contador.Reiniciar();
        await EnviarAsync(new ObtenerConversacionesUnificadasQuery());
        var conUna = _contador.Lecturas;

        using (AmbitoTenantExplicito.Establecer(_tenantDentro))
        {
            for (var i = 0; i < 4; i++)
                AgregarConversacion($"Consulta adicional {i}", _clienteDentro, null);
            await _propietario.SaveChangesAsync();
        }

        _contador.Reiniciar();
        var pagina = await EnviarAsync(new ObtenerConversacionesUnificadasQuery());
        pagina.Filas.Count(f => f.TenantId == _tenantDentro).Should().Be(5);
        _contador.Lecturas.Should().Be(conUna, "sin N+1: el número de sentencias no depende de las filas");
        conUna.Should().BePositive("control del instrumento: el contador ve las lecturas");
    }

    [Fact]
    public async Task Con_Sesion_Privilegiada_seleccionada_la_vista_no_se_abre()
    {
        var handler = ActivatorUtilities.CreateInstance<ObtenerConversacionesUnificadasQueryHandler>(
            _servicios, new CoordenadaSeleccionada());

        var accion = () => handler.Handle(new ObtenerConversacionesUnificadasQuery(), CancellationToken.None);

        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task El_ambito_cerrado_no_se_reutiliza()
    {
        var identidad = await IdentidadVistaUnificada.ResolverAsync(_usuarioReal);
        AmbitoTenantAutorizado? capturado = null;

        var (autorizado, _) = await AmbitoTenantAutorizado.EjecutarAsync(
            identidad!, _runtime, _runtime, _tenantDentro,
            ambito =>
            {
                capturado = ambito;
                ambito.ExigirAbierto();
                return Task.FromResult(AmbitoTenantExplicito.TenantIdActual);
            },
            CancellationToken.None);

        autorizado.Should().BeTrue();
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull("el ámbito se cierra al volver");
        AmbitoSinViaHeredada.Activo.Should().BeFalse();
        capturado!.Invoking(a => a.ExigirAbierto()).Should().Throw<ObjectDisposedException>();

        // También si la lectura lanza: el using de EjecutarAsync cierra el ámbito. Las dos aserciones de arriba sobre el
        // AsyncLocal no lo distinguen (un AsyncLocal fijado dentro de un método async no vuelve a quien llama); esta sí.
        AmbitoTenantAutorizado? conFallo = null;
        var lecturaQueFalla = () => AmbitoTenantAutorizado.EjecutarAsync<bool>(
            identidad!, _runtime, _runtime, _tenantDentro,
            ambito =>
            {
                conFallo = ambito;
                throw new InvalidOperationException("lectura que falla");
            },
            CancellationToken.None);
        await lecturaQueFalla.Should().ThrowAsync<InvalidOperationException>().WithMessage("lectura que falla");
        conFallo!.Invoking(a => a.ExigirAbierto()).Should().Throw<ObjectDisposedException>(
            "el ámbito se cierra también cuando la lectura lanza");

        var (fuera, valor) = await AmbitoTenantAutorizado.EjecutarAsync(
            identidad!, _runtime, _runtime, _tenantFuera, _ => Task.FromResult(true), CancellationToken.None);
        fuera.Should().BeFalse("la re-comprobación rechaza un Tenant fuera del conjunto");
        valor.Should().BeFalse("la lectura no llega a ejecutarse");
    }

    private Guid AgregarConversacion(string asunto, Guid? clienteId, Guid? conexionId)
    {
        var conversacion = new Conversacion(asunto, clienteId: clienteId);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "alguien@externo.test", "<p>Hola</p>");
        if (conexionId is { } conexion)
            conversacion.AsociarConexion(conexion, $"hilo-{Guid.NewGuid():N}");
        _propietario.Conversaciones.Add(conversacion);
        return conversacion.Id;
    }

    private Task<PaginaConversacionesUnificadasDto> EnviarAsync(ObtenerConversacionesUnificadasQuery query) =>
        _servicios.GetRequiredService<IMediator>().Send(query);

    private CurrentUserService CrearCurrentUserService(ITenantsQueryContext contexto)
    {
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _usuario.ToString()),
                new Claim(ClaimTypes.Role, Roles.GestorCae),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _tenantOrigen.ToString())
            ],
            "prueba");

        var servicios = new ServiceCollection();
        servicios.AddSingleton(contexto);
        servicios.AddSingleton((CaeManager.Application.Operaciones.IOperacionesQueryContext)contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorFalso(),
            new SinClienteActivo(),
            servicios.AddTechoDeRolSinEncargoSembrado().BuildServiceProvider());
    }

    private sealed class ContadorLecturas : DbCommandInterceptor
    {
        private int _lecturas;

        public int Lecturas => _lecturas;

        public void Reiniciar() => Interlocked.Exchange(ref _lecturas, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _lecturas);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CoordenadaSeleccionada : ICoordenadaSesionPrivilegiada
    {
        public bool HaySeleccionada => true;
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
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

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class HttpContextAccessorFalso : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }
}
