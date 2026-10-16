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

    [Theory]
    [InlineData("accesos@planta.example", true)]
    [InlineData("a@b", true)]
    [InlineData("Pedro accesos@planta.example", false)]
    [InlineData("@planta.example", false)]
    [InlineData("accesos@", false)]
    [InlineData("a@b@planta.example", false)]
    [InlineData("Pedro Salas", false)]
    public void El_contacto_copiado_del_Centro_pasa_la_misma_validacion_que_la_edicion(string contactoCentro, bool vaAlCorreo)
    {
        var visita = new Visita(Guid.NewGuid(), new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), null);
        visita.RellenarContactoDesdeCentro(contactoCentro);

        (visita.ContactoCorreo is not null).Should().Be(vaAlCorreo);
        new EditarContactoVisitaCommandValidator()
            .Validate(new EditarContactoVisitaCommand(visita.Id, visita.Version, visita.ContactoNombre, visita.ContactoTelefono, visita.ContactoCorreo))
            .IsValid.Should().BeTrue("lo que se copia del Centro tiene que poder volver a guardarse tal cual desde Visita 360");
    }

    [Theory]
    [InlineData("Pedro accesos@planta.example")]
    [InlineData("accesos@")]
    [InlineData("a@b@planta.example")]
    public void La_edicion_rechaza_un_correo_que_la_copia_no_aceptaria(string correo)
    {
        new EditarContactoVisitaCommandValidator()
            .Validate(new EditarContactoVisitaCommand(Guid.NewGuid(), Guid.NewGuid(), null, null, correo))
            .IsValid.Should().BeFalse();
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
