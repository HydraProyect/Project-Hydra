using CaeManager.Application.Gestiones;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Gestiones.Commands.RestaurarGestion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Gestiones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

public class CarteraDeGestionEnEscriturasTests
{
    [Theory]
    [InlineData("completar", false)]
    [InlineData("completar", true)]
    [InlineData("reabrir", false)]
    [InlineData("reabrir", true)]
    [InlineData("eliminar", false)]
    [InlineData("eliminar", true)]
    [InlineData("restaurar", false)]
    [InlineData("restaurar", true)]
    public async Task Escribir_una_gestion_exige_su_centro_en_la_cartera_de_gestion_aunque_sea_visible_para_leer(
        string operacion, bool enCarteraDeGestion)
    {
        var gestion = new Gestion(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        if (operacion == "restaurar") gestion.MarcarComoEliminado(Guid.NewGuid());
        if (operacion == "reabrir") gestion.Completar(DateTime.UtcNow);
        var estadoAnterior = gestion.Estado;
        var eliminadaAntes = gestion.EstaEliminado;
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [gestion.CentroId],
            centroIdsParaGestion: enCarteraDeGestion ? [gestion.CentroId] : []);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = operacion switch
        {
            "completar" or "reabrir" => await new CompletarGestionCommandHandler(repositorio, alcance, unitOfWork, TimeProvider.System)
                .Handle(new CompletarGestionCommand(gestion.Id, operacion == "completar"), CancellationToken.None),
            "eliminar" => await new EliminarGestionCommandHandler(
                    repositorio, alcance, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"))
                .Handle(new EliminarGestionCommand(gestion.Id), CancellationToken.None),
            // Sin persistencia, el TenantId de la entidad conserva su valor inicial; se usa el mismo
            // en la petición para ejercitar la autorización posterior al filtro explícito de Tenant propietario.
            _ => await new RestaurarGestionCommandHandler(
                    new GestionesContext(gestion), new TenantActualFijo(gestion.TenantId), alcance, unitOfWork)
                .Handle(new RestaurarGestionCommand(gestion.Id), CancellationToken.None),
        };

        resultado.EsExitoso.Should().Be(enCarteraDeGestion);
        unitOfWork.VecesGuardado.Should().Be(enCarteraDeGestion ? 1 : 0);
        if (!enCarteraDeGestion)
        {
            resultado.Error.Codigo.Should().Be("Gestion.NoEncontrada");
            gestion.Estado.Should().Be(estadoAnterior);
            gestion.EstaEliminado.Should().Be(eliminadaAntes);
        }
        else
        {
            gestion.Estado.Should().Be(operacion == "completar" ? EstadoGestion.Completada : EstadoGestion.Pendiente);
            gestion.EstaEliminado.Should().Be(operacion == "eliminar");
        }
    }

    private sealed class GestionesContext(Gestion gestion) : IGestionesQueryContext
    {
        public IQueryable<Gestion> Gestiones => new TestAsyncQueryable<Gestion>(new[] { gestion }.AsQueryable());
    }
}
