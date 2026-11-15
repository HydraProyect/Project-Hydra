using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// El filtro de expansión (<c>AmparoDeOrigen.SinOrigenOAmparadasPorSuOrigen</c>) en cada lector de alcance de
/// Infrastructure, leído como <c>cae_app_runtime</c>. Todas las pruebas usan el mismo estado discriminante: la
/// DelegacionTenant de origen se revoca y se reactiva con el SQL de la ranura anterior (solo <c>Activa</c>, sin cerrar
/// operaciones), así que la delegación vuelve a estar activa y sin caducar y la operación sigue vigente, pero su
/// contador de revocaciones ya no es el que guardó al nacer. En ese estado el resto del predicado de cada lector (el
/// que tenía en main, incluida la comprobación de una delegación activa del par donde la hay) deja pasar la operación:
/// solo el filtro de origen la quita. Por eso cada prueba falla si se borra el filtro de su lector, y cada una lleva un
/// control de que lo demás no cambió (la operación sigue vigente o una operación sin origen sigue contando).
/// </summary>
public partial class OrigenDeOperacionExternaBajoRuntimeTests
{
    /// <summary>
    /// <c>AlcanceDatosService.ObtenerCarteraAsync</c>, por <c>ObtenerClienteIdsVisiblesAsync</c> de la Gestora CAE del
    /// Operador CAE externo dentro del Tenant propietario. Sin el filtro, <c>delegacionVigente</c> es cierto (la
    /// delegación vuelve a estar activa) y las dos operaciones están vigentes: el alcance tendría los dos Clientes
    /// empresariales.
    /// </summary>
    [Fact]
    public async Task El_alcance_de_cartera_no_cuenta_la_operacion_cuyo_origen_se_revoco_y_reactivo()
    {
        var conOrigen = await SembrarAcotadaConCarteraAsync();
        (await ClientesVisiblesDeLaGestoraAsync()).Should().BeEquivalentTo([_cliente], "control: con su origen vigente cuenta");

        var (sinOrigen, clienteSinOrigen) = await RevocarSembrarSinOrigenYReactivarAsync();
        await AfirmarVigentesAsync(conOrigen, sinOrigen);

        (await ClientesVisiblesDeLaGestoraAsync()).Should().BeEquivalentTo([clienteSinOrigen],
            "la operación con origen se revocó después de nacer; la sin origen, con la delegación del par activa, sigue como antes");
    }

    /// <summary>
    /// <c>DirectorioUsuariosTenant.ObtenerCarterasVigentesAsync</c> (columna «Ámbito efectivo» de <c>/usuarios</c> en el
    /// Tenant propietario). Este lector no mira ninguna delegación: sin el filtro pintaría los dos Clientes empresariales.
    /// </summary>
    [Fact]
    public async Task Las_carteras_vigentes_del_directorio_no_pintan_la_operacion_cuyo_origen_se_revoco_y_reactivo()
    {
        var conOrigen = await SembrarAcotadaConCarteraAsync();
        (await CarterasVigentesEnElPropietarioAsync())[_gestoraOperador.Id].ClienteIds
            .Should().BeEquivalentTo([_cliente], "control: con su origen vigente se pinta");

        var (sinOrigen, clienteSinOrigen) = await RevocarSembrarSinOrigenYReactivarAsync();
        await AfirmarVigentesAsync(conOrigen, sinOrigen);

        (await CarterasVigentesEnElPropietarioAsync())[_gestoraOperador.Id].ClienteIds
            .Should().BeEquivalentTo([clienteSinOrigen]);
    }

