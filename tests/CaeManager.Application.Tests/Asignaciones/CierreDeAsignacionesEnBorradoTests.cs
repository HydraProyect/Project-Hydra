using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Tests.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Trabajadores;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Asignaciones;

/// <summary>
/// <b>Ninguna de las cinco rutas de borrado deja una asignación activa
/// colgando de un extremo muerto.</b>
///
/// <para>
/// El caso mínimo que motivó esto son tres clics —crear centro, asignar
/// trabajador, borrar centro— y no lo veía nadie porque el filtro global
/// esconde el centro borrado: la asignación seguía con
/// <c>FechaBaja IS NULL</c> y el sistema no podía observar la violación.
/// Hay un test <b>por ruta</b> a propósito: son cinco caminos distintos y un
/// solo test sobre uno de ellos habría dejado los otros cuatro sin cubrir,
/// que es exactamente como el inventario de F5 se quedó en cuatro rutas
/// cuando eran cinco.
/// </para>
/// </summary>
public class CierreDeAsignacionesEnBorradoTests
{
    private static readonly DateOnly Alta = new(2026, 1, 15);

    private static Centro CrearCentro() => new(Guid.NewGuid(), Guid.NewGuid(), "Planta de Getafe");

    private static Trabajador CrearTrabajador(string dni = "12345678Z") => Trabajador.DeEmpresa(
        Guid.NewGuid(), "Manuel", "Moreno Domínguez", dni, new DateOnly(1985, 4, 12), null, null, null);

    private static Asignacion Asignar(AsignacionRepositorioFalso repositorio, Guid trabajadorId, Guid centroId)
    {
        var asignacion = new Asignacion(trabajadorId, centroId, Alta);
        repositorio.Agregar(asignacion);
        return asignacion;
    }

    // ---------- Centro ----------

