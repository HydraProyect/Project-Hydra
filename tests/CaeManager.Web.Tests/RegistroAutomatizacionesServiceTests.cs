using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Infrastructure.Configuracion;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Sin fila sembrada, un trabajo debe seguir activo por defecto — condición
/// explícita al cablear <c>IngestaWhatsApp</c> en el gate de
/// <c>IngestaWebhookWhatsAppHostedService</c> (salud de plataforma, A-06):
/// si el fallback hubiera sido "inactivo", el despliegue habría apagado la
/// ingesta de WhatsApp en producción en silencio, sin que nadie tocara nada.
/// </summary>
public class RegistroAutomatizacionesServiceTests
{
    [Fact]
    public async Task Un_trabajo_sin_fila_previa_sigue_activo_por_defecto()
    {
        var servicio = new RegistroAutomatizacionesService(new EstadoAutomatizacionRepositorioFalso(), new UnitOfWorkFalso());

        var activo = await servicio.EstaActivoAsync("ingesta-webhook-whatsapp", CancellationToken.None);

        activo.Should().BeTrue();
    }

    [Fact]
    public async Task Un_trabajo_explicitamente_desactivado_no_esta_activo()
    {
        var repositorio = new EstadoAutomatizacionRepositorioFalso();
        var estado = new EstadoAutomatizacion("ingesta-webhook-whatsapp");
        estado.CambiarActivo(false);
        repositorio.Agregar(estado);
        var servicio = new RegistroAutomatizacionesService(repositorio, new UnitOfWorkFalso());

        var activo = await servicio.EstaActivoAsync("ingesta-webhook-whatsapp", CancellationToken.None);

        activo.Should().BeFalse();
    }

    private sealed class EstadoAutomatizacionRepositorioFalso : IEstadoAutomatizacionRepository
    {
        private readonly List<EstadoAutomatizacion> _estados = [];

        public void Agregar(EstadoAutomatizacion estado) => _estados.Add(estado);

        public Task<EstadoAutomatizacion?> ObtenerPorTrabajoAsync(string trabajoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_estados.FirstOrDefault(e => e.TrabajoId == trabajoId));

        public Task<IReadOnlyList<EstadoAutomatizacion>> ObtenerTodosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((IReadOnlyList<EstadoAutomatizacion>)_estados);
    }

    private sealed class UnitOfWorkFalso : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
