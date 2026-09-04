using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
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
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId]));

        var resultado = await handler.Handle(new ObtenerDeteccionesPorEmpresaQuery(empresaId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().ContainSingle().Which.Dni.Should().Be("12345678A");
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
            new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false,
                empresaIdsVisibles: [empresaId],
                empresaIdsParaGestion: []));

        var resultado = await handler.Handle(new ObtenerDeteccionesPorEmpresaQuery(empresaId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
    }
}