    [Fact]
    public async Task Eliminar_un_centro_cierra_sus_asignaciones_activas()
    {
        var centro = CrearCentro();
        var centros = new CentroRepositorioFalso();
        centros.Agregar(centro);
        var asignaciones = new AsignacionRepositorioFalso();
        var asignacion = Asignar(asignaciones, Guid.NewGuid(), centro.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarCentroCommandHandler(
            centros, asignaciones, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarCentroCommand(centro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        centro.EstaEliminado.Should().BeTrue();
        asignacion.EstaActiva.Should().BeFalse("una asignación no sobrevive al centro que la sostiene");
    }

    [Fact]
    public async Task Eliminar_un_centro_no_toca_las_asignaciones_de_otro_centro()
    {
        var centro = CrearCentro();
        var otroCentro = CrearCentro();
        var centros = new CentroRepositorioFalso();
        centros.Agregar(centro);
        centros.Agregar(otroCentro);
        var asignaciones = new AsignacionRepositorioFalso();
        var ajena = Asignar(asignaciones, Guid.NewGuid(), otroCentro.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarCentroCommandHandler(
            centros, asignaciones, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        await handler.Handle(new EliminarCentroCommand(centro.Id), CancellationToken.None);

        ajena.EstaActiva.Should().BeTrue();
    }

    [Fact]
    public async Task Eliminar_un_centro_fuera_de_cartera_no_cierra_nada()
    {
        // El cierre va DESPUÉS del guard de alcance: si el borrado no ocurre,
        // la asignación tampoco puede cerrarse.
        var centro = CrearCentro();
        var centros = new CentroRepositorioFalso();
        centros.Agregar(centro);
        var asignaciones = new AsignacionRepositorioFalso();
        var asignacion = Asignar(asignaciones, Guid.NewGuid(), centro.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: []);
        var handler = new EliminarCentroCommandHandler(
            centros, asignaciones, alcance, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarCentroCommand(centro.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        asignacion.EstaActiva.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Eliminar_centros_en_lote_cierra_las_asignaciones_de_todos()
    {
        var primero = CrearCentro();
        var segundo = CrearCentro();
        var centros = new CentroRepositorioFalso();
        centros.Agregar(primero);
        centros.Agregar(segundo);
        var asignaciones = new AsignacionRepositorioFalso();
        var unaDelPrimero = Asignar(asignaciones, Guid.NewGuid(), primero.Id);
        var unaDelSegundo = Asignar(asignaciones, Guid.NewGuid(), segundo.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarCentrosCommandHandler(
            centros, asignaciones, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(
            new EliminarCentrosCommand([primero.Id, segundo.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        unaDelPrimero.EstaActiva.Should().BeFalse();
        unaDelSegundo.EstaActiva.Should().BeFalse();
    }

    // ---------- Trabajador ----------

    [Fact]
    public async Task Eliminar_un_trabajador_cierra_sus_asignaciones_activas()
    {
        var trabajador = CrearTrabajador();
        var trabajadores = new TrabajadorRepositorioFalso();
        trabajadores.Agregar(trabajador);
        var asignaciones = new AsignacionRepositorioFalso();
        var asignacion = Asignar(asignaciones, trabajador.Id, Guid.NewGuid());
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarTrabajadorCommandHandler(
            trabajadores, asignaciones, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(
            new EliminarTrabajadorCommand(trabajador.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacion.EstaActiva.Should().BeFalse();
    }

    [Fact]
    public async Task Eliminar_trabajadores_en_lote_cierra_las_asignaciones_de_todos()
    {
        var primero = CrearTrabajador("12345678Z");
        var segundo = CrearTrabajador("87654321X");
        var trabajadores = new TrabajadorRepositorioFalso();
        trabajadores.Agregar(primero);
        trabajadores.Agregar(segundo);
        var asignaciones = new AsignacionRepositorioFalso();
        var unaDelPrimero = Asignar(asignaciones, primero.Id, Guid.NewGuid());
        var unaDelSegundo = Asignar(asignaciones, segundo.Id, Guid.NewGuid());
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarTrabajadoresCommandHandler(
            trabajadores, asignaciones, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(
            new EliminarTrabajadoresCommand([primero.Id, segundo.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unaDelPrimero.EstaActiva.Should().BeFalse();
        unaDelSegundo.EstaActiva.Should().BeFalse();
    }

    // ---------- La quinta ruta, la que el inventario de F5 no listaba ----------

    [Fact]
    public async Task Resolver_una_deteccion_ausente_desactivando_cierra_las_asignaciones()
    {
        var empresaId = Guid.NewGuid();
        var trabajador = Trabajador.DeEmpresa(empresaId, "Pedro", "Gómez Ruiz", "77189989B");
        var deteccion = DeteccionTrabajador.Ausente(
            Guid.NewGuid(), empresaId, trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni);

        var detecciones = new DeteccionTrabajadorRepositorioFalso();
        detecciones.Agregar(deteccion);
        var trabajadores = new TrabajadorRepositorioFalso();
        trabajadores.Agregar(trabajador);
        var asignaciones = new AsignacionRepositorioFalso();
        var asignacion = Asignar(asignaciones, trabajador.Id, Guid.NewGuid());
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ResolverDeteccionAusenteCommandHandler(
            detecciones, trabajadores, asignaciones, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new ResolverDeteccionAusenteCommand(deteccion.Id, Desactivar: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.EstaEliminado.Should().BeTrue();
        asignacion.EstaActiva.Should().BeFalse();
    }

    [Fact]
    public async Task Resolver_una_deteccion_ausente_manteniendo_no_cierra_nada()
    {
        // "Mantenido" no borra al trabajador —solo falta en ese documento—,
        // así que sus asignaciones siguen vivas. El cierre está atado al
        // borrado, no a la resolución de la detección.
        var empresaId = Guid.NewGuid();
        var trabajador = Trabajador.DeEmpresa(empresaId, "Pedro", "Gómez Ruiz", "77189989B");
        var deteccion = DeteccionTrabajador.Ausente(
            Guid.NewGuid(), empresaId, trabajador.Id, trabajador.Nombre, trabajador.Apellidos, trabajador.Dni);

        var detecciones = new DeteccionTrabajadorRepositorioFalso();
        detecciones.Agregar(deteccion);
        var trabajadores = new TrabajadorRepositorioFalso();
        trabajadores.Agregar(trabajador);
        var asignaciones = new AsignacionRepositorioFalso();
        var asignacion = Asignar(asignaciones, trabajador.Id, Guid.NewGuid());
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new ResolverDeteccionAusenteCommandHandler(
            detecciones, trabajadores, asignaciones, new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new ResolverDeteccionAusenteCommand(deteccion.Id, Desactivar: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.EstaEliminado.Should().BeFalse();
        asignacion.EstaActiva.Should().BeTrue();
    }
}
