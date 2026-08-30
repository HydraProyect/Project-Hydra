using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// Auditoría Módulo 5, hallazgo crítico 4/9 (extendido a Asignar/DesasignarTecnico,
/// hallazgo 6/9): las escrituras sobre Proyecto solo dependían del filtro de
/// tenant del repositorio, no de la cartera del actor — un IDOR de escritura
/// dentro del mismo tenant. Cada test aquí reproduce exactamente el mismo
/// escenario: un proyecto real, pero fuera de la cartera visible del actor.
/// </summary>
public class ProyectoAutorizacionEscrituraTests
{
    private static Proyecto CrearProyecto(Guid clienteId) =>
        Proyecto.Crear(clienteId, Guid.NewGuid(), "Ampliación Planta 2", new DateOnly(2026, 1, 1), null, null);

    private static AlcanceDatosServiceFalso AlcanceSinAcceso(Guid? clienteVisible = null) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: clienteVisible is { } id ? [id] : []);

    [Fact]
    public async Task Actualizar_un_proyecto_fuera_de_cartera_falla_sin_tocarlo()
    {
        var proyecto = CrearProyecto(Guid.NewGuid());
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarProyectoCommandHandler(repositorio, AlcanceSinAcceso(), unitOfWork);

        var resultado = await handler.Handle(
            new ActualizarProyectoCommand(proyecto.Id, "Nombre cambiado", null, null, proyecto.Version),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        proyecto.Nombre.Should().Be("Ampliación Planta 2");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Cerrar_un_proyecto_fuera_de_cartera_falla_sin_tocarlo()
    {
        var proyecto = CrearProyecto(Guid.NewGuid());
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CerrarProyectoCommandHandler(repositorio, AlcanceSinAcceso(), unitOfWork);

        var resultado = await handler.Handle(
            new CerrarProyectoCommand(proyecto.Id, new DateOnly(2026, 6, 1)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        proyecto.EstaAbierto.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Eliminar_un_proyecto_fuera_de_cartera_falla_sin_tocarlo()
    {
        var proyecto = CrearProyecto(Guid.NewGuid());
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarProyectoCommandHandler(
            repositorio, AlcanceSinAcceso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarProyectoCommand(proyecto.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Asignar_tecnico_a_un_proyecto_fuera_de_cartera_falla()
    {
        var proyecto = CrearProyecto(Guid.NewGuid());
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "García", "77189989B");
        var trabajadoresContext = new TrabajadoresQueryContextFalso();
        trabajadoresContext.ListaTrabajadores.Add(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AsignarTecnicoProyectoCommandHandler(
            new ProyectoTecnicoRepositorioFalso(), proyectos, trabajadoresContext, AlcanceSinAcceso(), unitOfWork);

        var resultado = await handler.Handle(
            new AsignarTecnicoProyectoCommand(proyecto.Id, trabajador.Id, new DateOnly(2026, 1, 10)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Asignar_un_tecnico_fuera_de_cartera_a_un_proyecto_visible_falla()
    {
        // El proyecto es visible, pero el trabajador NO — no basta con que
        // exista: "secuestrarlo" hacia esta cartera lo introduciría en el
        // alcance visible del actor en cuanto quedara asignado.
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyecto(clienteId);
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "García", "77189989B");
        var trabajadoresContext = new TrabajadoresQueryContextFalso();
        trabajadoresContext.ListaTrabajadores.Add(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [clienteId], trabajadorIdsVisibles: []);
        var handler = new AsignarTecnicoProyectoCommandHandler(
            new ProyectoTecnicoRepositorioFalso(), proyectos, trabajadoresContext, alcance, unitOfWork);

        var resultado = await handler.Handle(
            new AsignarTecnicoProyectoCommand(proyecto.Id, trabajador.Id, new DateOnly(2026, 1, 10)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.TrabajadorNoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Desasignar_un_tecnico_de_un_proyecto_fuera_de_cartera_falla()
    {
        var proyecto = CrearProyecto(Guid.NewGuid());
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var proyectoTecnico = new ProyectoTecnico(proyecto.Id, Guid.NewGuid(), new DateOnly(2026, 1, 10));
        var proyectosTecnicos = new ProyectoTecnicoRepositorioFalso();
        proyectosTecnicos.Agregar(proyectoTecnico);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new DesasignarTecnicoProyectoCommandHandler(
            proyectosTecnicos, proyectos, AlcanceSinAcceso(), unitOfWork);

        var resultado = await handler.Handle(
            new DesasignarTecnicoProyectoCommand(proyectoTecnico.Id, new DateOnly(2026, 2, 1)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.TecnicoNoEncontrado");
        proyectoTecnico.EstaActivo.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Actualizar_un_proyecto_dentro_de_cartera_funciona()
    {
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyecto(clienteId);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ActualizarProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(
            new ActualizarProyectoCommand(proyecto.Id, "Nombre cambiado", null, null, proyecto.Version),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        proyecto.Nombre.Should().Be("Nombre cambiado");
    }
}
