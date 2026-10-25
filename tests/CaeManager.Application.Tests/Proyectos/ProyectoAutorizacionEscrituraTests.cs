using CaeManager.Application.Common;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Common;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asignar_tecnico_exige_un_proyecto_abierto(bool cerrado)
    {
        var proyecto = CrearProyecto(Guid.NewGuid());
        if (cerrado) proyecto.Cerrar(new DateOnly(2026, 2, 1));
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "Garcia", "77189989B");
        var contexto = new TrabajadoresQueryContextFalso();
        contexto.ListaTrabajadores.Add(trabajador);
        var tecnicos = new ProyectoTecnicoRepositorioFalso();
        var guardado = new UnitOfWorkFalso();
        var handler = new AsignarTecnicoProyectoCommandHandler(
            tecnicos, proyectos, contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true), guardado);

        var resultado = await handler.Handle(
            new AsignarTecnicoProyectoCommand(proyecto.Id, trabajador.Id, new DateOnly(2026, 1, 10)), CancellationToken.None);

        resultado.EsExitoso.Should().Be(!cerrado);
        if (cerrado) resultado.Error.Codigo.Should().Be("Proyecto.YaCerrado");
        tecnicos.ProyectosTecnicos.Should().HaveCount(cerrado ? 0 : 1);
        guardado.VecesGuardado.Should().Be(cerrado ? 0 : 1);
    }

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
    public async Task Asignar_tecnico_a_un_proyecto_cerrado_fuera_de_cartera_no_revela_que_existe()
    {
        // La guarda de «proyecto cerrado» va DESPUÉS de la visibilidad: un actor
        // sin acceso recibe NoEncontrado, no YaCerrado (que confirmaría que existe).
        var proyecto = CrearProyecto(Guid.NewGuid());
        proyecto.Cerrar(new DateOnly(2026, 2, 1));
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
        resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
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

    // ---------- Hueco arquitectónico: concurrencia optimista ----------

    [Fact]
    public async Task Cerrar_un_proyecto_con_una_version_obsoleta_falla()
    {
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyecto(clienteId);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CerrarProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        // Cualquier Guid que no sea el vigente representa la versión que la
        // segunda persona tenía en pantalla — mismo patrón que
        // EditarClienteConcurrenciaTests.
        var versionObsoleta = Guid.NewGuid();
        versionObsoleta.Should().NotBe(proyecto.Version);

        var resultado = await handler.Handle(
            new CerrarProyectoCommand(proyecto.Id, new DateOnly(2026, 6, 1), versionObsoleta), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        proyecto.EstaAbierto.Should().BeTrue("el cierre con versión obsoleta no debe aplicarse");
    }

    [Fact]
    public async Task Desasignar_un_tecnico_con_una_version_obsoleta_falla()
    {
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyecto(clienteId);
        var proyectos = new ProyectoRepositorioFalso();
        proyectos.Agregar(proyecto);
        var proyectoTecnico = new ProyectoTecnico(proyecto.Id, Guid.NewGuid(), new DateOnly(2026, 1, 10));
        var proyectosTecnicos = new ProyectoTecnicoRepositorioFalso();
        proyectosTecnicos.Agregar(proyectoTecnico);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new DesasignarTecnicoProyectoCommandHandler(
            proyectosTecnicos, proyectos, AlcanceSinAcceso(clienteId), unitOfWork);

        var versionObsoleta = Guid.NewGuid();
        versionObsoleta.Should().NotBe(proyectoTecnico.Version);

        var resultado = await handler.Handle(
            new DesasignarTecnicoProyectoCommand(proyectoTecnico.Id, new DateOnly(2026, 2, 1), versionObsoleta),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        proyectoTecnico.EstaActivo.Should().BeTrue("la baja con versión obsoleta no debe aplicarse");
    }

    // ---------- Reabrir (FS-12: el cierre tiene salida) ----------

    private static Proyecto CrearProyectoCerrado(Guid clienteId)
    {
        var proyecto = CrearProyecto(clienteId);
        proyecto.Cerrar(new DateOnly(2026, 6, 1));
        return proyecto;
    }

    [Fact]
    public async Task Reabrir_un_proyecto_cerrado_de_la_cartera_lo_deja_abierto()
    {
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyectoCerrado(clienteId);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(proyecto.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        proyecto.EstaAbierto.Should().BeTrue();
        proyecto.FechaCierreReal.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    /// <summary>
    /// Un evento cerrado (a mano o por el cierre automático, decisión 2026-10-16) con la fecha de fin ya pasada no se
    /// reabre: el proceso programado lo volvería a cerrar en el pase siguiente. El usuario cambia antes la fecha de fin.
    /// </summary>
    [Fact]
    public async Task Reabrir_un_evento_con_la_fecha_de_fin_pasada_falla_sin_tocarlo()
    {
        var clienteId = Guid.NewGuid();
        var hoy = DiaDeNegocio.Hoy();
        var evento = Proyecto.Crear(clienteId, Guid.NewGuid(), "Partido", hoy.AddDays(-3), hoy.AddDays(-1), null, TipoProyecto.Evento);
        evento.CerrarEventoTerminado(hoy).Should().BeTrue("precondición: el evento terminó ayer");
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(evento);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(evento.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        resultado.Error.Codigo.Should().Be("Proyecto.EventoFinPasado");
        evento.EstaAbierto.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Reabrir_un_evento_terminado_con_una_nueva_fecha_de_fin_futura_lo_deja_abierto_con_esa_fecha()
    {
        var clienteId = Guid.NewGuid();
        var hoy = DiaDeNegocio.Hoy();
        var evento = Proyecto.Crear(clienteId, Guid.NewGuid(), "Partido", hoy.AddDays(-3), hoy.AddDays(-1), null, TipoProyecto.Evento);
        evento.CerrarEventoTerminado(hoy);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(evento);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(evento.Id, NuevaFechaFinPrevista: hoy.AddDays(4)), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        evento.EstaAbierto.Should().BeTrue();
        evento.FechaFinPrevista.Should().Be(hoy.AddDays(4));
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(-1, "Proyecto.EventoFinPasado")]
    [InlineData(-10, "Proyecto.FechaFinAnteriorAlInicio")]
    public async Task Reabrir_un_evento_con_una_nueva_fecha_no_valida_falla_sin_tocarlo(int dias, string codigo)
    {
        var clienteId = Guid.NewGuid();
        var hoy = DiaDeNegocio.Hoy();
        var evento = Proyecto.Crear(clienteId, Guid.NewGuid(), "Partido", hoy.AddDays(-3), hoy.AddDays(-2), null, TipoProyecto.Evento);
        evento.CerrarEventoTerminado(hoy);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(evento);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(evento.Id, NuevaFechaFinPrevista: hoy.AddDays(dias)), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(codigo);
        evento.EstaAbierto.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Reabrir_un_evento_cuya_fecha_de_fin_es_hoy_lo_deja_abierto()
    {
        var clienteId = Guid.NewGuid();
        var hoy = DiaDeNegocio.Hoy();
        var evento = Proyecto.Crear(clienteId, Guid.NewGuid(), "Partido", hoy.AddDays(-3), hoy, null, TipoProyecto.Evento);
        evento.Cerrar(hoy.AddDays(-1));
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(evento);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(evento.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        evento.EstaAbierto.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Reabrir_un_proyecto_fuera_de_cartera_falla_sin_tocarlo()
    {
        var proyecto = CrearProyectoCerrado(Guid.NewGuid());
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(proyecto.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.NoEncontrado");
        proyecto.EstaAbierto.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Reabrir_un_proyecto_con_una_version_obsoleta_falla()
    {
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyectoCerrado(clienteId);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(proyecto.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        proyecto.EstaAbierto.Should().BeFalse("la reapertura con versión obsoleta no debe aplicarse");
    }

    [Fact]
    public async Task Reabrir_un_proyecto_ya_abierto_falla_sin_guardar()
    {
        var clienteId = Guid.NewGuid();
        var proyecto = CrearProyecto(clienteId);
        var repositorio = new ProyectoRepositorioFalso();
        repositorio.Agregar(proyecto);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ReabrirProyectoCommandHandler(repositorio, AlcanceSinAcceso(clienteId), unitOfWork);

        var resultado = await handler.Handle(new ReabrirProyectoCommand(proyecto.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.YaAbierto");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
