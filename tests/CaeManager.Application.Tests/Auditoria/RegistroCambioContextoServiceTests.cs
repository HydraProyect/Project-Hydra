using System.Text.Json;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Auditoria;

/// <summary>
/// Auditoría del cambio de contexto (D-6 b, R-2; propuesta de buzones de varios Tenants § 5.5): solo en el Tenant de
/// destino, con el Actor real y el Usuario simulado por separado y sin el Tenant seleccionado antes. Este instrumento
/// ve en qué ámbito se guarda el registro; que la RLS lo acepte en ese Tenant lo prueba PostgreSQL, no este test.
/// </summary>
public class RegistroCambioContextoServiceTests
{
    private static readonly Guid ActorReal = Guid.NewGuid();
    private static readonly Guid Simulado = Guid.NewGuid();
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Destino = Guid.NewGuid();
    private static readonly Guid Operacion = Guid.NewGuid();

    private readonly RepositorioFalso _repositorio = new();

    [Fact]
    public async Task El_cambio_se_guarda_en_el_ambito_del_Tenant_de_destino_con_la_operacion_de_la_seleccion_nueva()
    {
        await Servicio(new ActorAuditoria(ActorReal, null, TipoViaAcceso.Normal, null))
            .RegistrarCambioAsync(Destino, Origen, Operacion, RegistroCambioContextoService.OrigenSelector, default);

        var (registro, ambito) = _repositorio.Guardados.Single();
        ambito.Should().Be(Destino, "el cambio se registra en el Tenant de destino y solo en él");
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull("el ámbito explícito no sobrevive a la llamada");
        registro.Accion.Should().Be(RegistroAuditoria.AccionCambioDeContexto);
        registro.EntidadTipo.Should().Be(RegistroCambioContextoService.EntidadTipo);
        registro.EntidadId.Should().Be(Destino);
        registro.UsuarioId.Should().Be(ActorReal);
        registro.ActorRealUsuarioId.Should().Be(ActorReal);
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.OperacionDelegada);
        registro.ViaAccesoId.Should().Be(Operacion);

        using var datos = JsonDocument.Parse(registro.DatosDespues!);
        datos.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["origen", "tenantOrigenActor"],
            "el registro no nombra el Tenant seleccionado antes: diría para qué otras Empresas gestionadas trabaja");
        datos.RootElement.GetProperty("origen").GetString().Should().Be(RegistroCambioContextoService.OrigenSelector);
        datos.RootElement.GetProperty("tenantOrigenActor").GetGuid().Should().Be(Origen);
    }

    [Fact]
    public async Task Con_Usuario_simulado_el_autor_y_el_Actor_real_quedan_separados()
    {
        await Servicio(new ActorAuditoria(ActorReal, Simulado, TipoViaAcceso.SesionPrivilegiada, Guid.NewGuid()))
            .RegistrarCambioAsync(Destino, Origen, Operacion, RegistroCambioContextoService.OrigenSelector, default);

        var registro = _repositorio.Guardados.Single().Registro;
        registro.UsuarioId.Should().Be(Simulado);
        registro.ActorRealUsuarioId.Should().Be(ActorReal);
    }

    [Fact]
    public async Task La_vuelta_al_origen_o_la_via_heredada_registran_via_normal_sin_operacion()
    {
        await Servicio(new ActorAuditoria(ActorReal, null, TipoViaAcceso.OperacionDelegada, Operacion))
            .RegistrarCambioAsync(Origen, Origen, null, RegistroCambioContextoService.OrigenSelector, default);

        var (registro, ambito) = _repositorio.Guardados.Single();
        ambito.Should().Be(Origen);
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.Normal, "la vía es la de la selección nueva, no la que se deja");
        registro.ViaAccesoId.Should().BeNull();
    }

    [Fact]
    public async Task Si_el_cambio_no_se_puede_registrar_la_excepcion_corta_el_cambio()
    {
        _repositorio.Lanzar = true;

        var registrar = () => Servicio(new ActorAuditoria(ActorReal, null, TipoViaAcceso.Normal, null))
            .RegistrarCambioAsync(Destino, Origen, Operacion, RegistroCambioContextoService.OrigenSelector, default);

        await registrar.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task El_rechazo_se_registra_en_el_Tenant_en_curso_con_el_comando_y_el_motivo()
    {
        var sesion = Guid.NewGuid();
        await Servicio(new ActorAuditoria(ActorReal, Simulado, TipoViaAcceso.SesionPrivilegiada, sesion))
            .RegistrarRechazoAsync("CrearVisitaCommand", SeleccionNoVigente.CambiadaEnOtraPestana, default);

        var (registro, ambito) = _repositorio.Guardados.Single();
        ambito.Should().BeNull("el rechazo no abre ningún ámbito: queda en el Tenant en el que corre la pestaña");
        registro.Accion.Should().Be(RegistroAuditoria.AccionComandoRechazadoPorContextoObsoleto);
        registro.UsuarioId.Should().Be(Simulado);
        registro.ActorRealUsuarioId.Should().Be(ActorReal);
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        registro.ViaAccesoId.Should().Be(sesion);
        using var datos = JsonDocument.Parse(registro.DatosDespues!);
        datos.RootElement.GetProperty("comando").GetString().Should().Be("CrearVisitaCommand");
        datos.RootElement.GetProperty("motivo").GetString().Should().Be(nameof(SeleccionNoVigente.CambiadaEnOtraPestana));
    }

    [Fact]
    public async Task Un_fallo_al_registrar_el_rechazo_no_lo_convierte_en_excepcion()
    {
        _repositorio.Lanzar = true;

        var registrar = () => Servicio(new ActorAuditoria(ActorReal, null, TipoViaAcceso.Normal, null))
            .RegistrarRechazoAsync("CrearVisitaCommand", SeleccionNoVigente.RetiradaTrasPintar, default);

        await registrar.Should().NotThrowAsync();
    }

    private RegistroCambioContextoService Servicio(ActorAuditoria actor) =>
        new(new ActorFijo(actor), _repositorio, NullLogger<RegistroCambioContextoService>.Instance);

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class RepositorioFalso : IRegistroAccesoDatoSensibleRepository
    {
        public bool Lanzar { get; set; }

        public List<(RegistroAuditoria Registro, Guid? Ambito)> Guardados { get; } = [];

        public Task GuardarAsync(RegistroAuditoria registro, CancellationToken cancellationToken = default)
        {
            if (Lanzar) throw new InvalidOperationException("Guardado fallido de prueba.");
            Guardados.Add((registro, AmbitoTenantExplicito.TenantIdActual));
            return Task.CompletedTask;
        }
    }
}