    /// <summary>
    /// <c>DirectorioUsuariosTenant.TieneVinculoOperativoAsync</c>: la única cartera de la Gestora CAE cuelga de una
    /// operación con origen. Este lector no mira ninguna delegación y la Gestora no tiene otro vínculo (ni Operador
    /// Delegado, ni línea, ni buzón, ni pool: lo prueba el control), así que sin el filtro seguiría siendo cierto.
    /// </summary>
    [Fact]
    public async Task El_vinculo_operativo_no_lo_da_una_cartera_bajo_una_operacion_cuyo_origen_se_revoco_y_reactivo()
    {
        var conOrigen = await SembrarAcotadaConCarteraAsync();
        (await TieneVinculoOperativoEnElPropietarioAsync()).Should().BeTrue("control: su cartera es el único vínculo");

        await RevocarYReactivarPorLaRanuraAnteriorAsync();
        await AfirmarVigentesAsync(conOrigen);

        (await TieneVinculoOperativoEnElPropietarioAsync()).Should().BeFalse();
    }

    /// <summary>
    /// <c>ContextoOperativoConversacionService.ResolverAsync</c>: para un Cliente empresarial sin operación acotada, la
    /// única candidata externa es la del Tenant entero que abrió el alta, que nace con su origen. Este lector no mira
    /// ninguna delegación: sin el filtro seguiría devolviéndola y el hilo lo operaría el Operador CAE externo.
    /// </summary>
    [Fact]
    public async Task El_contexto_operativo_del_hilo_no_resuelve_la_operacion_cuyo_origen_se_revoco_y_reactivo()
    {
        var delTenantEntero = await OperacionDelTenantEnteroAsync();
        (await ResolverContextoOperativoAsync()).Should().Be(new OperacionDeConversacion(_operador, delTenantEntero),
            "control: con su origen vigente, la operación del Tenant entero opera el hilo");

        await RevocarYReactivarPorLaRanuraAnteriorAsync();
        await AfirmarVigentesAsync(delTenantEntero);

        ((await ResolverContextoOperativoAsync())?.OperadorCaeTenantId).Should().NotBe(_operador,
            "sin amparo de su origen, el Operador CAE externo no opera el hilo");
    }

    /// <summary>
    /// <c>ContextoOperativoConversacionService.AvisarCriticaAsync</c> revalida la operación antes de avisar. El control
    /// lo demuestra por el error: con el origen vigente pasa la revalidación y se detiene después, porque el Operador CAE
    /// externo no tiene Coordinador CAE ni Dirección CAE. Tras revocar y reactivar, se detiene antes, en la revalidación;
    /// sin el filtro llegaría otra vez a buscar destinatarios.
    /// </summary>
    [Fact]
    public async Task El_aviso_critico_no_sale_hacia_una_operacion_cuyo_origen_se_revoco_y_reactivo()
    {
        var operacion = new OperacionDeConversacion(_operador, await OperacionDelTenantEnteroAsync());
        (await AvisarCriticaAsync(operacion)).Should().Contain("destinatarios",
            "control: con su origen vigente, la operación pasa la revalidación");

        await RevocarYReactivarPorLaRanuraAnteriorAsync();
        await AfirmarVigentesAsync(operacion.AsignacionOperacionId);

        (await AvisarCriticaAsync(operacion)).Should().Contain("ya no está vigente");
    }

