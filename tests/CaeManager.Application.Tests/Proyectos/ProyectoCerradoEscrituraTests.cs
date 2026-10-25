using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.RequisitosEvento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Proyectos;

public class ProyectoCerradoEscrituraTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Desasignar_tecnico_exige_proyecto_visible_y_abierto(bool cerrado, bool visible)
    {
        var proyecto = CrearProyecto(cerrado);
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var tecnico = new ProyectoTecnico(proyecto.Id, Guid.NewGuid(), new DateOnly(2026, 1, 10));
        var tecnicos = new ProyectoTecnicoRepositorioFalso();
        tecnicos.Agregar(tecnico);
        var guardado = new UnitOfWorkFalso();
        var handler = new DesasignarTecnicoProyectoCommandHandler(tecnicos, proyectos, CrearAlcance(visible), guardado);

        var resultado = await handler.Handle(
            new DesasignarTecnicoProyectoCommand(tecnico.Id, new DateOnly(2026, 2, 1), tecnico.Version), CancellationToken.None);

        resultado.EsExitoso.Should().Be(!cerrado && visible);
        if (!visible) resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        else if (cerrado) resultado.Error.Codigo.Should().Be("Proyecto.YaCerrado");
        tecnico.EstaActivo.Should().Be(cerrado || !visible);
        guardado.VecesGuardado.Should().Be(!cerrado && visible ? 1 : 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Anadir_requisito_exige_evento_visible_y_abierto(bool cerrado, bool visible)
    {
        var proyecto = CrearProyecto(cerrado);
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var tipo = new TipoDocumento("Formación", null, false, 1, AmbitoAplicacion.Trabajador);
        var tipos = new TiposDocumentoQueryContextFalso();
        tipos.ListaTiposDocumento.Add(tipo);
        var requisitos = new RequisitosFalso();
        var guardado = new UnitOfWorkFalso();
        var handler = new AnadirRequisitoEventoCommandHandler(proyectos, requisitos, tipos, CrearAlcance(visible), guardado);

        var resultado = await handler.Handle(new AnadirRequisitoEventoCommand(proyecto.Id, tipo.Id), CancellationToken.None);

        resultado.EsExitoso.Should().Be(!cerrado && visible);
        if (!visible) resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        else if (cerrado) resultado.Error.Codigo.Should().Be("Proyecto.YaCerrado");
        requisitos.Lista.Should().HaveCount(!cerrado && visible ? 1 : 0);
        if (!cerrado && visible) requisitos.Lista.Single().TipoDocumentoId.Should().Be(tipo.Id);
        guardado.VecesGuardado.Should().Be(!cerrado && visible ? 1 : 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Quitar_requisito_exige_evento_visible_y_abierto(bool cerrado, bool visible)
    {
        var proyecto = CrearProyecto(cerrado);
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var requisito = new ProyectoRequisito(proyecto.Id, Guid.NewGuid());
        var requisitos = new RequisitosFalso();
        requisitos.Agregar(requisito);
        var guardado = new UnitOfWorkFalso();
        var handler = new QuitarRequisitoEventoCommandHandler(proyectos, requisitos, CrearAlcance(visible), guardado);

        var resultado = await handler.Handle(
            new QuitarRequisitoEventoCommand(proyecto.Id, requisito.TipoDocumentoId), CancellationToken.None);

        resultado.EsExitoso.Should().Be(!cerrado && visible);
        if (!visible) resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        else if (cerrado) resultado.Error.Codigo.Should().Be("Proyecto.YaCerrado");
        requisitos.Lista.Should().HaveCount(!cerrado && visible ? 0 : 1);
        if (cerrado || !visible) requisitos.Lista.Should().ContainSingle().Which.Should().BeSameAs(requisito);
        guardado.VecesGuardado.Should().Be(!cerrado && visible ? 1 : 0);
    }

    private static Proyecto CrearProyecto(bool cerrado)
    {
        var proyecto = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Evento", new DateOnly(2026, 1, 1),
            new DateOnly(2026, 2, 1), null, TipoProyecto.Evento);
        if (cerrado) proyecto.Cerrar(new DateOnly(2026, 2, 1));
        return proyecto;
    }

    private static AlcanceDatosServiceFalso CrearAlcance(bool visible) =>
        new(tieneAccesoTotal: visible, clienteIdsVisibles: []);

    private sealed class RequisitosFalso : IProyectoRequisitoRepository
    {
        public List<ProyectoRequisito> Lista { get; } = [];
        public Task<IReadOnlyList<ProyectoRequisito>> ListarDeProyectoAsync(Guid proyectoId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProyectoRequisito>>(Lista.Where(r => r.ProyectoId == proyectoId).ToList());
        public void Agregar(ProyectoRequisito requisito) => Lista.Add(requisito);
        public void Eliminar(ProyectoRequisito requisito) => Lista.Remove(requisito);
    }
}
