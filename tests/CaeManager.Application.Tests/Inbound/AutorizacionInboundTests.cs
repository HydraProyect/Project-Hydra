using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Inbound;

/// <summary>
/// La matriz de capacidades de <see cref="AutorizacionInbound"/> (B-03), con las decisiones de Chris del 2026-11-16: el
/// Administrador del Tenant TCE gestiona y valida en su propio Tenant además de gobernar; los perfiles de organización del
/// Operador CAE externo con papel Inbound entran sin Asignación de Cartera; Consulta solo lee; el lado del Tenant TEC es
/// de su Administrador, en su Tenant o con papel Outbound (DN-2). Todo lo demás falla cerrado.
/// </summary>
public class AutorizacionInboundTests
{
    private const string Admin = "Administrador";

    private sealed class Usuario(string? rol, PapelActivo papel) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
    }

    private static Task<bool> Tiene(CapacidadInbound capacidad, string? rol, PapelActivo papel) =>
        new AutorizacionInbound(new Usuario(rol, papel)).TieneAsync(capacidad, default);

    public static TheoryData<CapacidadInbound, string?, PapelActivo, bool> Matriz()
    {
        var datos = new TheoryData<CapacidadInbound, string?, PapelActivo, bool>();
        var escriben = new[] { Admin, "DireccionCae", "CoordinadorCae", "GestorCae" };
        foreach (var papel in new[] { PapelActivo.TenantDeOrigen, PapelActivo.Inbound })
        {
            foreach (var rol in escriben)
            {
                datos.Add(CapacidadInbound.LeerInbound, rol, papel, true);
                datos.Add(CapacidadInbound.GestionarInbound, rol, papel, true);
                datos.Add(CapacidadInbound.ValidarInbound, rol, papel, true);
            }

            datos.Add(CapacidadInbound.LeerInbound, "Consulta", papel, true);
            datos.Add(CapacidadInbound.GestionarInbound, "Consulta", papel, false);
            datos.Add(CapacidadInbound.ValidarInbound, "Consulta", papel, false);
            datos.Add(CapacidadInbound.LeerInbound, "Cliente", papel, false);
            datos.Add(CapacidadInbound.GestionarInbound, "Cliente", papel, false);
            datos.Add(CapacidadInbound.LeerInbound, null, papel, false);
        }

        // El lado del Tenant TCE no se opera con papel Outbound, sin papel ni con una operación que ya no autoriza.
        foreach (var papel in new[] { PapelActivo.Outbound, PapelActivo.SinPapel, PapelActivo.OperacionQueYaNoAutoriza })
        {
            datos.Add(CapacidadInbound.LeerInbound, Admin, papel, false);
            datos.Add(CapacidadInbound.GestionarInbound, Admin, papel, false);
            datos.Add(CapacidadInbound.ValidarInbound, "GestorCae", papel, false);
            datos.Add(CapacidadInbound.GobernarInbound, Admin, papel, false);
        }

        // Gobierno: solo el Administrador, en su propio Tenant; nunca el Operador CAE externo con papel Inbound.
        datos.Add(CapacidadInbound.GobernarInbound, Admin, PapelActivo.TenantDeOrigen, true);
        datos.Add(CapacidadInbound.GobernarInbound, Admin, PapelActivo.Inbound, false);
        datos.Add(CapacidadInbound.GobernarInbound, "DireccionCae", PapelActivo.TenantDeOrigen, false);
        datos.Add(CapacidadInbound.GobernarInbound, "GestorCae", PapelActivo.TenantDeOrigen, false);

        // Lado del Tenant TEC: su Administrador, en su Tenant o como Operador CAE externo que lo opera en Outbound.
        datos.Add(CapacidadInbound.ParticipanteTec, Admin, PapelActivo.TenantDeOrigen, true);
        datos.Add(CapacidadInbound.ParticipanteTec, Admin, PapelActivo.Outbound, true);
        datos.Add(CapacidadInbound.ParticipanteTec, Admin, PapelActivo.Inbound, false);
        datos.Add(CapacidadInbound.ParticipanteTec, Admin, PapelActivo.SinPapel, false);
        datos.Add(CapacidadInbound.ParticipanteTec, Admin, PapelActivo.OperacionQueYaNoAutoriza, false);
        datos.Add(CapacidadInbound.ParticipanteTec, "GestorCae", PapelActivo.TenantDeOrigen, false);
        datos.Add(CapacidadInbound.ParticipanteTec, "Consulta", PapelActivo.TenantDeOrigen, false);
        return datos;
    }

    [Theory]
    [MemberData(nameof(Matriz))]
    public async Task Cada_capacidad_segun_el_rol_y_el_papel(CapacidadInbound capacidad, string? rol, PapelActivo papel, bool esperado)
    {
        (await Tiene(capacidad, rol, papel)).Should().Be(esperado);
    }

    [Fact]
    public async Task Una_capacidad_desconocida_falla_cerrada()
    {
        (await Tiene((CapacidadInbound)99, Admin, PapelActivo.TenantDeOrigen)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_papel_conocido_ninguna_capacidad()
    {
        // La implementación por defecto de ICurrentUserService responde SinPapel.
        var autorizacion = new AutorizacionInbound(new SinPapelConocido());

        foreach (var capacidad in Enum.GetValues<CapacidadInbound>())
            (await autorizacion.TieneAsync(capacidad, default)).Should().BeFalse(capacidad.ToString());
    }

    private sealed class SinPapelConocido : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Admin);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(Admin);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