    // ── Medida de los lectores ───────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>?> ClientesVisiblesDeLaGestoraAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var gestora = FijarSesion(_gestoraOperador, Roles.GestorCae);
        using (AmbitoTenantExplicito.Establecer(_propietario))
            return await new AlcanceDatosService(contexto, gestora, servicios.GetRequiredService<ITenantActual>(),
                new SesionPrivilegiadaAusente()).ObtenerClienteIdsVisiblesAsync();
    }

    private async Task<IReadOnlyDictionary<Guid, CarteraDeUsuario>> CarterasVigentesEnElPropietarioAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var administrador = FijarSesion(_administradorPropietario, Roles.Administrador);
        using (AmbitoTenantExplicito.Establecer(_propietario))
            return await Directorio(servicios, contexto, servicios.GetRequiredService<ITenantActual>(), administrador)
                .ObtenerCarterasVigentesAsync();
    }

    private async Task<bool> TieneVinculoOperativoEnElPropietarioAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var administrador = FijarSesion(_administradorPropietario, Roles.Administrador);
        using (AmbitoTenantExplicito.Establecer(_propietario))
            return await Directorio(servicios, contexto, servicios.GetRequiredService<ITenantActual>(), administrador)
                .TieneVinculoOperativoAsync(_gestoraOperador.Id);
    }

    private async Task<OperacionDeConversacion?> ResolverContextoOperativoAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        FijarSesion(_administradorPropietario, Roles.Administrador);
        using (AmbitoTenantExplicito.Establecer(_propietario))
            return await ContextoOperativo(ambito.ServiceProvider).ResolverAsync(_propietario, _cliente, null, CancellationToken.None);
    }

    /// <summary>El mensaje del <see cref="InvalidOperationException"/> con que se detiene el aviso.</summary>
    private async Task<string> AvisarCriticaAsync(OperacionDeConversacion operacion)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        FijarSesion(_administradorPropietario, Roles.Administrador);
        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            var aviso = async () => await ContextoOperativo(ambito.ServiceProvider)
                .AvisarCriticaAsync(operacion, Guid.NewGuid(), CancellationToken.None);
            return (await aviso.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        }
    }

    /// <summary>
    /// Ninguna de las dos ramas que se miden llega a escribir avisos: el repositorio de notificaciones y la unidad de
    /// trabajo van a null para que, si alguna llegara, la prueba falle ruidosamente.
    /// </summary>
    private static ContextoOperativoConversacionService ContextoOperativo(IServiceProvider servicios) =>
        new(servicios.GetRequiredService<CaeManagerDbContext>(), servicios.GetRequiredService<ITenantActual>(),
            servicios.GetRequiredService<IDirectorioUsuariosService>(), null!, null!);

    // ── Estado discriminante ─────────────────────────────────────────────────

    /// <summary>
    /// Revoca y reactiva la DelegacionTenant de origen como la ranura anterior: solo <c>Activa</c>. El trigger cuenta la
    /// revocación, y las operaciones que nacieron antes quedan sin amparo aunque la delegación vuelva a estar activa.
    /// </summary>
    private async Task RevocarYReactivarPorLaRanuraAnteriorAsync()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
    }

    /// <summary>
    /// Como <see cref="RevocarYReactivarPorLaRanuraAnteriorAsync"/>, pero mientras la delegación está revocada siembra
    /// otra operación acotada, a otro Cliente empresarial y con la cartera de la misma Gestora CAE: sin delegación activa
    /// el trigger no deduce su origen, y queda como una operación anterior a la migración.
    /// </summary>
    private async Task<(Guid Operacion, Guid Cliente)> RevocarSembrarSinOrigenYReactivarAsync()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");

        Guid cliente;
        await using (var sembrado = ContextoPropietarioDeLaBase(_propietario))
        {
            var empresa = Empresa.CrearComoCliente("Cliente sin origen S.A.", "A58818501", false, null, null);
            sembrado.Empresas.Add(empresa);
            await sembrado.SaveChangesAsync();
            cliente = empresa.Id;
        }

        var sinOrigen = await SembrarAcotadaConCarteraAsync(cliente);
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == sinOrigen)).OrigenId
            .Should().BeNull("control: nació con la delegación revocada, sin origen que deducir");
        return (sinOrigen, cliente);
    }

    /// <summary>
    /// Control de que la revocación de la ranura anterior no cerró nada: si hubiera cerrado la operación, el lector la
    /// excluiría por su estado y la prueba no mediría el filtro de origen.
    /// </summary>
    private async Task AfirmarVigentesAsync(params Guid[] operaciones)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.AsignacionesOperacion.Where(o => operaciones.Contains(o.Id)).Select(o => o.Estado).ToListAsync())
            .Should().HaveCount(operaciones.Length).And.OnlyContain(estado => estado == EstadoAsignacion.Vigente);
        (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion))
            .Should().Match<Domain.Tenants.DelegacionTenant>(d => d.Activa && d.Revocaciones == 1,
                "la delegación de origen vuelve a estar activa, con una revocación desde que nacieron las operaciones");
    }
}
