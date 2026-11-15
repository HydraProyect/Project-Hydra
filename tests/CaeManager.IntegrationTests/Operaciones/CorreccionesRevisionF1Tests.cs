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
    private readonly Guid _direccionConsultora = Guid.NewGuid();
    private readonly Guid _gestorPropietario = Guid.NewGuid();
    private readonly Guid _administradorPropietario = Guid.NewGuid();
    private Guid _consultora;
    private Guid _propietario;
    private Guid _clienteId;
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var inicial = CrearContexto(Guid.NewGuid());
        await inicial.Database.MigrateAsync();

        var consultora = new Tenant("Consultora F1", PerfilVocabularioTenant.Consultora);
        consultora.HabilitarComoOperadorCaeExterno();
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

        // La Asignación de Operación externa del Tenant entero la abre el alta de la delegación (el acto explícito),
        // con su origen sellado: desde la PR #2237 el backfill ya no crea operaciones externas (Codex r2, alta 5).
        var desde = DateTime.UtcNow.AddMinutes(-1);
        OperacionExternaAmparada.Agregar(contexto, AsignacionOperacion.Externa(
            _propietario, _consultora, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, desde), delegacion);

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

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().HaveCount(3, "el Tenant entero: los tres Clientes");
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
    /// Decisión de Chris del 2026-11-14 (opción A en la PR #2223), que supera D-9 («al reactivar vuelve
    /// a ser principal quien lo era antes»): reactivar la delegación no repone ninguna Asignación de
    /// Cartera de las que cerró la cascada de la desactivación, ni la marca de principal, haya uno o
    /// varios Gestores CAE. La cascada sigue escribiendo quién llevaba la marca, como histórico sin lector.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reactivar_no_repone_ninguna_cartera_ni_la_marca_de_principal(bool conDosGestores)
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
            var desactivar = CrearDesactivar(contexto);
            (await desactivar.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.AsignacionesCartera.CountAsync(c => c.EsPrincipal)).Should().Be(
                0, "control: la cascada cerró la principal y el cierre apaga la marca");
            (await contexto.AsignacionesCartera.Where(c => c.EraPrincipalAlCerrarsePorCascada).Select(c => c.UsuarioId).ToListAsync())
                .Should().Equal([_gestorConsultora], "control: la cascada deja escrito quién era el principal, y solo él");

            var reactivar = CrearReactivar(contexto);
            (await reactivar.Handle(new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesOperacion.CountAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario
                && o.Servicio == ServicioCae.Outbound && o.Estado == EstadoAsignacion.Vigente))
            .Should().Be(1, "control: la reactivación reabre la Asignación de Operación del Tenant entero");
        (await verificacion.AsignacionesCartera
                .CountAsync(c => c.Rol == Roles.GestorCae && c.Estado != EstadoAsignacion.Cerrada))
            .Should().Be(0, "reactivar no repone ninguna cartera de Gestor CAE (opción A)");
        (await verificacion.AsignacionesCartera.CountAsync(c => c.EsPrincipal))
            .Should().Be(0, "ni la marca de principal");
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
    public async Task El_ciclo_desactivar_reactivar_no_devuelve_el_acceso_del_operador_sin_un_acto_explicito()
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
            var handler = CrearDesactivar(contexto);

            var resultado = await handler.Handle(
                new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync()).Should().BeEmpty("revocada la delegación no ve nada");

        // Reactivar devuelve la Asignación de Operación pero ninguna cartera (decisión de Chris del
        // 2026-11-14, opción A en la PR #2223): el operador no ve nada hasta que un acto explícito se la
        // vuelva a conceder. Antes reactivar reponía las carteras que había cerrado la cascada.
        await using (var contexto = CrearContexto(_propietario))
        {
            // Reactivar exige ser Administrador del Tenant propietario (decisión
            // SEG-7 del 2026-10-31); aquí se entra con la autoridad correcta.
            var handler = CrearReactivar(contexto);

            var resultado = await handler.Handle(
                new ReactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync())
            .Should().BeEmpty("reactivar no repone la cartera que cerró la cascada");

        // Control positivo: los actos explícitos sí devuelven el acceso bajo la operación reabierta. La revocación
        // revocó también su fila heredada de Operador Delegado, y reactivar no la repone (la fila heredada falla
        // cerrada sin reposición, PR #2237): primero vuelve la fila, luego la cartera. La cartera la concede un acto
        // explícito sobre la operación reabierta: AsegurarCarteraTenantEnteroAsync es la vía de las siembras y no
        // emite a quien ya tuvo cartera en el par (FilaHeredadaPuedeEmitir).
        await using (var contexto = CrearContexto(_propietario))
        {
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacionId, _gestorConsultora, Roles.GestorCae));
            await contexto.SaveChangesAsync();
            var reabierta = await contexto.AsignacionesOperacion.SingleAsync(
                o => o.OrigenId == _delegacionId && o.Estado == EstadoAsignacion.Vigente);
            var ahora = DateTime.UtcNow;
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                reabierta, _gestorConsultora, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
            await contexto.SaveChangesAsync();
        }

        (await ClientesVisiblesParaElGestorDelegadoAsync())
            .Should().BeEquivalentTo([_clienteId], "con la cartera concedida de nuevo, vuelve el acceso");
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
            var handler = CrearDesactivar(contexto);
            (await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = CrearReactivar(contexto);
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
        // Después se desactiva y se reactiva la delegación: con la opción A (PR #2223) reactivar no repone
        // nada, y en particular no devuelve a quien perdió la cartera por decisión explícita.
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
            var handler = CrearDesactivar(contexto);
            (await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacionId), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = CrearContexto(_propietario))
        {
            var handler = CrearReactivar(contexto);
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

        // Desde la PR #2237 la operación que sobrevive a la revocación de su delegación de origen ya no es vigente
        // para el writer (AmparoDeOrigen): es lo primero que falla, antes de llegar a ObtenerRolDelegadoAsync, y la
        // revocación revoca además la fila heredada del gestor. Las capas siguen siendo independientes; aquí se
        // mide la que el orden alcanza primero, y que no queda escrita ninguna cartera. Sea cual sea el orden en
        // que EF emita el INSERT y el UPDATE del mismo guardado, la operación acaba sin amparo: sin origen (si la
        // delegación ya estaba inactiva al insertar) o con un contador sellado que la desactivación deja atrás.
        //
        // La rama Activa de ObtenerRolDelegadoAsync queda como defensa en profundidad que el esquema hace
        // inalcanzable sin desactivar triggers: una fila heredada solo nace con la delegación activa (BEFORE
        // INSERT), desactivarla revoca sus filas en la misma sentencia (AFTER UPDATE) y el amparo exige el
        // origen activo. Hueco declarado en la PR #2237 (Codex r2, media 10).
        await CrearWriter(verificacionContexto).Invoking(w => w.AsegurarCarteraTenantEnteroAsync(_propietario, _gestorConsultora))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tiene operación externa vigente*");
        (await verificacionContexto.AsignacionesCartera.AnyAsync(c => c.UsuarioId == _gestorConsultora))
            .Should().BeFalse("el fallo cerrado no deja escrita la cartera");
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
    /// Revoca quien puede: la Dirección CAE de la Consultora (el Operador CAE externo), que alcanza a toda la
    /// organización sin Asignación de Cartera propia. Quién puede y quién no se mide bajo RLS real en
    /// <c>AutorizarOperadorCaeExternoRlsTests</c>.
    /// </summary>
    private DesactivarDelegacionTenantCommandHandler CrearDesactivar(CaeManagerDbContext contexto) =>
        new(new DelegacionTenantRepository(contexto), DireccionDeLaConsultora(), CrearWriter(contexto), contexto,
            new TenantActualAmbiental { TenantId = _propietario }, new CuentasQueSiguenSiendoGestorCae(), contexto,
            new CatalogoIncorporacionCartera(contexto, new CurrentUserServiceFalso(Guid.NewGuid())));

    private ReactivarDelegacionTenantCommandHandler CrearReactivar(CaeManagerDbContext contexto) =>
        new(new DelegacionTenantRepository(contexto), AdministradorDelPropietario(),
            CrearWriter(contexto), contexto, contexto,
            new TransaccionDeComando(contexto), new TenantActualAmbiental { TenantId = _propietario },
            new CuentaAdministradoraEnBase());

    private CurrentUserServiceFalso DireccionDeLaConsultora() => new(_direccionConsultora, Roles.DireccionCae, _consultora);

    private CurrentUserServiceFalso AdministradorDelPropietario() => new(_administradorPropietario, Roles.Administrador, _propietario);

    /// <summary>Quién reactiva se mide bajo RLS real en <c>AutorizarOperadorCaeExternoRlsTests</c>; aquí, las carteras.</summary>
    private sealed class CuentaAdministradoraEnBase : IAutorizacionDelegacionTenant
    {
        public Task<bool> PuedeGestionarDelegacionesAsync(
            Guid usuarioId, Guid tenantClienteId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Estos tests miden qué carteras cierra la desactivación, con el contexto del propietario de la base.
    /// La cuenta de quien revoca (Dirección CAE) se da por válida aquí; el relevo del principal se prueba
    /// con Identity real en <c>PrincipalDeCarteraBajoRuntimeTests</c>.
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
            Task.FromResult(rol is Roles.GestorCae or Roles.DireccionCae);

        // Sin nadie en los perfiles de escalado: sin Coordinador CAE la operación queda sin principal.
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(
            Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
