using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Los defectos que encontró la revisión final de F1, cada uno con el escenario
/// concreto que fallaba. No son tests de "que funcione": son la prueba de que
/// cada agujero está cerrado.
/// </summary>
public class CorreccionesRevisionF1Tests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorConsultora = Guid.NewGuid();
    private readonly Guid _gestorPropietario = Guid.NewGuid();
    private Guid _consultora;
    private Guid _propietario;
    private Guid _clienteId;
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var inicial = CrearContexto(Guid.NewGuid());
        await inicial.Database.MigrateAsync();

        var consultora = new Tenant("Consultora F1", PerfilVocabularioTenant.Consultora);
        var propietario = new Tenant("Propietario F1", PerfilVocabularioTenant.ClienteDirecto);
        inicial.Tenants.Add(consultora);
        inicial.Tenants.Add(propietario);
        await inicial.SaveChangesAsync();

        _consultora = consultora.Id;
        _propietario = propietario.Id;

        await using var contexto = CrearContexto(_propietario);

        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorConsultora,
            TenantId = _consultora,
            UserName = "g@consultora",
            Email = "g@consultora"
        });
        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorPropietario,
            TenantId = _propietario,
            UserName = "g@propietario",
            Email = "g@propietario"
        });

        var delegacion = new DelegacionTenant(_consultora, _propietario);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _gestorConsultora, Roles.GestorCae));
        _delegacionId = delegacion.Id;

        var cliente = Empresa.CrearComoCliente("Cliente F1", "B12345674", false, null, _gestorConsultora);
        contexto.Empresas.Add(cliente);

        contexto.AsignacionesOperacion.Add(
            AsignacionOperacion.Raiz(_propietario, ServicioCae.Outbound, DateTime.UtcNow, DateTime.UtcNow));

        await contexto.SaveChangesAsync();
        _clienteId = cliente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ---------- B3: el alcance no se ensancha ----------

    [Fact]
    public async Task La_referencia_de_un_Cliente_empresarial_no_concede_cartera_ni_alcance()
    {
        // D-7 (2026-10-02): Empresa.EjecutivoUsuarioId es una referencia, no una cartera. El Gestor CAE
        // delegado es la referencia de _clienteId, tiene su fila de operador delegado y la operación
        // externa existe, y aun así no alcanza nada: sin una Asignación de Cartera explícita, alcance
        // cero. Antes, el backfill derivaba de la referencia una cartera por Cliente empresarial.
        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_propietario);
        (await contexto.AsignacionesCartera.AnyAsync(c => c.UsuarioId == _gestorConsultora))
            .Should().BeFalse("el backfill ya no deriva carteras de la referencia");

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task La_cartera_explicita_da_el_Tenant_entero_y_no_solo_los_clientes_de_los_que_es_referencia()
    {
        await using (var otros = CrearContexto(_propietario))
        {
            otros.Empresas.Add(Empresa.CrearComoCliente(
                "Cliente de otro gestor", "B10380186", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            otros.Empresas.Add(Empresa.CrearComoCliente(
                "Cliente sin gestor", "B10380194", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await otros.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();
        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            await contexto.SaveChangesAsync();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().HaveCount(3, "el Tenant entero: los tres Clientes empresariales");
    }

    [Fact]
    public async Task El_backfill_no_emite_cartera_universal_para_un_rol_de_cartera()
    {
        await EjecutarBackfillAsync();

        await using var contexto = CrearContexto(_propietario);
        var universales = await contexto.AsignacionesCartera
            .Where(c => c.UsuarioId == _gestorConsultora && c.AmbitoRelacionClienteId == null)
            .ToListAsync();

        universales.Should().BeEmpty();
    }

    // ---------- D2: la cartera explícita es idempotente y no cierra lo de otros ----------

    [Fact]
    public async Task Asegurar_la_cartera_del_Tenant_entero_es_idempotente_y_no_cierra_nada()
    {
        await EjecutarBackfillAsync();

        for (var vez = 0; vez < 2; vez++)
        {
            await using var contexto = CrearContexto(_propietario);
            await CrearWriter(contexto).AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto(_propietario);
        var carteras = await verificacion.AsignacionesCartera.Where(c => c.UsuarioId == _gestorConsultora).ToListAsync();
        carteras.Should().ContainSingle().Which.Should().Match<AsignacionCartera>(
            c => c.Estado == EstadoAsignacion.Vigente && c.Ambito.EsUniversal && c.Rol == Roles.GestorCae && c.EsPrincipal);
    }

    [Fact]
    public async Task Asegurar_la_cartera_de_un_Gestor_CAE_del_propio_Tenant_cuelga_de_la_raiz_sin_rol_propio()
    {
        await using (var contexto = CrearContexto(_propietario))
        {
            var rol = await contexto.Roles.SingleOrDefaultAsync(r => r.Name == Roles.GestorCae);
            if (rol is null)
            {
                rol = new Microsoft.AspNetCore.Identity.IdentityRole<Guid> { Id = Guid.NewGuid(), Name = Roles.GestorCae, NormalizedName = Roles.GestorCae.ToUpperInvariant() };
                contexto.Roles.Add(rol);
            }

            contexto.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = _gestorPropietario, RoleId = rol.Id });
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).AsegurarCarteraTenantEnteroAsync(_propietario, _gestorPropietario);
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto(_propietario);
        var raiz = await verificacion.AsignacionesOperacion.SingleAsync(o => o.EsRaiz && o.PropietarioTenantId == _propietario);
        var cartera = await verificacion.AsignacionesCartera.SingleAsync(c => c.UsuarioId == _gestorPropietario);
        cartera.AsignacionOperacionId.Should().Be(raiz.Id);
        cartera.Ambito.EsUniversal.Should().BeTrue();
        cartera.Rol.Should().BeNull("en su propio Tenant opera con el rol de Identity");
        cartera.EsPrincipal.Should().BeTrue("es la primera cartera de Gestor CAE de la raíz: nace principal");
    }

    // ---------- Marca de principal (ADR-011 § 2.7, enmienda 2026-10-08) ----------

    [Fact]
    public async Task La_primera_cartera_que_asegura_el_writer_nace_principal_y_la_segunda_de_apoyo()
    {
        await EjecutarBackfillAsync();
        var segundo = await AnadirSegundoGestorDelegadoAsync();

        // En un solo guardado, como una siembra: la segunda tiene que ver la primera aún sin guardar.
        await using (var contexto = CrearContexto(_propietario))
        {
            var writer = CrearWriter(contexto);
            await writer.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            await writer.AsegurarCarteraTenantEnteroAsync(_propietario, segundo);
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto(_propietario);
        var carteras = await verificacion.AsignacionesCartera.Where(c => c.Rol == Roles.GestorCae).ToListAsync();
        carteras.Single(c => c.UsuarioId == _gestorConsultora).EsPrincipal.Should().BeTrue();
        carteras.Single(c => c.UsuarioId == segundo).EsPrincipal.Should().BeFalse("ya había principal vivo bajo esa operación");
    }

    /// <summary>
    /// D-9 (2026-10-08): «al reactivar vuelve a ser principal quien lo era antes». La cascada de la
    /// desactivación deja escrito en la cartera cerrada quién llevaba la marca y la reactivación se la
    /// devuelve, haya uno o varios Gestores CAE. Una operación cerrada <b>antes</b> de que ese dato
    /// existiera no lo tiene: ahí rige la regla anterior (una sola cartera repuesta nace principal;
    /// varias, ninguna), y no se inventa un responsable.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Reactivar_devuelve_la_marca_a_quien_era_el_principal_y_sin_ese_dato_solo_marca_a_un_unico_Gestor_CAE(
        bool conDosGestores, bool cerradaAntesDeQueExistieraElDato)
    {
        await EjecutarBackfillAsync();
        var segundo = conDosGestores ? await AnadirSegundoGestorDelegadoAsync() : (Guid?)null;

        await using (var contexto = CrearContexto(_propietario))
        {
            var writer = CrearWriter(contexto);
            await writer.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            if (segundo is { } id) await writer.AsegurarCarteraTenantEnteroAsync(_propietario, id);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var desactivar = new DesactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new CurrentUserServiceFalso(tenantOrigenId: _consultora),
                CrearWriter(contexto), contexto);
            (await desactivar.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.AsignacionesCartera.CountAsync(c => c.EsPrincipal)).Should().Be(
                0, "control: la cascada cerró la principal y el cierre apaga la marca");
            (await contexto.AsignacionesCartera.Where(c => c.EraPrincipalAlCerrarsePorCascada).Select(c => c.UsuarioId).ToListAsync())
                .Should().Equal([_gestorConsultora], "control: la cascada deja escrito quién era el principal, y solo él");

            // Lo que dejó una desactivación anterior a la columna: carteras cerradas sin ese dato.
            if (cerradaAntesDeQueExistieraElDato)
                await contexto.AsignacionesCartera.ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.EraPrincipalAlCerrarsePorCascada, false));

            var reactivar = new ReactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new AutorizacionAdministradorDe(_propietario),
                new CurrentUserServiceFalso(Guid.NewGuid()),
                CrearWriter(contexto), contexto, contexto,
                new TransaccionDeComando(contexto),
                new CatalogoIncorporacionCartera(contexto, new CurrentUserServiceFalso(Guid.NewGuid())),
                new SinDirectorioDeDestinos(), new CuentasQueSiguenSiendoGestorCae(),
                new BloqueoCarteraUsuario(contexto));
            (await reactivar.Handle(new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto(_propietario);
        var repuestas = await verificacion.AsignacionesCartera
            .Where(c => c.Rol == Roles.GestorCae && c.Estado == EstadoAsignacion.Vigente).ToListAsync();
        repuestas.Should().HaveCount(conDosGestores ? 2 : 1, "control: la reactivación repone las carteras que la cascada cerró");

        var principales = repuestas.Where(c => c.EsPrincipal).Select(c => c.UsuarioId).ToList();
        if (cerradaAntesDeQueExistieraElDato && conDosGestores)
            principales.Should().BeEmpty("sin saber quién lo era y con varias carteras no se inventa un responsable");
        else
            principales.Should().Equal([_gestorConsultora],
                cerradaAntesDeQueExistieraElDato
                    ? "con una sola cartera repuesta esa responde del Tenant"
                    : "vuelve a ser principal quien lo era antes; las demás carteras vuelven sin marca");
    }

    /// <summary>
    /// Revisión puente de I2b. Con la delegación desactivada se puede revocar la fila de operador
    /// delegado de quien era el principal: su cartera ya no se repone. Eso es «ya no puede serlo», no
    /// «no había principal»: el Gestor CAE de apoyo que queda solo no hereda la marca por ser el único.
    /// </summary>
    [Fact]
    public async Task Si_quien_era_el_principal_ya_no_es_operador_delegado_el_Gestor_CAE_de_apoyo_que_queda_no_hereda_la_marca()
    {
        await EjecutarBackfillAsync();
        var apoyo = await AnadirSegundoGestorDelegadoAsync();

        await using (var contexto = CrearContexto(_propietario))
        {
            var writer = CrearWriter(contexto);
            await writer.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            await writer.AsegurarCarteraTenantEnteroAsync(_propietario, apoyo);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var desactivar = new DesactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new CurrentUserServiceFalso(tenantOrigenId: _consultora),
                CrearWriter(contexto), contexto);
            (await desactivar.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.AsignacionesCartera.Where(c => c.EraPrincipalAlCerrarsePorCascada).Select(c => c.UsuarioId).ToListAsync())
                .Should().Equal([_gestorConsultora], "control: el principal era él");
            (await contexto.AsignacionesOperadorDelegadoConRevocadas
                    .Where(a => a.DelegacionTenantId == _delegacionId && a.UsuarioId == _gestorConsultora)
                    .ExecuteDeleteAsync())
                .Should().Be(1, "control: había una fila que revocar");

            var reactivar = new ReactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new AutorizacionAdministradorDe(_propietario),
                new CurrentUserServiceFalso(Guid.NewGuid()),
                CrearWriter(contexto), contexto, contexto,
                new TransaccionDeComando(contexto),
                new CatalogoIncorporacionCartera(contexto, new CurrentUserServiceFalso(Guid.NewGuid())),
                new SinDirectorioDeDestinos(), new CuentasQueSiguenSiendoGestorCae(),
                new BloqueoCarteraUsuario(contexto));
            (await reactivar.Handle(new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto(_propietario);
        var repuestas = await verificacion.AsignacionesCartera
            .Where(c => c.Rol == Roles.GestorCae && c.Estado == EstadoAsignacion.Vigente).ToListAsync();
        repuestas.Select(c => c.UsuarioId).Should().Equal([apoyo], "control: solo se repone la cartera de quien sigue siendo operador delegado");
        repuestas.Should().OnlyContain(c => !c.EsPrincipal,
            "sin Coordinador CAE al que relevar la operación queda sin principal; nunca pasa al Gestor CAE de apoyo");
    }

    private async Task<Guid> AnadirSegundoGestorDelegadoAsync()
    {
        var segundo = Guid.NewGuid();
        await using var contexto = CrearContexto(_propietario);
        contexto.Users.Add(new ApplicationUser
        {
            Id = segundo,
            TenantId = _consultora,
            UserName = "g2@consultora",
            Email = "g2@consultora"
        });
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(_delegacionId, segundo, Roles.GestorCae));
        await contexto.SaveChangesAsync();
        return segundo;
    }

    [Fact]
    public async Task Asegurar_la_cartera_a_un_usuario_del_propio_Tenant_que_no_es_Gestor_CAE_falla()
    {
        // _gestorPropietario no tiene ningún rol de Identity en la base de este test.
        await using var contexto = CrearContexto(_propietario);

        await CrearWriter(contexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorPropietario))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Asegurar_la_cartera_de_otro_Tenant_que_no_es_el_del_contexto_falla()
    {
        await using var contexto = CrearContexto(_propietario);

        await CrearWriter(contexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_consultora, _gestorConsultora))
            .Should().ThrowAsync<InvalidOperationException>("el acto ocurre dentro del workspace del propietario");
    }

    // ---------- D3: la escritura falla, no se degrada en silencio ----------

    [Fact]
    public async Task Asegurar_la_cartera_a_un_usuario_inexistente_falla_en_vez_de_dejarla_sin_escribir()
    {
        await using var contexto = CrearContexto(_propietario);

        await CrearWriter(contexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_propietario, Guid.NewGuid()))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Asegurar_la_cartera_sin_operacion_donde_colgarla_falla()
    {
        await using var contexto = CrearContexto(_propietario);

        // El gestor de la consultora sin operación externa vigente: no hay dónde colgar su cartera y la
        // escritura no puede fingir que la hizo.
        await CrearWriter(contexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // ---------- B4: desactivar → reactivar → acceso ----------

    [Fact]
    public async Task El_ciclo_desactivar_reactivar_devuelve_el_acceso_del_operador()
    {
        await EjecutarBackfillAsync();
        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            await contexto.SaveChangesAsync();
        }

        // Estado inicial: el gestor delegado, con su cartera explícita, ve el Tenant entero.
        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEquivalentTo([_clienteId]);

        // Desactivar cierra la operación y sus carteras en cascada.
        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = new DesactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new CurrentUserServiceFalso(tenantOrigenId: _consultora),
                CrearWriter(contexto), contexto);

            var resultado = await handler.Handle(
                new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty("revocada la delegación no ve nada");

        // Reactivar tiene que devolver operación Y carteras. Antes solo abría
        // la operación: el operador entraba al workspace y no veía un dato.
        await using (var contexto = CrearContexto(_propietario))
        {
            // Reactivar exige ser Administrador del Cliente Delegante, no de la
            // Consultora: restaurar acceso es potestad de quien lo concede.
            // Lo que este test comprueba es otra cosa —que reactivar devuelva
            // también las carteras—, así que entra con la autoridad correcta.
            var handler = new ReactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new AutorizacionAdministradorDe(_propietario),
                new CurrentUserServiceFalso(Guid.NewGuid()),
                CrearWriter(contexto), contexto, contexto,
                new TransaccionDeComando(contexto),
                new CatalogoIncorporacionCartera(contexto, new CurrentUserServiceFalso(Guid.NewGuid())),
                new SinDirectorioDeDestinos(), new CuentasQueSiguenSiendoGestorCae(),
                new BloqueoCarteraUsuario(contexto));

            var resultado = await handler.Handle(
                new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync())
            .Should().BeEquivalentTo([_clienteId], "reactivar devuelve el acceso que tenía");
    }

    [Fact]
    public async Task Reactivar_no_da_cartera_a_quien_solo_tiene_la_fila_de_operador_ni_a_quien_es_la_referencia()
    {
        // El gestor delegado es la referencia de _clienteId y tiene su fila de operador delegado, pero
        // nunca tuvo una Asignación de Cartera. Desactivar y reactivar la delegación no se la inventa:
        // reponer solo repone lo que existía como concesión explícita (D-7).
        await EjecutarBackfillAsync();
        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty();

        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = new DesactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new CurrentUserServiceFalso(tenantOrigenId: _consultora),
                CrearWriter(contexto), contexto);
            (await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = new ReactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new AutorizacionAdministradorDe(_propietario),
                new CurrentUserServiceFalso(Guid.NewGuid()),
                CrearWriter(contexto), contexto, contexto,
                new TransaccionDeComando(contexto),
                new CatalogoIncorporacionCartera(contexto, new CurrentUserServiceFalso(Guid.NewGuid())),
                new SinDirectorioDeDestinos(), new CuentasQueSiguenSiendoGestorCae(),
                new BloqueoCarteraUsuario(contexto));
            (await handler.Handle(new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty("sin cartera previa, reactivar no la crea");
        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesCartera.AnyAsync(c => c.UsuarioId == _gestorConsultora)).Should().BeFalse();
    }

    [Fact]
    public async Task Reactivar_no_repone_la_cartera_que_se_revoco_a_ese_operador_antes_de_desactivar()
    {
        // La cartera de este Gestor CAE se le retiró a él solo (cerrada Revocada con la operación aún vigente).
        // Después se desactiva y se reactiva la delegación: la reposición es de lo que cerró la cascada, no de
        // todo lo que alguna vez se revocó. Sin esto, quien perdió la cartera por decisión explícita la
        // recuperaba sin que nadie lo decidiera.
        await EjecutarBackfillAsync();
        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).CerrarCarteraOperadorAsync(
                _propietario, _consultora, _gestorConsultora, MotivoCierreAsignacion.Revocada);
            await contexto.SaveChangesAsync();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty("control: la cartera quedó revocada");

        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = new DesactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new CurrentUserServiceFalso(tenantOrigenId: _consultora),
                CrearWriter(contexto), contexto);
            (await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = new ReactivarDelegacionTenantCommandHandler(
                new DelegacionTenantRepository(contexto),
                new AutorizacionAdministradorDe(_propietario),
                new CurrentUserServiceFalso(Guid.NewGuid()),
                CrearWriter(contexto), contexto, contexto,
                new TransaccionDeComando(contexto),
                new CatalogoIncorporacionCartera(contexto, new CurrentUserServiceFalso(Guid.NewGuid())),
                new SinDirectorioDeDestinos(), new CuentasQueSiguenSiendoGestorCae(),
                new BloqueoCarteraUsuario(contexto));
            (await handler.Handle(new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty("lo revocado a un operador concreto no se repone al reactivar");
    }

    // ---------- O1: revalidación al activar una programada ----------

    [Fact]
    public async Task Una_programada_que_choca_con_la_vigente_se_queda_programada_y_no_tumba_el_lote()
    {
        var ahora = DateTime.UtcNow;
        Guid programadaQueChoca;
        Guid programadaLimpia;

        await using (var contexto = CrearContexto(_propietario))
        {
            // Ya hay una raíz vigente del propietario (la creó InitializeAsync).
            // Esta programada apunta al mismo hueco del índice único.
            var choca = AsignacionOperacion.Raiz(
                _propietario, ServicioCae.Outbound, ahora.AddMinutes(-1), ahora.AddMinutes(-2));
            contexto.AsignacionesOperacion.Add(choca);

            var limpia = AsignacionOperacion.Externa(
                _propietario, _consultora, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddMinutes(-1), null, ahora.AddMinutes(-2));
            contexto.AsignacionesOperacion.Add(limpia);

            await contexto.SaveChangesAsync();
            programadaQueChoca = choca.Id;
            programadaLimpia = limpia.Id;

            choca.Estado.Should().Be(EstadoAsignacion.Programada);
            limpia.Estado.Should().Be(EstadoAsignacion.Programada);
        }

        await EjecutarExpiracionAsync();

        await using var verificacion = CrearContexto(_propietario);

        // La que choca se queda como estaba, sin excepción que escape.
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == programadaQueChoca))
            .Estado.Should().Be(EstadoAsignacion.Programada);

        // Y la que no choca se activa igualmente: un choque no puede tumbar el
        // lote entero y dejar el job reintentando cada hora sin avanzar.
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == programadaLimpia))
            .Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    // ---------- Auditoría Módulo 5: rol delegado falla cerrado ----------

    [Fact]
    public async Task Asegurar_la_cartera_a_un_usuario_del_operador_sin_asignacion_delegada_falla_en_vez_de_heredar_GestorCae()
    {
        // Antes de la corrección, ObtenerRolDelegadoAsync devolvía
        // Roles.GestorCae cuando no encontraba fila: cualquier usuario del
        // tenant operador con una operación externa vigente entraba al tenant
        // propietario, aunque nadie lo hubiera dado de alta como operador
        // delegado. El backfill deja la operación externa vigente.
        await EjecutarBackfillAsync();

        var otroUsuarioConsultora = Guid.NewGuid();
        await using (var contexto = CrearContexto(_propietario))
        {
            contexto.Users.Add(new ApplicationUser
            {
                Id = otroUsuarioConsultora,
                TenantId = _consultora,
                UserName = "otro@consultora",
                Email = "otro@consultora"
            });
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            await CrearWriter(contexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_propietario, otroUsuarioConsultora))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }

        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesCartera.AnyAsync(c => c.UsuarioId == otroUsuarioConsultora))
            .Should().BeFalse("el fallo cerrado no puede dejar escrita una cartera para un usuario sin autorizar");
    }

    [Fact]
    public async Task Asegurar_la_cartera_a_un_operador_de_una_delegacion_desactivada_falla_aunque_la_operacion_siga_vigente()
    {
        // ObtenerRolDelegadoAsync comprueba Activa por sí mismo, sin depender
        // de que la operación externa ya se haya cerrado en cascada — las dos
        // comprobaciones son capas independientes, no una la garantía de la
        // otra (ver AbrirCarteraOperadorAsync/CerrarOperacionDelegadaAsync).
        //
        // La operación externa se crea a mano, sin pasar por el backfill, que
        // no la dejaría vigente con la delegación desactivada.
        var ahora = DateTime.UtcNow;
        await using (var contexto = CrearContexto(_propietario))
        {
            contexto.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _propietario, _consultora, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddMinutes(-5), null, ahora.AddMinutes(-5)));

            var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacionId);
            delegacion.Desactivar();

            await contexto.SaveChangesAsync();
        }

        await using var verificacionContexto = CrearContexto(_propietario);

        await CrearWriter(verificacionContexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ---------- utilidades ----------

    private async Task<IReadOnlyList<Guid>?> ClientesVisiblesParaElGestorDelegadoAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        var alcance = new AlcanceDatosService(
            contexto,
            new CurrentUserServiceFalso(_gestorConsultora, Roles.GestorCae, tenantOrigenId: _consultora),
            new TenantActualAmbiental { TenantId = _propietario },
            new SesionPrivilegiadaAusente());

        return await alcance.ObtenerClienteIdsVisiblesAsync();
    }

    private AsignacionesOperativasWriter CrearWriter(CaeManagerDbContext contexto) =>
        new(contexto, new TenantActualAmbiental { TenantId = _propietario },
            new CurrentUserServiceFalso(_gestorConsultora, Roles.Administrador, tenantOrigenId: _consultora));

    private async Task EjecutarBackfillAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        await CaeManager.Infrastructure.Persistence.Seed.AsignacionesOperativasBackfillSeeder
            .SeedAsync(contexto, NullLogger.Instance);
    }

    private async Task EjecutarExpiracionAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        await ExpiracionAsignacionesHostedService.ProcesarParaPruebasAsync(
            contexto, NullLogger.Instance, CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Estos tests miden qué carteras repone la reactivación, con el contexto del propietario de la base.
    /// La cuenta del anterior principal se da por válida aquí; que una cuenta desactivada o de otro rol no
    /// recupere la marca, y el relevo, se prueban con Identity real en <c>PrincipalDeCarteraBajoRuntimeTests</c>.
    /// </summary>
    private sealed class CuentasQueSiguenSiendoGestorCae : Application.Common.IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<bool> EsCuentaActivaConRolAsync(
            Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(rol == Roles.GestorCae);

        // Sin nadie en los perfiles de escalado: sin Coordinador CAE la operación queda sin principal.
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(
            Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class SinDirectorioDeDestinos : Application.Clientes.IDirectorioDestinosCartera
    {
        public Task<Application.Clientes.DestinoCartera?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Application.Clientes.DestinoCartera?>(null);
    }

    /// <summary>
    /// Doble de <see cref="IAutorizacionDelegacionTenant"/> que responde como un
    /// <c>Administrador</c> del tenant indicado: la implementación real exige rol
    /// y pertenencia, así que solo autoriza cuando le preguntan por su tenant.
    /// </summary>
    private sealed class AutorizacionAdministradorDe(Guid tenant) : IAutorizacionDelegacionTenant
    {
        public Task<bool> PuedeGestionarDelegacionesAsync(
            Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default)
            => Task.FromResult(tenantClienteDeleganteId == tenant);
    }
}
