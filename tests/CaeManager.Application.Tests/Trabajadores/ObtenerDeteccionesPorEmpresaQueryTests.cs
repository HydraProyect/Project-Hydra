using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class ObtenerDeteccionesPorEmpresaQueryTests
{
    [Fact]
    public async Task Devuelve_las_detecciones_pendientes_dentro_de_la_cartera_de_gestion()
    {
        var empresaId = Guid.NewGuid();
        var contexto = new TrabajadoresQueryContextFalso();
        contexto.ListaDeteccionesTrabajador.Add(
            DeteccionTrabajador.Nuevo(Guid.NewGuid(), empresaId, "Ana", "García", "12345678A"));

        var handler = new ObtenerDeteccionesPorEmpresaQueryHandler(
            contexto, new AsignacionesQueryContextFalso(), new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId]));

        var resultado = await handler.Handle(new ObtenerDeteccionesPorEmpresaQuery(empresaId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().ContainSingle().Which.Dni.Should().Be("12345678A");
    }

    /// <summary>
    /// DATO NUEVO del mockup Gen 2 («Qué se pierde al dar de baja»): la
    /// detección Ausente trae cuántas asignaciones ACTIVAS (FechaBaja == null,
    /// mismo criterio que CierreDeAsignaciones.PorTrabajadorEliminadoAsync) tiene
    /// el trabajador. Una detección Nuevo, que no tiene TrabajadorExistenteId,
    /// siempre trae 0. Una asignación ya cerrada no cuenta.
    /// </summary>
    [Fact]
    public async Task Trae_las_asignaciones_activas_del_trabajador_de_cada_deteccion_ausente()
    {
        var empresaId = Guid.NewGuid();
        var trabajadorConDosActivas = Trabajador.DeEmpresa(empresaId, "Nuria", "Salas Prieto", "11223344B");
        var trabajadorSinActivas = Trabajador.DeEmpresa(empresaId, "Ana", "Cid Puente", "99887766P");

        var contexto = new TrabajadoresQueryContextFalso();
        contexto.ListaDeteccionesTrabajador.Add(DeteccionTrabajador.Ausente(
            Guid.NewGuid(), empresaId, trabajadorConDosActivas.Id, trabajadorConDosActivas.Nombre, trabajadorConDosActivas.Apellidos, trabajadorConDosActivas.Dni!));
        contexto.ListaDeteccionesTrabajador.Add(DeteccionTrabajador.Ausente(
            Guid.NewGuid(), empresaId, trabajadorSinActivas.Id, trabajadorSinActivas.Nombre, trabajadorSinActivas.Apellidos, trabajadorSinActivas.Dni!));
        contexto.ListaDeteccionesTrabajador.Add(
            DeteccionTrabajador.Nuevo(Guid.NewGuid(), empresaId, "Iker", "Mena Ruiz", "12345678Z"));

        var asignaciones = new AsignacionesQueryContextFalso();
        asignaciones.ListaAsignaciones.Add(new Asignacion(trabajadorConDosActivas.Id, Guid.NewGuid(), new DateOnly(2026, 1, 1)));
        asignaciones.ListaAsignaciones.Add(new Asignacion(trabajadorConDosActivas.Id, Guid.NewGuid(), new DateOnly(2026, 2, 1)));
        var cerrada = new Asignacion(trabajadorConDosActivas.Id, Guid.NewGuid(), new DateOnly(2025, 1, 1));
        cerrada.DarDeBaja(new DateOnly(2025, 6, 1));
        asignaciones.ListaAsignaciones.Add(cerrada);

        var handler = new ObtenerDeteccionesPorEmpresaQueryHandler(
            contexto, asignaciones, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId]));

        var resultado = await handler.Handle(new ObtenerDeteccionesPorEmpresaQuery(empresaId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().ContainSingle(d => d.TrabajadorExistenteId == trabajadorConDosActivas.Id)
            .Which.AsignacionesActivas.Should().Be(2, "una asignación ya cerrada no cuenta");
        resultado.Valor.Should().ContainSingle(d => d.TrabajadorExistenteId == trabajadorSinActivas.Id)
            .Which.AsignacionesActivas.Should().Be(0);
        resultado.Valor.Should().ContainSingle(d => d.Tipo == TipoDeteccion.Nuevo)
            .Which.AsignacionesActivas.Should().Be(0, "un alta propuesta no tiene trabajador existente");
    }

    /// <summary>
    /// REC-149: la Empresa está en la cartera de LECTURA del usuario de
    /// portal (rol Cliente) — es una de sus contratistas, y por eso el
    /// portal le enseña su documentación — pero las detecciones no son
    /// documentación de cumplimiento en su relación con el Cliente: son una
    /// herramienta de conciliación de personal de la Empresa entera, con el
    /// DNI de cada trabajador detectado, y su cartera de GESTIÓN es vacía.
    /// Antes del arreglo esta consulta usaba la cartera de lectura como
    /// puerta, y ese mismo usuario podía leer el DNI de trabajadores de la
    /// contratista sin ninguna relación con su propio Cliente.
    /// </summary>
    [Fact]
    public async Task Usuario_de_portal_no_ve_detecciones_de_una_empresa_de_su_cartera_de_lectura()
    {
        var empresaId = Guid.NewGuid();
        var contexto = new TrabajadoresQueryContextFalso();
        contexto.ListaDeteccionesTrabajador.Add(
            DeteccionTrabajador.Nuevo(Guid.NewGuid(), empresaId, "Ana", "García", "12345678A"));

        var handler = new ObtenerDeteccionesPorEmpresaQueryHandler(
            contexto,
            new AsignacionesQueryContextFalso(),
            new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false,
                empresaIdsVisibles: [empresaId],
                empresaIdsParaGestion: []));

        var resultado = await handler.Handle(new ObtenerDeteccionesPorEmpresaQuery(empresaId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
    }
}
