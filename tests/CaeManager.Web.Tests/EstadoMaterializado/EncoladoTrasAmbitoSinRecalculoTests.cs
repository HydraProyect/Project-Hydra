using CaeManager.Application.EstadoMaterializado;
using CaeManager.Infrastructure.EstadoMaterializado;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests.EstadoMaterializado;

/// <summary>
/// El ámbito «sin recálculo» del estado materializado va por <c>AsyncLocal</c>. Cerrarlo dentro de un método <c>async</c>
/// solo lo cierra en el contexto de ese método: el arranque (del que heredan los servicios de fondo) seguiría con el ámbito
/// abierto y el detector encolaría Tenants enteros en vez de recalcular en línea.
/// </summary>
public class EncoladoTrasAmbitoSinRecalculoTests
{
    [Fact]
    public async Task Cerrar_y_encolar_cierra_el_ambito_tambien_para_quien_lo_abrio()
    {
        var ambito = AmbitoSinRecalculoEstado.Abrir("prueba del cierre");
        Assert.True(AmbitoSinRecalculoEstado.Activo);

        // Sin Tenants tocados no abre ningún ámbito de servicios ni toca la base.
        using var servicios = new ServiceCollection().BuildServiceProvider();
        await EncoladoTrasAmbitoSinRecalculo.CerrarYEncolarAsync(
            ambito, servicios.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        Assert.False(AmbitoSinRecalculoEstado.Activo);
    }
}
