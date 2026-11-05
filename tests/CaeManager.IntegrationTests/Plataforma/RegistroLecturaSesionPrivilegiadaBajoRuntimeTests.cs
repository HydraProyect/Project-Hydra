using CaeManager.Application.Common;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;
using RegistroLecturaSesionPrivilegiada = CaeManager.Infrastructure.Plataforma.RegistroLecturaSesionPrivilegiada;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Issue #2001: toda lectura que el Importador hace bajo una Sesión Privilegiada deja una fila en
/// <c>RegistrosActividadSoporte</c> con el Actor real, la Sesión Privilegiada y el Tenant objetivo,
/// contra PostgreSQL real y con la identidad de conexión de producción, <c>cae_app_runtime</c>, que
/// con la sesión adopta <c>cae_app_soporte</c>. El propietario solo siembra y lee lo que quedó escrito.
///
/// <para>
/// Lo que se ataca: la conexión de la sesión sigue sin <c>INSERT</c> sobre la tabla y la RLS no se
/// ensancha. La única escritura es la función <c>app_registrar_actividad_soporte_por_sesion</c>,
/// que vuelve a comprobar la sesión contra el contexto RLS firmado; cada precondición tiene aquí
/// un caso que la incumple, no deja fila y hace que el servicio devuelva false (fallo cerrado).
/// Mismo andamiaje que <see cref="RegistroAccesoDocumentoSensiblePorSoporteBajoRuntimeTests"/>.
/// </para>
/// </summary>
public class RegistroLecturaSesionPrivilegiadaBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "app_registrar_actividad_soporte_por_sesion";
    private const string Firma = Funcion + "(uuid, uuid, text, text)";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _otroTecnico = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Siembra como propietario: el rol de runtime no crea Tenants ni cuentas.
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
        contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
        contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
        SembrarCuenta(contexto, "tecnico", _tecnico);
        SembrarCuenta(contexto, "otro-tecnico", _otroTecnico);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ──────────────────────────────────────────────────────

    [Fact]
    public async Task En_una_Sesion_Privilegiada_vigente_la_lectura_queda_registrada_con_actor_sesion_y_Tenant_objetivo()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);

        (await RegistrarComoSoporteAsync(_tenantA, sesion, "EjecutarImportacionCommand")).Should().BeTrue();

        var fila = (await LeerFilasAsync(sesion)).Should().ContainSingle().Subject;
        fila.TenantId.Should().Be(_tenantA, "el Tenant de la fila es el Tenant objetivo, sacado del contexto firmado");
        fila.UsuarioSoporteId.Should().Be(_tecnico, "el Actor real es el técnico de Soporte TALVEG");
        fila.SesionPrivilegiadaId.Should().Be(sesion);
        fila.DelegacionTenantId.Should().BeNull("exactamente un agrupador: la Sesión Privilegiada");
        fila.Tipo.Should().Be(nameof(TipoActividadSoporte.Lectura));
        fila.Detalle.Should().Be("EjecutarImportacionCommand");
    }

    [Fact]
    public async Task Si_la_sesion_simula_a_un_usuario_la_fila_nombra_al_actor_real()
    {
        // Sesión de impersonación construida por el dominio (SesionPrivilegiada.Abrir solo admite
        // Usuario simulado bajo una concesión de Impersonacion), sembrada como propietario.
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Impersonacion, [_tenantA],
            ahora.AddMinutes(-10), ahora.AddHours(4), concedidaPorUsuarioId: _otroTecnico);
        var impersonacion = SesionPrivilegiada.Abrir(
            concesion, _tenantA, "Reproducir lo que ve el usuario", ahora.AddMinutes(-1), TimeSpan.FromMinutes(30),
            usuarioSimuladoId: Guid.NewGuid());
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            contexto.SesionesPrivilegiadas.Add(impersonacion);
            await contexto.SaveChangesAsync();
        }

        var sesion = impersonacion.Id;
        impersonacion.UsuarioSimuladoId.Should().NotBeNull();

        (await RegistrarComoSoporteAsync(_tenantA, sesion, "AnalizarPlantillaCombinadaQuery")).Should().BeTrue();

        (await LeerFilasAsync(sesion)).Should().ContainSingle()
            .Which.UsuarioSoporteId.Should().Be(_tecnico, "en impersonación, la fila es del Actor real, nunca del Usuario simulado");
    }

    /// <summary>
    /// El behavior del pipeline con la pieza real: la lectura (el <c>next</c>) solo corre después de
    /// que la fila quede escrita, y con la misma conexión de la sesión, que lleva <c>cae_app_soporte</c>.
    /// </summary>
    [Fact]
    public async Task El_behavior_registra_antes_de_leer_y_la_lectura_corre_despues()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<ObtenerHistorialImportacionesQuery, IReadOnlyList<HistorialImportacionDto>>(
            new SesionFija(sesion, _tenantA),
            new CoordenadaFija(true),
            new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance));

        IReadOnlyList<HistorialImportacionDto> leido = [];
        var filasVistasPorLaLectura = -1;
        var resultado = await behavior.Handle(new ObtenerHistorialImportacionesQuery(), async ct =>
        {
            filasVistasPorLaLectura = (await LeerFilasAsync(sesion)).Count;
            return leido;
        }, CancellationToken.None);

        resultado.Should().BeSameAs(leido, "el behavior devuelve lo que leyó la lectura");
        filasVistasPorLaLectura.Should().Be(1, "la fila existe antes de que la lectura corra");
        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be(nameof(ObtenerHistorialImportacionesQuery));
    }

    [Fact]
    public async Task Con_una_sesion_cerrada_el_behavior_no_deja_leer_ni_escribe_fila()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        // La memo de la aplicación todavía nombra la sesión: la función es la que la rechaza.
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<ObtenerHistorialImportacionesQuery, IReadOnlyList<HistorialImportacionDto>>(
            new SesionFija(sesion, _tenantA),
            new CoordenadaFija(true),
            new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance));

        var leyo = false;
        var ejecutar = () => behavior.Handle(new ObtenerHistorialImportacionesQuery(), _ =>
        {
            leyo = true;
            return Task.FromResult<IReadOnlyList<HistorialImportacionDto>>([]);
        }, CancellationToken.None);

        await ejecutar.Should().ThrowAsync<LecturaPrivilegiadaNoRegistradaException>();
        leyo.Should().BeFalse("lo que no deja rastro no se sirve");
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    /// <summary>
    /// Con las piezas reales de la aplicación: cerrada la sesión, <c>SesionPrivilegiadaActual</c>
    /// deja de resolverla pero el circuito aún la nombra y la conexión sigue adoptando
    /// <c>cae_app_soporte</c>. El behavior no deja pasar la lectura sin fila.
    /// </summary>
    [Fact]
    public async Task Una_sesion_seleccionada_que_ya_no_resuelve_no_deja_leer_bajo_la_identidad_de_soporte()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);

        // Control positivo: la misma composición, con la sesión abierta, registra y lee.
        (await LeerConPiezasRealesAsync(sesion)).Should().BeTrue();
        (await LeerFilasAsync(sesion)).Should().ContainSingle();

        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);

        var leer = () => LeerConPiezasRealesAsync(sesion);

        await leer.Should().ThrowAsync<LecturaPrivilegiadaNoRegistradaException>();
        (await LeerFilasAsync(sesion)).Should().ContainSingle("la lectura rechazada no deja fila nueva");
    }

    /// <summary>
    /// now() es la hora de inicio de la transacción. Una transacción abierta mientras la sesión
    /// era vigente no puede seguir registrando lecturas después de que caduque.
    /// </summary>
    [Fact]
    public async Task Una_transaccion_abierta_antes_de_caducar_la_sesion_no_registra_despues()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        (await RegistrarComoSoporteAsync(_tenantA, sesion, "ObtenerHistorialImportacionesQuery")).Should().BeTrue("control positivo");

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync();
            await contexto.Database.ExecuteSqlRawAsync("SELECT now();");
            // La caducidad se fija DESPUÉS de empezar la transacción, desde otra conexión: now()
            // dentro de ella queda por fuerza antes de ExpiraEnUtc, con cualquier carga de CI.
            await EjecutarComoPropietarioAsync(
                @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = clock_timestamp() + interval '2 seconds' WHERE ""Id"" = @u;", sesion);
            await Task.Delay(TimeSpan.FromSeconds(4));

            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
                .Should().Be("sesion_no_autorizada", "la ventana se comprueba con la hora del reloj, no con la de la transacción");
            await transaccion.RollbackAsync();
        }

        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be("ObtenerHistorialImportacionesQuery");
    }

    // ── Cada precondición de la función, incumplida: no hay fila y el servicio da false ─

    [Fact]
    public async Task Una_sesion_cerrada_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_caducada_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = now() - interval '1 minute' WHERE ""Id"" = @u;", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_que_aun_no_empieza_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""InicioEnUtc"" = now() + interval '5 minutes' WHERE ""Id"" = @u;", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_sesion_sobre_A_no_registra_con_el_contexto_de_B_aunque_la_concesion_alcance_los_dos()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA, _tenantB]);

        await NoRegistraAsync(_tenantB, sesion);
    }

    [Fact]
    public async Task Otro_actor_no_registra_con_la_sesion_del_tecnico_aunque_conozca_su_Id()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await NoRegistraAsync(_tenantA, sesion, actor: _otroTecnico);
    }

    [Fact]
    public async Task Una_concesion_revocada_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""Estado"" = 'Revocada'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_caducada_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaHasta"" = now() - interval '1 minute'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_que_aun_no_es_vigente_no_registra_aunque_la_sesion_siga_abierta()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await EjecutarComoPropietarioAsync(@"
UPDATE ""ConcesionesPrivilegio"" SET ""VigenciaDesde"" = now() + interval '5 minutes'
WHERE ""Id"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task Una_concesion_por_Tenant_que_ya_no_alcanza_el_Tenant_no_registra()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await EjecutarComoPropietarioAsync(@"
UPDATE ""TenantsAlcanzadosPorConcesion"" SET ""TenantId"" = gen_random_uuid()
WHERE ""ConcesionPrivilegioId"" = (SELECT ""ConcesionPrivilegioId"" FROM ""SesionesPrivilegiadas"" WHERE ""Id"" = @u);", sesion);

        await NoRegistraAsync(_tenantA, sesion);
    }

    [Fact]
    public async Task La_funcion_solo_acepta_el_tipo_Lectura()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            foreach (var tipo in Enum.GetValues<TipoActividadSoporte>().Where(t => t != TipoActividadSoporte.Lectura))
            {
                (await LlamarFuncionAsync(contexto, sesion, tipo.ToString(), "ObtenerHistorialImportacionesQuery"))
                    .Should().Be("valor_no_valido", $"{tipo} no es una lectura y la función no lo escribe");
            }
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    /// <summary>
    /// El detalle es una lista cerrada con el nombre de cada request marcado: un llamador con EXECUTE
    /// no puede escribir texto libre, datos leídos ni un valor fabricado con forma de identificador
    /// en un registro que también es dato personal del técnico.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("Nombre con espacios")]
    [InlineData("1EmpiezaPorDigito")]
    [InlineData("Juan Pérez, DNI 12345678Z")]
    [InlineData("Consulta;DROP")]
    [InlineData("x\ny")]
    [InlineData("DNI_12345678Z")]
    [InlineData("LecturaMarcada")]
    [InlineData("ejecutarimportacioncommand")]
    [InlineData("EjecutarImportacionCommand ")]
    [InlineData("RegistrarHistorialImportacionCommand")]
    public async Task La_funcion_rechaza_un_detalle_fuera_de_la_lista_cerrada(string detalle)
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
                .Should().Be("registrado", "control positivo con un identificador válido");
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), detalle))
                .Should().Be("valor_no_valido");
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), new string('A', 201)))
                .Should().Be("valor_no_valido", "más de 200 caracteres no es un nombre de la lista");
        }

        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be("EjecutarImportacionCommand");
    }

    [Fact]
    public async Task Un_nombre_con_forma_de_request_fuera_de_la_lista_no_deja_fila()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "DNI_12345678Z"))
                .Should().Be("valor_no_valido");
            (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                    .RegistrarAsync(sesion, "DNI_12345678Z"))
                .Should().BeFalse("el servicio traduce el rechazo a false, y el behavior no sirve los datos");
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    /// <summary>
    /// Control positivo de la lista cerrada contra la base migrada: cada request marcado
    /// <see cref="ILecturaRegistradaEnSesionPrivilegiada"/> se registra con su nombre. Con la
    /// igualdad que fija el test de arquitectura, ningún request marcado queda sin poder registrarse.
    /// </summary>
    [Fact]
    public async Task Cada_request_marcado_se_registra_con_su_nombre()
    {
        var nombres = typeof(ILecturaRegistradaEnSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILecturaRegistradaEnSesionPrivilegiada).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();
        nombres.Should().HaveCount(11, "el instrumento tiene que ver los requests marcados");

        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            foreach (var nombre in nombres)
            {
                (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), nombre))
                    .Should().Be("registrado", "{0} es un request marcado", nombre);
            }
        }

        (await LeerFilasAsync(sesion)).Select(f => f.Detalle).Should().BeEquivalentTo(nombres);
    }

    // ── Aislamiento: solo READ COMMITTED, donde cada sentencia ve el estado actual ─

    /// <summary>
    /// Bajo REPEATABLE READ o SERIALIZABLE la función vería la sesión y la concesión como estaban
    /// al empezar la transacción, aunque después se cerraran o revocaran: rechaza sin insertar,
    /// también con la sesión vigente, y confirmar la transacción no deja fila.
    /// </summary>
    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task Bajo_un_aislamiento_mas_fuerte_que_READ_COMMITTED_no_registra_aunque_la_sesion_este_vigente(System.Data.IsolationLevel aislamiento)
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(aislamiento);
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
                .Should().Be("aislamiento_no_admitido");
            (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                    .RegistrarAsync(sesion, "EjecutarImportacionCommand"))
                .Should().BeFalse("el servicio traduce el rechazo a false, y el behavior no sirve los datos");
            await transaccion.CommitAsync();
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty("ni confirmando la transacción queda fila");
        (await RegistrarComoSoporteAsync(_tenantA, sesion, "ObtenerHistorialImportacionesQuery"))
            .Should().BeTrue("control positivo: la misma sesión, fuera de esa transacción, registra");
    }

    /// <summary>
    /// El caso que el rechazo por aislamiento cierra: la transacción REPEATABLE READ toma su
    /// instantánea con la sesión abierta, otra conexión la cierra, y la función no la da por vigente.
    /// </summary>
    [Fact]
    public async Task Una_transaccion_REPEATABLE_READ_con_instantanea_previa_al_cierre_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            // La primera sentencia fija la instantánea de la transacción.
            await contexto.Database.ExecuteSqlRawAsync("SELECT now();");
            await EjecutarComoPropietarioAsync(
                @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", sesion);

            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
                .Should().Be("aislamiento_no_admitido");
            await transaccion.CommitAsync();
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    [Fact]
    public async Task En_una_transaccion_READ_COMMITTED_explicita_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
                .Should().Be("registrado");
            await transaccion.CommitAsync();
        }

        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be("EjecutarImportacionCommand");
    }

    // ── Transacción explícita: la fila no puede deshacerse después de servir los datos ─

    /// <summary>
    /// Dentro de una transacción explícita, aunque sea READ COMMITTED, la función registraría y un
    /// ROLLBACK posterior borraría la fila de una lectura ya servida. El servicio no llega a
    /// llamarla: devuelve false y el behavior no deja leer.
    /// </summary>
    [Fact]
    public async Task Dentro_de_una_transaccion_explicita_el_servicio_no_registra_y_un_rollback_no_deja_lectura_sin_fila()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                    .RegistrarAsync(sesion, "EjecutarImportacionCommand"))
                .Should().BeFalse("con una transacción abierta la fila podría deshacerse");
            await transaccion.CommitAsync();
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty("el servicio no llamó a la función, ni confirmando");
        (await RegistrarComoSoporteAsync(_tenantA, sesion, "EjecutarImportacionCommand"))
            .Should().BeTrue("control positivo: el mismo servicio, sin transacción, registra");
    }

    [Fact]
    public async Task Dentro_de_una_transaccion_el_behavior_no_deja_leer()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<ObtenerHistorialImportacionesQuery, IReadOnlyList<HistorialImportacionDto>>(
            new SesionFija(sesion, _tenantA),
            new CoordenadaFija(true),
            new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance));

        await using var transaccion = await contexto.Database.BeginTransactionAsync();
        var leyo = false;
        var ejecutar = () => behavior.Handle(new ObtenerHistorialImportacionesQuery(), _ =>
        {
            leyo = true;
            return Task.FromResult<IReadOnlyList<HistorialImportacionDto>>([]);
        }, CancellationToken.None);

        await ejecutar.Should().ThrowAsync<LecturaPrivilegiadaNoRegistradaException>();
        leyo.Should().BeFalse("lo que no deja rastro duradero no se sirve");
        await transaccion.RollbackAsync();
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_una_transaccion_ambiente_el_servicio_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        using (new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
            (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                    .RegistrarAsync(sesion, "EjecutarImportacionCommand"))
                .Should().BeFalse("una transacción ambiente también podría deshacer la fila");
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    // ── Unicidad: el contexto firmado no nombra la sesión, así que no puede haber dos ─

    /// <summary>
    /// Con dos Sesiones Privilegiadas vigentes del mismo Actor real sobre el mismo Tenant, una
    /// llamada directa podría atribuir la fila a la que no se usa: la función rechaza con las dos.
    /// </summary>
    [Fact]
    public async Task Con_dos_sesiones_vigentes_del_mismo_actor_sobre_el_Tenant_no_registra_ninguna()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        var otra = await SembrarOtraSesionVigenteAsync(_tenantA);

        foreach (var nombrada in new[] { sesion, otra })
        {
            await using (var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion))
            {
                (await LlamarFuncionAsync(contexto, nombrada, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
                    .Should().Be("sesion_ambigua");
            }

            (await RegistrarComoSoporteAsync(_tenantA, nombrada, "EjecutarImportacionCommand"))
                .Should().BeFalse("el servicio traduce el rechazo a false, y el behavior no sirve los datos");
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
        (await LeerFilasAsync(otra)).Should().BeEmpty();

        // Cerrada la otra, la que queda vuelve a registrar.
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""CerradaEnUtc"" = now() WHERE ""Id"" = @u;", otra);
        (await RegistrarComoSoporteAsync(_tenantA, sesion, "EjecutarImportacionCommand")).Should().BeTrue();
        (await LeerFilasAsync(sesion)).Should().ContainSingle();
    }

    [Fact]
    public async Task Una_segunda_sesion_sobre_otro_Tenant_o_ya_caducada_no_hace_ambigua_la_vigente()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await SembrarOtraSesionVigenteAsync(_tenantB);
        var caducada = await SembrarOtraSesionVigenteAsync(_tenantA);
        await EjecutarComoPropietarioAsync(
            @"UPDATE ""SesionesPrivilegiadas"" SET ""ExpiraEnUtc"" = clock_timestamp() - interval '1 second' WHERE ""Id"" = @u;", caducada);

        (await RegistrarComoSoporteAsync(_tenantA, sesion, "EjecutarImportacionCommand")).Should().BeTrue();
        (await LeerFilasAsync(sesion)).Should().ContainSingle();
    }

    [Fact]
    public async Task La_sesion_vigente_de_otro_actor_sobre_el_mismo_Tenant_no_hace_ambigua_la_propia()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await SembrarOtraSesionVigenteAsync(_tenantA, _otroTecnico);

        (await RegistrarComoSoporteAsync(_tenantA, sesion, "EjecutarImportacionCommand")).Should().BeTrue();
    }

    /// <summary>
    /// La carrera que cierra el candado SHARE: la función nombra A mientras otra conexión tiene sin
    /// confirmar una segunda sesión B vigente del mismo actor sobre el Tenant. La función espera el
    /// candado (lo que se comprueba en pg_locks, no por tiempo), B se confirma, y la cuenta, tomada
    /// después del candado, ve las dos: 'sesion_ambigua' y ninguna fila atribuida a A.
    /// </summary>
    [Fact]
    public async Task Una_segunda_sesion_que_se_confirma_mientras_la_funcion_espera_el_candado_la_hace_ambigua()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await using var propietario = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        await using var transaccionB = await propietario.Database.BeginTransactionAsync();
        AnadirOtraSesionVigente(propietario, _tenantA);
        await propietario.SaveChangesAsync();

        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();
        var llamada = LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand");

        (await EsperaElCandadoAsync(llamada)).Should().BeTrue("la función espera el candado SHARE mientras B no se confirma");
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        llamada.IsCompleted.Should().BeFalse("sigue esperando a que B termine");

        await transaccionB.CommitAsync();

        (await llamada).Should().Be("sesion_ambigua");
        (await LeerFilasAsync(sesion)).Should().BeEmpty("la fila no queda atribuida a A con otra sesión vigente");
    }

    [Fact]
    public async Task Si_la_segunda_sesion_se_deshace_mientras_la_funcion_espera_el_candado_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        await using var propietario = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        await using var transaccionB = await propietario.Database.BeginTransactionAsync();
        AnadirOtraSesionVigente(propietario, _tenantA);
        await propietario.SaveChangesAsync();

        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();
        var llamada = LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand");

        (await EsperaElCandadoAsync(llamada)).Should().BeTrue("la función espera el candado SHARE mientras B no termina");
        await transaccionB.RollbackAsync();

        (await llamada).Should().Be("registrado");
        (await LeerFilasAsync(sesion)).Should().ContainSingle();
    }

    /// <summary>
    /// LOCK ... IN SHARE MODE exige UPDATE, DELETE o TRUNCATE sobre la tabla, y dentro de una función
    /// SECURITY DEFINER se comprueba con el propietario de la función (el rol de las migraciones).
    /// </summary>
    [Fact]
    public async Task El_propietario_de_la_funcion_puede_bloquear_SesionesPrivilegiadas_en_SHARE()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            SELECT pg_get_userbyid(p.proowner) = pg_get_userbyid(c.relowner),
                   has_table_privilege(p.proowner, c.oid, 'UPDATE')
                   OR has_table_privilege(p.proowner, c.oid, 'DELETE')
                   OR has_table_privilege(p.proowner, c.oid, 'TRUNCATE')
              FROM pg_proc p, pg_class c
             WHERE p.proname = '{Funcion}' AND p.pronamespace = 'public'::regnamespace
               AND c.oid = 'public."SesionesPrivilegiadas"'::regclass;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la función existe tras migrar");
        lector.GetBoolean(0).Should().BeTrue("las migraciones crean la tabla y la función con el mismo rol");
        lector.GetBoolean(1).Should().BeTrue("el propietario de la función puede tomar el candado SHARE");
    }

    /// <summary>
    /// Espera, hasta 10 s, a ver en pg_locks un candado no concedido sobre SesionesPrivilegiadas
    /// mientras la llamada sigue en curso. False si la llamada termina antes o no llega a esperar.
    /// </summary>
    private async Task<bool> EsperaElCandadoAsync(Task llamada)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        for (var intento = 0; intento < 100 && !llamada.IsCompleted; intento++)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "SELECT count(*) FROM pg_locks WHERE NOT granted AND relation = 'public.\"SesionesPrivilegiadas\"'::regclass;";
            if ((long)(await comando.ExecuteScalarAsync())! > 0)
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>Segunda sesión vigente del actor (por defecto el técnico), sin guardar.</summary>
    private Guid AnadirOtraSesionVigente(CaeManagerDbContext contexto, Guid tenantObjetivo, Guid? actor = null)
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            actor ?? _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _otroTecnico);
        var otra = SesionPrivilegiada.Abrir(
            concesion, tenantObjetivo, "Segunda sesión en paralelo", ahora.AddMinutes(-1), TimeSpan.FromMinutes(30));
        contexto.ConcesionesPrivilegio.Add(concesion);
        contexto.SesionesPrivilegiadas.Add(otra);
        return otra.Id;
    }

    /// <summary>Segunda sesión vigente, sembrada y confirmada como propietario con el dominio.</summary>
    private async Task<Guid> SembrarOtraSesionVigenteAsync(Guid tenantObjetivo, Guid? actor = null)
    {
        await using var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion);
        var otra = AnadirOtraSesionVigente(contexto, tenantObjetivo, actor);
        await contexto.SaveChangesAsync();
        return otra;
    }

    [Fact]
    public async Task Un_contexto_firmado_invalidado_no_registra()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();

        // Control positivo: la misma conexión y la misma sesión registran antes de invalidar el contexto.
        (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "ObtenerHistorialImportacionesQuery")).Should().Be("registrado");
        await contexto.Database.ExecuteSqlRawAsync("SELECT set_config('app.contexto', '', false);");

        (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "EjecutarImportacionCommand"))
            .Should().Be("contexto_no_valido");
        (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                .RegistrarAsync(sesion, "EjecutarImportacionCommand"))
            .Should().BeFalse();

        (await LeerFilasAsync(sesion)).Should().ContainSingle().Which.Detalle.Should().Be("ObtenerHistorialImportacionesQuery");
    }

    // ── Quién puede ejecutarla, y la tabla sin INSERT para la sesión ─────────

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_rol_de_runtime_no_puede_ejecutarla()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();

        // Mismo técnico, mismo Tenant, pero sin la sesión en la conexión: el rol es
        // cae_app_runtime, que no tiene EXECUTE.
        await using (var contexto = CrearContexto(_tenantA))
        {
            var ejecutar = async () => await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "ObtenerHistorialImportacionesQuery");
            (await ejecutar.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

            (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                    .RegistrarAsync(sesion, "ObtenerHistorialImportacionesQuery"))
                .Should().BeFalse("el 42501 se traduce a false, que el behavior convierte en fallo cerrado");
        }

        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("cae_app_runtime", false)]
    [InlineData("cae_app_soporte", true)]
    [InlineData("cae_app_aprovisionamiento", false)]
    public async Task Solo_cae_app_soporte_tiene_EXECUTE(string rol, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT has_function_privilege(@rol, '{Firma}', 'EXECUTE');";
        comando.Parameters.AddWithValue("rol", rol);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado);
    }

    [Fact]
    public async Task La_funcion_es_SECURITY_DEFINER_con_search_path_fijo_y_soporte_sigue_sin_INSERT()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"""
            SELECT p.prosecdef,
                   array_to_string(p.proconfig, ';'),
                   has_table_privilege('cae_app_soporte', '"RegistrosActividadSoporte"', 'INSERT'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
              FROM pg_proc p
             WHERE p.proname = '{Funcion}' AND p.pronamespace = 'public'::regnamespace;
            """;
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("la función existe tras migrar");

        lector.GetBoolean(0).Should().BeTrue("SECURITY DEFINER");
        lector.GetString(1).Should().Be("search_path=pg_catalog, pg_temp",
            "sin search_path fijo, una tabla temporal podría suplantar a las de la sesión");
        lector.GetBoolean(2).Should().BeFalse("la función es la única puerta: la conexión de soporte no gana INSERT");
        lector.GetBoolean(3).Should().BeFalse("PUBLIC no conserva EXECUTE");
    }

    [Fact]
    public async Task La_conexion_de_la_sesion_no_puede_insertar_directamente_en_la_tabla()
    {
        var sesion = await AbrirSesionDeSoporteLecturaAsync();
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        contexto.RegistrosActividadSoporte.Add(
            RegistroActividadSoporte.PorSesionPrivilegiada(_tecnico, sesion, TipoActividadSoporte.Lectura, "directo"));

        var guardar = () => contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    /// <summary>
    /// Por qué el behavior va ANTES de la elevación: con la conexión ya en
    /// <c>cae_app_aprovisionamiento</c>, la función no se puede ejecutar y el registro falla en cerrado.
    /// </summary>
    [Fact]
    public async Task Con_la_conexion_elevada_a_aprovisionamiento_el_registro_falla_en_cerrado()
    {
        var sesion = await AbrirSesionDeAprovisionamientoAsync([_tenantA]);
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        await contexto.Database.OpenConnectionAsync();
        await contexto.Database.ExecuteSqlRawAsync("SET ROLE cae_app_aprovisionamiento;");

        (await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
                .RegistrarAsync(sesion, "EjecutarImportacionCommand"))
            .Should().BeFalse();

        await contexto.Database.ExecuteSqlRawAsync("SET ROLE cae_app_soporte;");
        (await LeerFilasAsync(sesion)).Should().BeEmpty();
    }

    // ── Andamiaje ────────────────────────────────────────────────────────────

    private sealed class SesionFija(Guid sesionId, Guid tenantObjetivo) : ISesionPrivilegiadaActual
    {
        private readonly SesionPrivilegiadaActiva _sesion =
            new(sesionId, Guid.NewGuid(), tenantObjetivo, CapacidadPrivilegio.Aprovisionamiento, null);

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(_sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(_sesion);
    }

    private sealed class CoordenadaFija(bool haySeleccionada) : ICoordenadaSesionPrivilegiada
    {
        public bool HaySeleccionada => haySeleccionada;
    }

    /// <summary>
    /// El behavior con las piezas reales de la aplicación sobre un ámbito nuevo (la memo de
    /// <c>SesionPrivilegiadaActual</c> es por ámbito): la sesión seleccionada en el circuito,
    /// la conexión de esa sesión y el registro real. Devuelve si la lectura llegó a correr.
    /// </summary>
    private async Task<bool> LeerConPiezasRealesAsync(Guid sesion)
    {
        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesion);
        var actual = new CaeManager.Infrastructure.Plataforma.SesionPrivilegiadaActual(
            contexto, new ClienteActivoSeleccionadoFalso(_tenantA, sesion), UsuarioTecnico());
        var behavior = new RegistroLecturaSesionPrivilegiadaBehavior<ObtenerHistorialImportacionesQuery, IReadOnlyList<HistorialImportacionDto>>(
            actual, actual,
            new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance));

        var leyo = false;
        await behavior.Handle(new ObtenerHistorialImportacionesQuery(), _ =>
        {
            leyo = true;
            return Task.FromResult<IReadOnlyList<HistorialImportacionDto>>([]);
        }, CancellationToken.None);
        return leyo;
    }

    private async Task NoRegistraAsync(Guid tenantDelContexto, Guid sesion, Guid? actor = null)
    {
        await using (var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion, actor: actor))
        {
            (await LlamarFuncionAsync(contexto, sesion, nameof(TipoActividadSoporte.Lectura), "ObtenerHistorialImportacionesQuery"))
                .Should().Be("sesion_no_autorizada");
        }

        (await RegistrarComoSoporteAsync(tenantDelContexto, sesion, "EjecutarImportacionCommand", actor))
            .Should().BeFalse("el servicio traduce el rechazo a false, y el behavior no sirve los datos");

        (await LeerFilasAsync(sesion)).Should().BeEmpty("sin sesión válida la función no escribe");
    }

    private async Task<bool> RegistrarComoSoporteAsync(Guid tenantDelContexto, Guid sesion, string operacion, Guid? actor = null)
    {
        await using var contexto = CrearContexto(tenantDelContexto, sesionPrivilegiadaId: sesion, actor: actor);
        return await new RegistroLecturaSesionPrivilegiada(contexto, NullLogger<RegistroLecturaSesionPrivilegiada>.Instance)
            .RegistrarAsync(sesion, operacion);
    }

    private static Task<string> LlamarFuncionAsync(CaeManagerDbContext contexto, Guid sesion, string tipo, string detalle) =>
        contexto.Database
            .SqlQuery<string>(
                $"SELECT app_registrar_actividad_soporte_por_sesion({sesion}, {Guid.NewGuid()}, {tipo}, {detalle}) AS \"Value\"")
            .SingleAsync();

    private void SembrarCuenta(CaeManagerDbContext contexto, string nombre, Guid id)
    {
        // El técnico de Soporte TALVEG es una cuenta del Tenant de plataforma.
        var correo = $"{nombre}@registro-lectura-sesion.test";
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = nombre,
            TenantId = _tenantPlataforma,
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
        });
    }

    private async Task<Guid> AbrirSesionDeSoporteLecturaAsync()
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);

        // Sembrada como propietario: bajo runtime la auto-concesión exige AdminPlataforma vigente.
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    /// <summary>Aprovisionamiento por Tenant: la capacidad con la que el Importador lee bajo la sesión.</summary>
    private async Task<Guid> AbrirSesionDeAprovisionamientoAsync(Guid[] tenantsAlcanzados)
    {
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.Aprovisionamiento, tenantsAlcanzados,
            ahora.AddMinutes(-10), ahora.AddDays(1), concedidaPorUsuarioId: _otroTecnico);
        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.ConcesionesPrivilegio.Add(concesion);
            await contexto.SaveChangesAsync();
        }

        return await AbrirComoRuntimeAsync(concesion.Id, _tenantA);
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), UsuarioTecnico(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "Carga inicial del catálogo", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private sealed record FilaRegistro(
        Guid TenantId, Guid UsuarioSoporteId, string Tipo, Guid? DelegacionTenantId, Guid? SesionPrivilegiadaId, string? Detalle);

    private async Task<List<FilaRegistro>> LeerFilasAsync(Guid sesion)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT "TenantId", "UsuarioSoporteId", "Tipo", "DelegacionTenantId", "SesionPrivilegiadaId", "Detalle"
              FROM "RegistrosActividadSoporte"
             WHERE "SesionPrivilegiadaId" = @s
             ORDER BY "OcurridaEnUtc";
            """;
        comando.Parameters.AddWithValue("s", sesion);
        await using var lector = await comando.ExecuteReaderAsync();
        var filas = new List<FilaRegistro>();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaRegistro(
                lector.GetGuid(0),
                lector.GetGuid(1),
                lector.GetString(2),
                lector.IsDBNull(3) ? null : lector.GetGuid(3),
                lector.IsDBNull(4) ? null : lector.GetGuid(4),
                lector.IsDBNull(5) ? null : lector.GetString(5)));
        }

        return filas;
    }

    private async Task EjecutarComoPropietarioAsync(string sql, Guid parametro)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", parametro);
        (await comando.ExecuteNonQueryAsync()).Should().BeGreaterThan(0, "la preparación tiene que tocar alguna fila");
    }

    private CurrentUserServiceFalso UsuarioTecnico(Guid? actor = null) =>
        new(actor ?? _tecnico, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de producción.
    /// Solo la siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(
        Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadena = null, Guid? actor = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var seleccion = new ClienteActivoSeleccionadoFalso(
            sesionPrivilegiadaId is null ? null : tenantId, sesionPrivilegiadaId);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion, UsuarioTecnico(actor), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Domain.Tenants.Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Domain.Tenants.Tenant(nombre);
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
