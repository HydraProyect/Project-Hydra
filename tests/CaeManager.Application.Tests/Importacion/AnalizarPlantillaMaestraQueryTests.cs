using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Importacion.Queries;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>El análisis de la plantilla maestra revela qué CIF e identidades existen: pasa por la misma autorización que confirmar.</summary>
public class AnalizarPlantillaMaestraQueryTests
{
    private sealed class ServicioFalso : IPlantillaMaestraService
    {
        public int Llamadas { get; private set; }

        public byte[] GenerarPlantilla() => [];

        public Task<PlanImportacionMaestraDto> AnalizarAsync(Stream archivo, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(new PlanImportacionMaestraDto(Guid.NewGuid(), [], [], [], [], [], [], []));
        }
    }

    private sealed class Usuario(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class Criterio(bool cierra) : ICriterioDeOutbound
    {
        public PapelActivo? PapelPreguntado { get; private set; }

        public Task<bool> AdmiteOutboundAsync(PapelActivo papel, CancellationToken cancellationToken = default) =>
            Task.FromResult(!cierra);

        public Task<bool> CierraPorSoloInboundAsync(PapelActivo papel, CancellationToken cancellationToken = default)
        {
            PapelPreguntado = papel;
            return Task.FromResult(cierra);
        }
    }

    private sealed class AdministradorDeOrigen : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(PapelActivo.TenantDeOrigen);
    }

    /// <summary>
    /// «Inbound sin Outbound»: el <c>YaExiste</c> del plan diría qué Empresas, Centros y Trabajadores tiene el Tenant, así que
    /// al Tenant solo Inbound con el papel de Tenant de origen se le niega antes de leer el archivo, y con el papel activo.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Al_Tenant_solo_Inbound_no_se_le_analiza_ni_se_consulta_nada(bool soloInbound)
    {
        var servicio = new ServicioFalso();
        var criterio = new Criterio(soloInbound);

        var r = await new AnalizarPlantillaMaestraQueryHandler(servicio, new AdministradorDeOrigen(), criterio)
            .Handle(new AnalizarPlantillaMaestraQuery([1, 2, 3]), CancellationToken.None);

        criterio.PapelPreguntado.Should().Be(PapelActivo.TenantDeOrigen);
        r.EsExitoso.Should().Be(!soloInbound);
        servicio.Llamadas.Should().Be(soloInbound ? 0 : 1);
        if (soloInbound) r.Error.Codigo.Should().Be("Autorizacion.ComandoDeOutboundSinLineaOutbound");
    }
    [Fact]
    public async Task Sin_ser_Administrador_no_se_lee_el_archivo_ni_se_consulta_nada()
    {
        var servicio = new ServicioFalso();

        var r = await new AnalizarPlantillaMaestraQueryHandler(servicio, new Usuario("Gestor CAE"), new Criterio(false))
            .Handle(new AnalizarPlantillaMaestraQuery([1, 2, 3]), CancellationToken.None);

        r.EsExitoso.Should().BeFalse();
        servicio.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_Administrador_obtiene_el_plan()
    {
        var servicio = new ServicioFalso();

        var r = await new AnalizarPlantillaMaestraQueryHandler(servicio, new Usuario("Administrador"), new Criterio(false))
            .Handle(new AnalizarPlantillaMaestraQuery([1, 2, 3]), CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        servicio.Llamadas.Should().Be(1);
    }

    /// <summary>
    /// ADR-013 M2 (K-2): el handler dejó de preguntar a la autoridad de Inbound (<c>AutorizacionLecturaAutoridad</c>) y
    /// compara él mismo el rol efectivo. Para cada rol, el resultado tiene que ser el que daba aquel predicado: solo
    /// «Administrador», exacto, concede; un rol efectivo nulo (Sesión Privilegiada de plataforma) no.
    /// </summary>
    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("administrador", false)]
    [InlineData("Administrador ", false)]
    [InlineData("DireccionCae", false)]
    [InlineData("CoordinadorCae", false)]
    [InlineData("GestorCae", false)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task Solo_el_rol_efectivo_Administrador_analiza_como_decidia_la_autoridad_de_Inbound(string? rol, bool concede)
    {
        var servicio = new ServicioFalso();
        var usuario = new Usuario(rol);

        var r = await new AnalizarPlantillaMaestraQueryHandler(servicio, usuario, new Criterio(false))
            .Handle(new AnalizarPlantillaMaestraQuery([1, 2, 3]), CancellationToken.None);

        r.EsExitoso.Should().Be(concede);
        servicio.Llamadas.Should().Be(concede ? 1 : 0);
        (await CaeManager.Application.Inbound.AutorizacionesLectura.AutorizacionLecturaAutoridad.EsAdministradorAsync(usuario))
            .Should().Be(concede, "el predicado de antes de M2 decide lo mismo para este rol");
        if (!concede) r.Error.Codigo.Should().Be("Importacion.SoloAdministrador");
    }
}
