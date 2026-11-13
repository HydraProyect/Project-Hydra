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

    private sealed class Usuario(string rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    [Fact]
    public async Task Sin_ser_Administrador_no_se_lee_el_archivo_ni_se_consulta_nada()
    {
        var servicio = new ServicioFalso();

        var r = await new AnalizarPlantillaMaestraQueryHandler(servicio, new Usuario("Gestor CAE"))
            .Handle(new AnalizarPlantillaMaestraQuery([1, 2, 3]), CancellationToken.None);

        r.EsExitoso.Should().BeFalse();
        servicio.Llamadas.Should().Be(0);
    }

    [Fact]
    public async Task Un_Administrador_obtiene_el_plan()
    {
        var servicio = new ServicioFalso();

        var r = await new AnalizarPlantillaMaestraQueryHandler(servicio, new Usuario("Administrador"))
            .Handle(new AnalizarPlantillaMaestraQuery([1, 2, 3]), CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        servicio.Llamadas.Should().Be(1);
    }
}
