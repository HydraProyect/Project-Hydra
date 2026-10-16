using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Commands.EditarContactoVisita;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Editar el contacto de la Visita desde Visita 360: mismo alcance de GESTIÓN, misma regla de cancelada y misma
/// concurrencia optimista que <c>EditarVisitaCommand</c>. El aislamiento entre Tenants se prueba contra Postgres real
/// en <c>ContactoDeVisitaBajoRlsTests</c>.
/// </summary>
public class EditarContactoVisitaCommandHandlerTests
{
    private static Visita CrearVisita()
    {
        var visita = new Visita(Guid.NewGuid(), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), null);
        visita.RellenarContactoDesdeCentro("Pedro Salas");
        return visita;
    }

    private static (EditarContactoVisitaCommandHandler Handler, UnitOfWorkFalso UnitOfWork) CrearHandler(
        Visita visita, AlcanceDatosServiceFalso? alcance = null)
    {
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        return (new EditarContactoVisitaCommandHandler(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork), unitOfWork);
    }

    [Fact]
    public async Task Cambia_el_contacto_por_otra_persona()
    {
        var visita = CrearVisita();
        var (handler, unitOfWork) = CrearHandler(visita);

        var resultado = await handler.Handle(
            new EditarContactoVisitaCommand(visita.Id, visita.Version, "Lucía Marín", "954555022", "lucia@centro.example"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        visita.ContactoNombre.Should().Be("Lucía Marín");
        visita.ContactoTelefono.Should().Be("954555022");
        visita.ContactoCorreo.Should().Be("lucia@centro.example");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Fuera_del_alcance_de_gestion_responde_no_encontrada_sin_revelar_la_version()
    {
        var visita = CrearVisita();
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [visita.CentroId], centroIdsParaGestion: []);
        var (handler, unitOfWork) = CrearHandler(visita, alcance);

        var resultado = await handler.Handle(
            new EditarContactoVisitaCommand(visita.Id, Guid.NewGuid(), "Otra persona", null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada",
            "ver el Centro no basta para editar, y fuera de alcance no se distingue de que no exista");
        visita.ContactoNombre.Should().Be("Pedro Salas");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_visita_inexistente_responde_no_encontrada()
    {
        var (handler, _) = CrearHandler(CrearVisita());

        var resultado = await handler.Handle(
            new EditarContactoVisitaCommand(Guid.NewGuid(), Guid.NewGuid(), "Otra persona", null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
    }

    [Fact]
    public async Task Una_visita_cancelada_no_se_modifica()
    {
        var visita = CrearVisita();
        visita.Cancelar(DateTime.UtcNow, null);
        var (handler, unitOfWork) = CrearHandler(visita);

        var resultado = await handler.Handle(
            new EditarContactoVisitaCommand(visita.Id, visita.Version, "Otra persona", null, null), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.Cancelada");
        visita.ContactoNombre.Should().Be("Pedro Salas");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_version_desfasada_no_pisa_el_contacto()
    {
        var visita = CrearVisita();
        var (handler, unitOfWork) = CrearHandler(visita);

        var resultado = await handler.Handle(
            new EditarContactoVisitaCommand(visita.Id, Guid.NewGuid(), "Otra persona", null, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        visita.ContactoNombre.Should().Be("Pedro Salas");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("no-es-un-correo", false)]
    [InlineData("persona@centro.example", true)]
    [InlineData("", true)]
    public void El_validador_exige_un_correo_valido_solo_si_se_informa(string correo, bool valido)
    {
        var resultado = new EditarContactoVisitaCommandValidator().Validate(
            new EditarContactoVisitaCommand(Guid.NewGuid(), Guid.NewGuid(), "Persona", null, correo));

        resultado.IsValid.Should().Be(valido);
    }
}
